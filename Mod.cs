using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.Sigscan.Definitions.Structs;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Lets Chapter Select and Fate Episode be entered while an online session is
/// active, so story content can be played co-op.
///
/// This DOES modify game behaviour: it rewrites four bytes of game code in
/// memory while the process runs (never on disk). Both players need the same mod
/// version and the same game version — signatures resolve to build-specific
/// addresses.
///
/// Pressing F8 while the game has focus runs the game's own "unlock controls"
/// routine for this PC's characters. It happens only on that key press, and
/// nothing is sent to the other player.
///
/// While in an online session, the Grandcypher Departs of Chapter 6 and
/// Chapter 8 skip the walk-around town part that follows them (see
/// <see cref="TownParts"/>), because online the host would go there alone.
/// Offline nothing changes. Option SkipTownPartsOnline in probe_config.json.
///
/// Everything else here is read-only logging to probe.log, which is how the gate
/// was originally found and remains the fastest way to see what a session is
/// doing.
/// </summary>
public class Mod : IMod
{
    // ---------------------------------------------------------------- signatures

    // Verified against Endless Ragnarok 2.0.x. Same patterns Nenkai's Discord
    // Rich Presence mod ships; confirmed resolving on 2.0.5.
    //
    // 2.0.6: every signature in this file still matches exactly once, and each
    // hit was compared byte for byte with its 2.0.5 original. The only
    // differences are call and data displacements (code moved), so they are
    // the same functions, not lookalikes.
    private const string SigIsInLobby =
        "48 83 B9 ?? ?? ?? ?? ?? 74 ?? 48 8B 91 ?? ?? ?? ?? 48 8B 81";
    private const string SigGetCurrentQuestId =
        "56 57 48 83 EC ?? 48 89 D6 48 89 CF 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 01 FF 50 ?? 84 C0 75 ?? 48 8B 87";
    private const string SigGetNumPlayers =
        "48 81 EC ?? ?? ?? ?? C5 79 7F B4 24 ?? ?? ?? ?? C5 79 7F 6C 24";

    /// <summary>
    /// Global holding the current quest phase. The match is
    /// <c>mov dword ptr [rip+disp32], imm32</c> (C7 05 …), 10 bytes, so the
    /// global sits at <c>match + 10 + disp32</c>.
    ///
    /// Phase turns out to be effectively quest-level: every section of a quest
    /// shares one phaseNo. It is good for spotting two clients in different
    /// quests, not for locating a stall inside one.
    /// </summary>
    private const string SigPhaseId =
        "C7 05 ?? ?? ?? ?? ?? ?? ?? ?? C5 F9 EF C0 C5 FA 7F 05 ?? ?? ?? ?? C5 FA 7F 05 ?? ?? ?? ?? C7 05";

    /// <summary>
    /// The row-availability gate, in <c>ui::component::MenuQuestCounter</c>
    /// vtable slot 27 — the per-row "is this entry selectable?" query:
    ///
    /// <code>
    /// switch (this->rowKind[index]) {            // this+0x388 + index*4
    ///   case 0,1,5,6,7,8: break;                 // always available
    ///   case 2:  /* separate online-lobby checks */
    ///   case 3: case 4:
    ///       available = (this->onlineFlag == 0); // Chapter Select / Fate Episode
    ///   case 9:  available = sub_1403F0D60(4);
    ///   default: available = 0;
    /// }
    /// *out = available ^ 1;
    /// </code>
    ///
    /// Compiled form, unique in 2.0.5 at RVA 0x3D08713:
    ///
    ///   80 B9 F0 03 00 00 00   cmp  byte [rcx+0x3F0], 0
    ///   40 0F 94 C7            sete dil
    ///
    /// Note the REX prefix on the sete — scanning for a bare 0F 94 finds nothing.
    ///
    /// Slot 30 is a different method that only *explains* a refusal; patching it
    /// suppresses the popup while leaving the row unselectable.
    /// </summary>
    private const string SigStoryOnlineGate =
        "80 B9 F0 03 00 00 00 40 0F 94 C7";

    /// <summary>
    /// First of four consecutive globals holding party-member pointers. Begins
    /// with <c>48 8B 0D disp32</c> (mov rcx,[rip+disp32]), 7 bytes, so the first
    /// global is at <c>match + 7 + disp32</c>.
    ///
    /// These are adjacent globals, NOT an array behind a pointer. Confirmed by
    /// decoding the pattern: it loads 0x147034CC0, then a manager at
    /// 0x147C21D38, then 0x147034CC8 — i.e. first and first+8. An earlier
    /// version dereferenced once too many times and consequently read a constant
    /// "1/4" in every situation, which looked like data but measured nothing.
    ///
    /// Distinguishes "AI companions never spawned" from "AI exist but are not
    /// replicated" — two problems needing opposite fixes.
    /// </summary>
    private const string SigPartyMemberArray =
        "48 8B 0D ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 01 C6 44 24 ?? ?? 89 FA 41 89 F0 41 89 D9 FF 90 ?? ?? ?? ?? 48 8B 0D";

    /// <summary>Offset of the sete within <see cref="SigStoryOnlineGate"/>.</summary>
    private const int GatePatchOffset = 7;

    private static readonly byte[] SeteDil = { 0x40, 0x0F, 0x94, 0xC7 }; // sete dil
    private static readonly byte[] MovDil1 = { 0x40, 0xB7, 0x01, 0x90 }; // mov dil,1 ; nop

    /// <summary>Largest plausible module span, for sanity-checking resolved globals.</summary>
    private const long MaxModuleSpan = 0x10000000;

    // ------------------------------------------------------------------- win32

    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_READABLE = 0x02 | 0x04 | 0x20 | 0x40;  // R, RW, XR, XRW
    private const uint PAGE_GUARD_OR_NOACCESS = 0x100 | 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION buffer, nint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint addr, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static readonly int MbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

    /// <summary>
    /// True if <paramref name="addr"/> is committed and readable for
    /// <paramref name="size"/> bytes.
    ///
    /// This is not optional politeness. AccessViolationException is NOT catchable
    /// in .NET Core, so a try/catch around a bad dereference does nothing and the
    /// game dies. Every speculative read goes through here first.
    /// </summary>
    private static bool Readable(nint addr, int size)
    {
        if (addr == 0) return false;
        if (VirtualQuery(addr, out var mbi, MbiSize) == 0) return false;
        if (mbi.State != MEM_COMMIT) return false;
        if ((mbi.Protect & PAGE_GUARD_OR_NOACCESS) != 0) return false;
        if ((mbi.Protect & PAGE_READABLE) == 0) return false;
        return addr.ToInt64() + size <= mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
    }

    // ------------------------------------------------------------------ hooks

    [Function(CallingConventions.Microsoft)]
    private delegate byte IsInLobbyFn(nint self);

    [Function(CallingConventions.Microsoft)]
    private delegate nint GetCurrentQuestIdFn(nint self, nint outQuestId);

    private IHook<IsInLobbyFn> _isInLobbyHook;
    private IHook<GetCurrentQuestIdFn> _questIdHook;

    // ---------------------------------------------------------- quest signals

    /// <summary>
    /// <c>stage::quest::RecvSignal</c> vtable slot 1 (RVA 0x3219D20 in 2.0.5):
    /// the callback that hands one quest signal to one listening RecvSignal
    /// condition. Located through the class's RTTI vtable; unique in .text.
    ///
    /// Story scripts advance on signals, and many of them come from objects
    /// placed on the map (the boss, gimmicks) rather than from the script.
    /// <c>SendSignal</c> posts into a table local to each PC, with no network
    /// path. The theory under test: signals raised by a boss's scripted
    /// routines exist only on the host, so the guest's script never gets past
    /// them. That is Chapter 7's hole. Logging what each PC receives turns the
    /// theory into a diff of two logs.
    ///
    /// Signal object, from the decompiled callback: +0x08 type, +0x10 extra,
    /// +0x18 object id, +0x20 signal hash. These are the four numbers of an
    /// FSM's <c>signalField_</c>. Listener: +0x28 the signal it waits for,
    /// +0x38 how many it has received.
    /// </summary>
    private const string SigRecvSignalOnSignal =
        "4C 8B 41 28 4D 85 C0 74 21 4D 8B 50 08 4C 8B 0A 4D 3B 51 08 75 14 49 8B 40 20 49 8B 51 20";

    [Function(CallingConventions.Microsoft)]
    private delegate void RecvSignalOnSignalFn(nint listener, nint signalRef);

    private IHook<RecvSignalOnSignalFn> _recvSignalHook;

    // Which signals this PC has seen and used in the current story quest, so
    // each is logged once. Cleared when the quest changes.
    private readonly object _sigLock = new();
    private readonly HashSet<(ulong obj, ulong hash)> _sigSeen = new();
    private readonly HashSet<(ulong obj, ulong hash)> _sigUsed = new();
    private int _sigQuest = -1;

    // ----------------------------------------------------- player input limits

    /// <summary>
    /// <c>stage::quest::SetPlayerInputLimit</c> execute (RVA 0x320BD20 in 2.0.5).
    /// Story scripts call it to lock and unlock the player's controls around
    /// scenes. Node fields: +0x30 <c>actionId_</c> (which control), +0x34
    /// <c>enable_</c> (1 = lock, 0 = unlock), +0x38 <c>charaIndex_</c> (0 = the
    /// whole party, 1-4 one party slot, 5-12 the extra character slots). It sets
    /// or clears bit <c>actionId_</c> of a mask at character+0x17A0, holding the
    /// character's lock at +0x1870.
    ///
    /// The freeze at the start of Chapter 7's chase fits a lock whose unlock
    /// never ran on that PC: the same host-only trigger problem as the hole.
    /// Logging every call turns that into a two-PC diff.
    /// </summary>
    private const string SigSetPlayerInputLimit =
        "41 56 56 57 55 53 48 81 EC 80 00 00 00 48 89 CE 8B 41 38 85 C0 7E 37 83 F8 04 0F 87 AB 00 00 00 FF C8";

    /// <summary>
    /// The game's own "unlock every character" routine (RVA 0x32068D0 in 2.0.5).
    /// No arguments: for each of the 12 character slots it resolves the
    /// character, takes its lock and writes 0 to the mask at +0x17A0. The F8 key
    /// calls this instead of writing memory itself, so the game's locking is
    /// respected. A set bit is a locked control; 0 is the game's own "all free".
    /// </summary>
    private const string SigClearAllInputLimits =
        "56 57 48 83 EC 58 48 8B 05 ?? ?? ?? ?? 48 89 44 24 50 C5 F8 28 05 ?? ?? ?? ?? C5 F8 29 44 24 40 48 8D 4C 24 40 E8 ?? ?? ?? ?? 48 85 C0 74 3C 48 89 C6 48 8D B8 70 18 00 00";

    [Function(CallingConventions.Microsoft)]
    private delegate void SetPlayerInputLimitFn(nint node);

    [Function(CallingConventions.Microsoft)]
    private delegate void ClearAllInputLimitsFn();

    private IHook<SetPlayerInputLimitFn> _inputLimitHook;
    private IHook<ClearAllInputLimitsFn> _clearLimitsHook;

    // Locks this PC's scripts turned on and have not turned off in the current
    // quest, as (charaIndex, actionId). Bookkeeping from the calls seen, not a
    // read of the real masks: a lock on "whole party" and an unlock on one slot
    // stay separate entries. Enough to spot a lock that never came off.
    private readonly object _limitLock = new();
    private readonly SortedSet<(int chara, int action)> _limitsHeld = new();
    private int _limitQuest = -1;
    private long _lastResetLogMs = -100000;
    private int _resetsSinceLog;

    private const int VK_F8 = 0x77;
    private Timer _keyTimer;
    private int _keyPolling;
    private bool _unstuckKeyDown;
    private long _lastUnstuckMs = -100000;
    private long _phaseChangedMs;

    // ------------------------------------------------------- story part chain

    /// <summary>
    /// What happens when a story part ends (quest-system functions, 2.0.5 RVAs;
    /// 2.0.6 is +0x3C0). After a part is cleared, <c>NextPartSearch</c>
    /// (0x62D790) walks the quest list and asks <c>OccurrenceMet</c> (0x62DB10)
    /// whether each story quest's "follows" conditions now hold. The first one
    /// that passes is started with <c>StartQuest</c> (0x62E860); if none does,
    /// <c>QuestEnd</c> (0x62EBE0) sends the player to a town, the one it
    /// computes into questSystem+0x63A54.
    ///
    /// Only two Grandcypher Departs lead into a walk-around town part rather
    /// than a mission: Chapter 6 (104F00 into 104000) and Chapter 8 (106F00
    /// into 106000). Online, the host goes into the town part alone and the
    /// other player is sent back to the lobby town. That is the "town split".
    /// </summary>
    private const string SigNextPartSearch =
        "41 57 41 56 41 55 41 54 56 57 55 53 48 83 EC 48 88 54 24 47";
    private const string SigOccurrenceMet =
        "55 41 57 41 56 41 55 41 54 56 57 53 48 83 EC 68 48 8D 6C 24 60 48 C7 45 00 FE FF FF FF 48 89 D7 49 89 CB 48";
    private const string SigStartQuest =
        "41 57 41 56 41 55 41 54 56 57 55 53 48 83 EC 48 44 89 CB 44 89 C6 48 89";
    private const string SigQuestEnd =
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC 98 00 00 00 48 8D AC 24 80 00 00 00 48 C7 45 10 FE FF FF FF 45 89 CF 45 89";

    // Every parameter is taken at full register width and forwarded untouched,
    // so the hooks cannot narrow or reinterpret anything the game passes.
    [Function(CallingConventions.Microsoft)]
    private delegate void NextPartSearchFn(nint questSys, nint arg);
    [Function(CallingConventions.Microsoft)]
    private delegate nint OccurrenceMetFn(nint questSys, nint questRecord, nint arg);
    [Function(CallingConventions.Microsoft)]
    private delegate void StartQuestFn(nint questSys, nint questIdPtr, nint section, nint a4, nint a5, nint a6);
    [Function(CallingConventions.Microsoft)]
    private delegate nint QuestEndFn(nint questSys, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8);

    private IHook<NextPartSearchFn> _nextPartHook;
    private IHook<OccurrenceMetFn> _occurrenceHook;
    private IHook<StartQuestFn> _startQuestHook;
    private IHook<QuestEndFn> _questEndHook;

    // The part just cleared, set only while NextPartSearch runs on this thread,
    // so the overrides below can never touch any other caller of OccurrenceMet
    // (quest lists and menus ask it the same question).
    [ThreadStatic] private static int t_chainFrom;

    private const int QuestSysReplayState = 0xDD4;   // 2 or 4: Chapter Select replay
    private const int QuestSysReturnTown = 0x63A54;  // map QuestEnd sends the player to
    private const int QuestRecordId = 0x8;

    // Town parts that Grandcypher Departs lead into, and where to go instead
    // while online. 0 means "no part": fall through to the normal quest end.
    private static readonly Dictionary<int, (int ship, int skipTo)> TownParts = new()
    {
        // Chapter 8: ship -> town part -> first mission. 106001 is a Chapter
        // Select entry, so it is known to start cleanly on its own.
        [0x106000] = (0x106F00, 0x106001),
        // Chapter 6: ship -> town part -> another ship part (104F01), which is
        // not a Chapter Select entry. Skipping into it is untested, so only the
        // town part is blocked; both players go back to town.
        [0x104000] = (0x104F00, 0),
    };

    private volatile bool _skipTownParts = true;

    // ---------------------------------------------------------- UI lobby steps

    /// <summary>
    /// The pause menu's "Return to town" in a story area, while online, runs
    /// <c>SetQuestExitLeaveLobbyFlag</c> and the flow <c>LeaveLobbyAndEndMultiplay</c>
    /// (<c>LeaveLobby</c> then <c>EndOnlineAndMulti</c>), then <c>JumpPhaseTown</c>
    /// (system/fsm/ui/ui_pausemenu_stage). That drops the session on purpose.
    /// These are the four UI actions' Execute (vtable slot 9), hooked to log
    /// only: each one runs unchanged.
    /// </summary>
    private const string SigUiSetQuestExitLeaveLobbyFlag =
        "48 8B 05 ?? ?? ?? ?? C6 80 3D C8 06 00 01 C3";
    private const string SigUiLeaveLobby =
        "56 48 83 EC 30 48 8B 05 ?? ?? ?? ?? 8B 40 04 83 F8 03";
    private const string SigUiEndOnlineAndMulti =
        "48 8B 05 ?? ?? ?? ?? 8B 40 04 83 F8 03 75 0B 48 8B 05 ?? ?? ?? ?? C6 40 09 01 C3";
    private const string SigUiJumpPhaseTown =
        "55 48 83 EC 50 48 8D 6C 24 50 48 C7 45 F8 FE FF FF FF 48 89";

    [Function(CallingConventions.Microsoft)]
    private delegate void UiActionFn(nint self, nint arg);

    private readonly Dictionary<string, IHook<UiActionFn>> _uiHooks = new();

    // ------------------------------------------------------------------ state

    private ILogger _log;
    private IReloadedHooks _hooks;
    private nint _baseAddr;

    private readonly object _logLock = new();
    private StreamWriter _file;
    private string _logPath;

    // Written only by the IsInLobby hook thread, read by the timer thread. A
    // torn read would only mis-print a diagnostic counter, never misbehave.
    private long _lobbyCalls;
    private volatile int _lastLobbyResult = -1;
    private volatile int _lastQuestId = -1;

    // Validated-pointer cache for the quest-id out param. The game passes the
    // same buffer over and over, so this pays for VirtualQuery once rather than
    // on every call of a hot hook.
    private nint _lastQuestOutPtr;
    private bool _lastQuestOutOk;

    private const int PartySlots = 4;
    private nint _partyArrayAddr;
    // Starts at "n/a" — the true initial state, before the array is resolved.
    // Starting at "" made the first tick log a meaningless 'n/a -> n/a'.
    private string _lastParty = "n/a";

    private nint _phaseIdAddr;
    private int _lastPhase = int.MinValue;

    // Whether the first real quest of the session has been seen. On boot the
    // game loads the quest the save resumes in, briefly, before the town. That
    // first load is the save's story point; anything after it is the player's
    // own pick. Both players comparing that one line is the fastest way to
    // check the matched-progress rule the mod depends on.
    private bool _bootQuestSeen;

    // Filled party slots from the last read, so the hang check can ask "is a
    // second player actually here?" without re-walking the party globals.
    private volatile int _partyFilled;

    // Per-slot pointer fingerprints, so a party *swap* is visible. Set by
    // ReadPartySlots; compared in Tick.
    private string _partyComp = "n/a";
    private string _lastPartyComp = "n/a";

    // When the current quest was entered, so the QUEST CHANGE line can report
    // how long the previous one was held. That is time-in-quest, which includes
    // time spent playing it — it is NOT a measure of how long a transition took,
    // and reading it that way once produced a wrong conclusion.
    private long _questEnteredMs;


    private readonly object _patchLock = new();
    private nint _gateAddr;
    private byte[] _gateOriginal;
    private bool _unlockApplied;
    private bool _unlockWanted;
    private bool _unlockLogged;

    private string _configPath;
    private DateTime _configStamp = DateTime.MinValue;
    private Timer _tickTimer;

    private static readonly Regex SkipTownFalse = new(
        "\"SkipTownPartsOnline\"\\s*:\\s*false",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnlockTrue = new(
        "\"UnlockStoryWhileOnline\"\\s*:\\s*true",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastHeartbeatMs;
    private int _ticking;

    private const long MaxLogBytes = 2 * 1024 * 1024;

    // ------------------------------------------------------------------ start

    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 modConfig)
    {
        var loader = (IModLoader)loaderApi;
        _log = (ILogger)loader.GetLogger();
        _baseAddr = Process.GetCurrentProcess().MainModule!.BaseAddress;

        var modDir = loader.GetDirectoryForModId(modConfig.ModId);

        // Config path is set BEFORE the log is opened. Previously both lived in
        // one try block, so a log-file failure silently disabled config loading
        // too — the unlock would never apply and nothing would say why.
        _configPath = Path.Combine(modDir, "probe_config.json");
        _logPath = Path.Combine(modDir, "probe.log");
        OpenLog();

        // Self-identifying header. probe.log travels with the mod folder, so a
        // copied install carries the other machine's history; the machine tag
        // and module base make it obvious which sessions belong to which PC.
        Write($"=== session {DateTime.Now:yyyy-MM-dd HH:mm:ss} — mod v{modConfig.ModVersion} ===");
        Write($"machine: {MachineTag(modDir)}   game: {GameVersion()}");

        // Logs get shared - with friends, in bug reports, publicly - and the PC's
        // real name has no business in them. The tag hashes the machine name
        // with 16 random bytes that never leave this folder, so it still differs
        // per PC (a copied folder keeps the salt but not the name) while nobody
        // holding only the log can reverse it or test it against common names.
        static string MachineTag(string dir)
        {
            try
            {
                var saltPath = Path.Combine(dir, "probe.salt");
                byte[] salt;
                if (File.Exists(saltPath) && new FileInfo(saltPath).Length == 16)
                    salt = File.ReadAllBytes(saltPath);
                else
                {
                    salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                    File.WriteAllBytes(saltPath, salt);
                }
                var name = System.Text.Encoding.UTF8.GetBytes(Environment.MachineName);
                var buf = new byte[salt.Length + name.Length];
                Buffer.BlockCopy(salt, 0, buf, 0, salt.Length);
                Buffer.BlockCopy(name, 0, buf, salt.Length, name.Length);
                return "pc-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buf), 0, 3).ToLowerInvariant();
            }
            catch { return "pc-unknown"; }
        }
        Write($"main module base: 0x{_baseAddr:X}");
        LogPartyFixState(modDir);

        ReloadConfig();

        loader.GetController<IReloadedHooks>()?.TryGetTarget(out _hooks);
        if (_hooks is null) { Write("FATAL: no IReloadedHooks controller"); return; }

        IStartupScanner scanner = null;
        var scannerRef = loader.GetController<IStartupScanner>();
        if (scannerRef is null || !scannerRef.TryGetTarget(out scanner))
        {
            Write("FATAL: no IStartupScanner controller");
            return;
        }

        // The gate scan is the one that matters; register it first.
        scanner.AddMainModuleScan(SigStoryOnlineGate, r =>
        {
            if (!r.Found)
            {
                Write("MISS: StoryOnlineGate — this game build is not supported, unlock disabled");
                return;
            }
            lock (_patchLock)
            {
                _gateAddr = _baseAddr + r.Offset;
                Write($"FOUND StoryOnlineGate @ 0x{_gateAddr:X} (+0x{r.Offset:X})");
                if (_unlockWanted) SetUnlockPatchLocked(true);
            }
        });

        scanner.AddMainModuleScan(SigIsInLobby, r =>
        {
            if (!r.Found) { Write("MISS: IsInLobby (logging only, unlock unaffected)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND IsInLobby @ 0x{addr:X} (+0x{r.Offset:X})");
            _isInLobbyHook = _hooks.CreateHook<IsInLobbyFn>(OnIsInLobby, addr).Activate();
        });

        scanner.AddMainModuleScan(SigGetCurrentQuestId, r =>
        {
            if (!r.Found) { Write("MISS: GetCurrentQuestId (logging only)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND GetCurrentQuestId @ 0x{addr:X} (+0x{r.Offset:X})");
            _questIdHook = _hooks.CreateHook<GetCurrentQuestIdFn>(OnGetCurrentQuestId, addr).Activate();
        });

        // C7 05 <disp32> <imm32> — 10 bytes, global at match+10+disp
        scanner.AddMainModuleScan(SigPhaseId, r =>
            _phaseIdAddr = ResolveRipGlobal(r, "PhaseId", dispAt: 2, insnLen: 10));

        // 48 8B 0D <disp32> — 7 bytes, global at match+7+disp
        scanner.AddMainModuleScan(SigPartyMemberArray, r =>
            _partyArrayAddr = ResolveRipGlobal(r, "PartyMemberArray", dispAt: 3, insnLen: 7));

        // Resolved for reference only. Not hooked: its parameter list is not
        // confirmed, and guessing one wrong on a live x64 call risks a crash for
        // a diagnostic we can already read off the screen.
        scanner.AddMainModuleScan(SigGetNumPlayers, r =>
            Write(r.Found
                ? $"FOUND GetNumPlayersInOnlineLobby @ 0x{_baseAddr + r.Offset:X} (+0x{r.Offset:X}) [not hooked]"
                : "MISS: GetNumPlayersInOnlineLobby"));

        scanner.AddMainModuleScan(SigRecvSignalOnSignal, r =>
        {
            if (!r.Found) { Write("MISS: RecvSignal callback (signal log unavailable, nothing else affected)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND RecvSignal callback @ 0x{addr:X} (+0x{r.Offset:X})");
            // Assign before activating: OnRecvSignal calls through this field,
            // so it must be set before the first redirected call can arrive.
            _recvSignalHook = _hooks.CreateHook<RecvSignalOnSignalFn>(OnRecvSignal, addr);
            _recvSignalHook.Activate();
        });

        scanner.AddMainModuleScan(SigSetPlayerInputLimit, r =>
        {
            if (!r.Found) { Write("MISS: SetPlayerInputLimit (input-lock log unavailable, nothing else affected)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND SetPlayerInputLimit @ 0x{addr:X} (+0x{r.Offset:X})");
            _inputLimitHook = _hooks.CreateHook<SetPlayerInputLimitFn>(OnSetPlayerInputLimit, addr);
            _inputLimitHook.Activate();
        });

        scanner.AddMainModuleScan(SigClearAllInputLimits, r =>
        {
            if (!r.Found) { Write("MISS: ClearAllInputLimits (F8 unstuck key unavailable, nothing else affected)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND ClearAllInputLimits @ 0x{addr:X} (+0x{r.Offset:X})");
            _clearLimitsHook = _hooks.CreateHook<ClearAllInputLimitsFn>(OnClearAllInputLimits, addr);
            _clearLimitsHook.Activate();
        });

        scanner.AddMainModuleScan(SigNextPartSearch, r =>
        {
            if (!r.Found) { Write("MISS: NextPartSearch (part-chain log and town-part skip unavailable)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND NextPartSearch @ 0x{addr:X} (+0x{r.Offset:X})");
            _nextPartHook = _hooks.CreateHook<NextPartSearchFn>(OnNextPartSearch, addr);
            _nextPartHook.Activate();
        });

        scanner.AddMainModuleScan(SigOccurrenceMet, r =>
        {
            if (!r.Found) { Write("MISS: OccurrenceMet (town-part skip unavailable)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND OccurrenceMet @ 0x{addr:X} (+0x{r.Offset:X})");
            _occurrenceHook = _hooks.CreateHook<OccurrenceMetFn>(OnOccurrenceMet, addr);
            _occurrenceHook.Activate();
        });

        scanner.AddMainModuleScan(SigStartQuest, r =>
        {
            if (!r.Found) { Write("MISS: StartQuest (part-chain log only)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND StartQuest @ 0x{addr:X} (+0x{r.Offset:X})");
            _startQuestHook = _hooks.CreateHook<StartQuestFn>(OnStartQuest, addr);
            _startQuestHook.Activate();
        });

        scanner.AddMainModuleScan(SigQuestEnd, r =>
        {
            if (!r.Found) { Write("MISS: QuestEnd (part-chain log only)"); return; }
            var addr = _baseAddr + r.Offset;
            Write($"FOUND QuestEnd @ 0x{addr:X} (+0x{r.Offset:X})");
            _questEndHook = _hooks.CreateHook<QuestEndFn>(OnQuestEnd, addr);
            _questEndHook.Activate();
        });

        foreach (var (name, sig) in new[]
        {
            ("SetQuestExitLeaveLobbyFlag", SigUiSetQuestExitLeaveLobbyFlag),
            ("LeaveLobby", SigUiLeaveLobby),
            ("EndOnlineAndMulti", SigUiEndOnlineAndMulti),
            ("JumpPhaseTown", SigUiJumpPhaseTown),
        })
        {
            scanner.AddMainModuleScan(sig, r =>
            {
                if (!r.Found) { Write($"MISS: UI {name} (log only)"); return; }
                var addr = _baseAddr + r.Offset;
                Write($"FOUND UI {name} @ 0x{addr:X} (+0x{r.Offset:X})");
                IHook<UiActionFn> hook = null;
                // The lambda reads the local, which is assigned before Activate.
                hook = _hooks.CreateHook<UiActionFn>((self, arg) => OnUiAction(name, hook, self, arg), addr);
                lock (_uiHooks) _uiHooks[name] = hook;
                hook.Activate();
            });
        }

        // Its own fast timer: a key press lasts a fraction of a second, far
        // shorter than the 3 s tick.
        _keyTimer = new Timer(_ => PollKeys(), null, 1000, 100);

        // Runs on its own thread so it keeps reporting when game threads stall —
        // an infinite loading screen is exactly when we most want the log.
        _tickTimer = new Timer(_ => Tick(), null, 3000, 3000);
    }

    /// <summary>
    /// Resolves a RIP-relative global from a scan hit: the operand at
    /// <paramref name="dispAt"/> is a displacement from the end of an
    /// <paramref name="insnLen"/>-byte instruction. Returns 0 on any failure.
    /// </summary>
    private nint ResolveRipGlobal(PatternScanResult r, string name, int dispAt, int insnLen)
    {
        if (!r.Found) { Write($"MISS: {name} (logging unavailable)"); return 0; }
        try
        {
            var insn = _baseAddr + r.Offset;
            var disp = Marshal.ReadInt32(insn + dispAt);
            var addr = insn + insnLen + disp;
            var span = addr.ToInt64() - _baseAddr.ToInt64();
            if (span < 0 || span > MaxModuleSpan)
            {
                Write($"{name}: computed 0x{addr:X} is outside the module, ignoring");
                return 0;
            }
            Write($"FOUND {name} global @ 0x{addr:X} (insn +0x{r.Offset:X}, disp 0x{disp:X})");
            return addr;
        }
        catch (Exception ex) { Write($"{name} resolve failed: {ex.Message}"); return 0; }
    }

    private static string GameVersion()
    {
        try
        {
            var m = Process.GetCurrentProcess().MainModule;
            var fv = FileVersionInfo.GetVersionInfo(m!.FileName);
            var ver = string.IsNullOrWhiteSpace(fv.ProductVersion) ? "unknown" : fv.ProductVersion.Trim();
            return $"{ver} (module {m.ModuleMemorySize / (1024 * 1024)} MB)";
        }
        catch { return "unknown"; }
    }

    // ------------------------------------------------------------------ hooks

    private byte OnIsInLobby(nint self)
    {
        var result = _isInLobbyHook.OriginalFunction(self);
        _lobbyCalls++;

        if (result != _lastLobbyResult)
        {
            _lastLobbyResult = result;
            Write($"IsInLobby -> {result}  (quest={Categorise(_lastQuestId)}, calls={_lobbyCalls})");
        }
        return result;
    }

    private nint OnGetCurrentQuestId(nint self, nint outQuestId)
    {
        var ret = _questIdHook.OriginalFunction(self, outQuestId);

        // The out pointer comes from the game so it is virtually always valid,
        // but an invalid one would be an uncatchable AccessViolation. Validate
        // it — and cache the verdict, because this hook is hot and the game
        // reuses the same buffer.
        if (outQuestId != _lastQuestOutPtr)
        {
            _lastQuestOutPtr = outQuestId;
            _lastQuestOutOk = Readable(outQuestId, 4);
        }
        if (!_lastQuestOutOk) return ret;

        var id = Marshal.ReadInt32(outQuestId);
        if (id != _lastQuestId)
        {
            var prev = _lastQuestId;
            var now = _clock.ElapsedMilliseconds;

            // How long the quest just left was held. Comparing patched against
            // unpatched runs meant hand-subtracting timestamps out of the log,
            // which is exactly the sort of manual step that produces a wrong
            // number in a table. Put it on the line instead.
            var held = (prev > 0 && _questEnteredMs > 0)
                ? $"  (held {(now - _questEnteredMs) / 1000}s)"
                : "";

            _lastQuestId = id;
            _questEnteredMs = now;
            Write($"QUEST CHANGE {Categorise(prev)} -> {Categorise(id)}  inLobby={_lastLobbyResult}{held}");

            // Warn on the way in, not after the fact. By the time a party-heavy
            // quest has hung there is nothing to do but quit, so the useful
            // moment to say so is the moment it is entered.
            if (PartyHeavyQuests.TryGetValue(id, out var ratio) && _lastLobbyResult == 1)
            {
                Write($"!!! NOTE: {Categorise(id)} reassigns the party in {ratio} of its sections.");
                Write("!!! Party composition does not replicate between clients, so scripted party changes");
                Write("!!! (companions joining mid-quest) may not happen. Confirmed on 100001: with two");
                Write("!!! players the crew never joined and the fight could not be finished.");
                Write("!!! These quests are NOT guaranteed to fail — 102000 played through fine solo online.");
            }

            // Only the FIRST quest of the session is the save's own story point.
            // This used to take the first *story* quest instead, so a save that
            // resumes in town reported whatever chapter was picked next from
            // Chapter Select (2026-09-12: "Chapter 0" for a save sitting in
            // town). Mismatched story progress between the two players is the
            // single biggest cause of hangs, so this line has to be right.
            if (!_bootQuestSeen && id > 0)
            {
                _bootQuestSeen = true;
                if (_lastLobbyResult != 1 && IsStory(id))
                    Write($">>> THIS SAVE RESUMES IN: {Categorise(id)} — compare with the other player's line; the two saves should be near the same story point <<<");
                else
                    Write($">>> THIS SAVE RESUMES IN: {Categorise(id)} — no story point to compare; if story parts hang, check both saves are at the same chapter <<<");
            }
        }
        return ret;
    }

    /// <summary>
    /// Logs each quest signal once per story quest: "seen" when it first
    /// reaches a listener of its type, "used" when a listener's count goes up.
    /// Behaviour is untouched: the original always runs, with the same
    /// arguments, before anything is read.
    ///
    /// Every read mirrors one the original makes itself, so nothing is
    /// dereferenced that the game would not dereference. It reads the
    /// listener's +0x28. Only when that is set does it follow the signal
    /// pointer and read the type at +0x08 of both. Only when the types match
    /// does it read the hash at +0x20. The +0x10 and +0x18 read here sit between
    /// those two in the same object.
    /// </summary>
    private void OnRecvSignal(nint listener, nint signalRef)
    {
        var expected = Marshal.ReadIntPtr(listener + 0x28);
        var before = expected != 0 ? Marshal.ReadInt32(listener + 0x38) : 0;

        _recvSignalHook.OriginalFunction(listener, signalRef);

        if (expected == 0) return;
        try
        {
            var quest = _lastQuestId;
            if (!IsStory(quest)) return;

            var sig = Marshal.ReadIntPtr(signalRef);
            var type = Marshal.ReadInt64(sig + 0x08);
            if (type != Marshal.ReadInt64(expected + 0x08)) return;

            var extra = (ulong)Marshal.ReadInt64(sig + 0x10);
            var obj = (ulong)Marshal.ReadInt64(sig + 0x18);
            var hash = (ulong)Marshal.ReadInt64(sig + 0x20);
            var used = Marshal.ReadInt32(listener + 0x38) > before;

            string seenLine = null, usedLine = null;
            lock (_sigLock)
            {
                if (quest != _sigQuest) { _sigQuest = quest; _sigSeen.Clear(); _sigUsed.Clear(); }
                if (_sigSeen.Add((obj, hash)))
                    seenLine = $"SIGNAL seen obj={obj} hash={hash} type={type}{(extra != 0 ? $" extra={extra}" : "")}   quest={Categorise(quest)}";
                if (used && _sigUsed.Add((obj, hash)))
                    usedLine = $"SIGNAL used obj={obj} hash={hash}   quest={Categorise(quest)}";
            }
            if (seenLine != null) Write(seenLine);
            if (usedLine != null) Write(usedLine);
        }
        catch { /* the diagnostic must never take the game down */ }
    }

    // ------------------------------------------------------------------ reads

    private int ReadPhase()
    {
        if (_phaseIdAddr == 0 || !Readable(_phaseIdAddr, 4)) return int.MinValue;
        return Marshal.ReadInt32(_phaseIdAddr);
    }

    /// <summary>
    /// Party-slot occupancy, e.g. <c>"XX-- (2/4)"</c>. Purely observational.
    /// The array layout is undocumented, so this reports raw occupancy rather
    /// than pretending to identify who occupies each slot.
    /// </summary>
    private string ReadPartySlots()
    {
        if (_partyArrayAddr == 0) return "n/a";
        if (!Readable(_partyArrayAddr, 8 * PartySlots)) return "unreadable";

        Span<char> slots = stackalloc char[PartySlots];
        var filled = 0;
        var comp = new string[PartySlots];
        for (var i = 0; i < PartySlots; i++)
        {
            var member = Marshal.ReadIntPtr(_partyArrayAddr + i * 8);
            var ok = member != 0 && Readable(member, 8);
            slots[i] = ok ? 'X' : '-';
            if (ok) filled++;

            // Low 32 bits of the member pointer as a per-slot fingerprint. Slot
            // occupancy alone cannot see a party *swap* — Katalina out, Rackam
            // in still reads XXXX (4/4) — and a swap is exactly what
            // isPartyChange sections do and what is claimed not to replicate.
            // The pointer is a proxy, not an identity: it can change without
            // the character changing. So treat a change as "something moved,
            // look here", never as proof of a specific swap.
            comp[i] = ok ? ((ulong)member.ToInt64() & 0xFFFFFFFF).ToString("X8") : "--------";
        }
        _partyFilled = filled;
        _partyComp = string.Join(" ", comp);
        return $"{new string(slots)} ({filled}/{PartySlots})";
    }

    // ------------------------------------------------------------------ patch

    /// <summary>
    /// Applies or reverts the gate patch. Caller must hold <see cref="_patchLock"/>.
    ///
    /// <c>sete dil</c> → <c>mov dil,1</c> makes row kinds 3 and 4 report
    /// available regardless of the online flag. The <c>cmp</c> above is left
    /// intact; its flags simply go unused.
    ///
    /// The four bytes are not 4-byte aligned in practice, so the write cannot be
    /// made atomic. The window is nanoseconds and the instruction only runs while
    /// the Quest Counter builds its row list, so a torn read is very unlikely —
    /// but the safe usage is to set the option before launching, which is why it
    /// defaults on.
    /// </summary>
    private void SetUnlockPatchLocked(bool enable)
    {
        if (_gateAddr == 0 || enable == _unlockApplied) return;
        try
        {
            var setePos = _gateAddr + GatePatchOffset;

            if (_gateOriginal is null)
            {
                if (!Readable(setePos, 4))
                {
                    Write($"UNLOCK: patch site 0x{setePos:X} is not readable, not patching");
                    return;
                }
                var current = new byte[4];
                Marshal.Copy(setePos, current, 0, 4);
                for (var i = 0; i < 4; i++)
                {
                    if (current[i] == SeteDil[i]) continue;
                    Write($"UNLOCK: refusing to patch — expected sete dil at gate+{GatePatchOffset}, found {BitConverter.ToString(current)}");
                    return;
                }
                _gateOriginal = current;
                if ((setePos.ToInt64() & 3) != 0)
                    Write($"UNLOCK: note — patch site 0x{setePos:X} is not 4-byte aligned; write is non-atomic");
            }

            var bytes = enable ? MovDil1 : _gateOriginal;

            if (!VirtualProtect(setePos, 4, PAGE_EXECUTE_READWRITE, out var old))
            {
                Write($"UNLOCK: VirtualProtect failed (err {Marshal.GetLastWin32Error()}), not patching");
                return;
            }
            try
            {
                if ((setePos.ToInt64() & 3) == 0)
                    Marshal.WriteInt32(setePos, BitConverter.ToInt32(bytes, 0)); // single store
                else
                    Marshal.Copy(bytes, 0, setePos, 4);
            }
            finally { VirtualProtect(setePos, 4, old, out _); }

            _unlockApplied = enable;
            Write(enable
                ? $"*** UNLOCK APPLIED at 0x{setePos:X}: sete dil -> mov dil,1 ({BitConverter.ToString(bytes)}) ***"
                : $"UNLOCK reverted at 0x{setePos:X} ({BitConverter.ToString(bytes)})");
        }
        catch (Exception ex) { Write($"UNLOCK error: {ex.Message}"); }
    }

    // ----------------------------------------------------------------- config

    /// <summary>
    /// Re-reads probe_config.json so the option can be toggled without
    /// restarting. Skips the read entirely unless the file's timestamp changed.
    /// </summary>
    private void ReloadConfig()
    {
        if (_configPath is null) return;
        try
        {
            bool wanted;
            var skipTown = true;
            if (!File.Exists(_configPath))
            {
                // Default ON: unlocking the gate is the point of the mod, and it
                // means a second machine works after a plain copy, with no edit.
                File.WriteAllText(_configPath, "{\n  \"UnlockStoryWhileOnline\": true,\n  \"SkipTownPartsOnline\": true\n}\n");
                _configStamp = File.GetLastWriteTimeUtc(_configPath);
                wanted = true;
            }
            else
            {
                var stamp = File.GetLastWriteTimeUtc(_configPath);
                if (stamp == _configStamp && _unlockLogged) return;   // unchanged, nothing to do
                _configStamp = stamp;
                var text = File.ReadAllText(_configPath);
                wanted = UnlockTrue.IsMatch(text);
                // On unless explicitly false, so config files from older
                // versions (which lack the key) get it too.
                skipTown = !SkipTownFalse.IsMatch(text);
            }

            if (skipTown != _skipTownParts || !_unlockLogged)
            {
                _skipTownParts = skipTown;
                Write($"config: SkipTownPartsOnline = {skipTown}");
            }

            lock (_patchLock)
            {
                // Log only when the *requested* value changes. Previously this
                // compared against the applied value, so before the gate scan
                // completed it re-logged the same line every few seconds — which
                // is why early logs show "config: ... = True" five times over.
                if (wanted != _unlockWanted || !_unlockLogged)
                {
                    _unlockWanted = wanted;
                    _unlockLogged = true;
                    Write($"config: UnlockStoryWhileOnline = {wanted}");
                }
                if (_unlockWanted != _unlockApplied) SetUnlockPatchLocked(_unlockWanted);
            }
        }
        catch { /* a bad config must never take the game down */ }
    }

    // ----------------------------------------------------------- input limits

    private void OnSetPlayerInputLimit(nint node)
    {
        // Read before the call. The original reads the same three fields, so a
        // pointer bad enough to fault here would fault there too.
        var action = Marshal.ReadInt32(node + 0x30);
        var on = Marshal.ReadByte(node + 0x34) != 0;
        var chara = Marshal.ReadInt32(node + 0x38);

        _inputLimitHook.OriginalFunction(node);
        try
        {
            var quest = _lastQuestId;
            string held;
            lock (_limitLock)
            {
                if (quest != _limitQuest) { _limitQuest = quest; _limitsHeld.Clear(); }
                if (on) _limitsHeld.Add((chara, action)); else _limitsHeld.Remove((chara, action));
                held = HeldLocked();
            }
            Write($"INPUT LIMIT {(on ? "lock  " : "unlock")} control={action} who={CharaSlot(chara)}   held=[{held}]   quest={Categorise(quest)}");
        }
        catch { }
    }

    private void OnClearAllInputLimits()
    {
        _clearLimitsHook.OriginalFunction();
        try
        {
            var now = _clock.ElapsedMilliseconds;
            int count;
            lock (_limitLock)
            {
                _limitsHeld.Clear();
                _resetsSinceLog++;
                // The game may call this often; one line per 10 s is plenty.
                if (now - _lastResetLogMs < 10000) return;
                _lastResetLogMs = now;
                count = _resetsSinceLog;
                _resetsSinceLog = 0;
            }
            Write($"INPUT LIMIT reset by game, all characters (x{count})   quest={Categorise(_lastQuestId)}");
        }
        catch { }
    }

    private static string CharaSlot(int i) => i <= 0 ? "party" : i <= 4 ? $"p{i}" : $"x{i - 4}";

    private string HeldLocked()
    {
        var parts = new List<string>();
        foreach (var (chara, action) in _limitsHeld) parts.Add($"{CharaSlot(chara)}:{action}");
        return string.Join(" ", parts);
    }

    private void PollKeys()
    {
        // Timer callbacks overlap if one waits on a character's lock.
        if (Interlocked.Exchange(ref _keyPolling, 1) == 1) return;
        try
        {
            var down = (GetAsyncKeyState(VK_F8) & 0x8000) != 0;
            var pressed = down && !_unstuckKeyDown;
            _unstuckKeyDown = down;
            if (pressed && GameHasFocus()) Unstuck();
        }
        catch { }
        finally { Interlocked.Exchange(ref _keyPolling, 0); }
    }

    // GetAsyncKeyState sees keys pressed in any program, so F8 in a browser
    // must not reach the game.
    private static bool GameHasFocus()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// F8: unlock the controls of this PC's characters with the game's own
    /// reset. Only this PC changes and nothing is sent anywhere. It does not
    /// move the story on: if the script is waiting for something that never
    /// arrived, the player can move again but the other player still has to
    /// finish that part (as in Chapter 7, try 2).
    /// </summary>
    private void Unstuck()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now - _lastUnstuckMs < 2000) return;
        _lastUnstuckMs = now;

        var quest = _lastQuestId;
        if (_clearLimitsHook is null)
        {
            Write("UNSTUCK: F8 pressed, but this game build has no matching reset function");
            return;
        }
        // Characters are created and destroyed while an area loads, and
        // resolving one mid-load is the one real risk, so wait transitions out.
        if (quest <= 0 || now - _questEnteredMs < 5000 || now - _phaseChangedMs < 5000)
        {
            Write($"UNSTUCK: F8 ignored, the area is still loading   quest={Categorise(quest)}");
            return;
        }
        string held;
        lock (_limitLock)
        {
            // The set is only reset by the next lock call, so right after a
            // quest change it still holds the previous quest's entries.
            held = _limitQuest == quest ? HeldLocked() : "";
            _limitsHeld.Clear();
        }
        _clearLimitsHook.OriginalFunction();
        Write($"UNSTUCK: F8, controls unlocked for this PC's characters (locks before: [{held}])   quest={Categorise(quest)}");
    }

    // ------------------------------------------------------- story part chain

    private void OnNextPartSearch(nint questSys, nint arg)
    {
        var from = _lastQuestId;
        var state = -1;
        try
        {
            state = Marshal.ReadInt32(questSys + QuestSysReplayState);
            Write($"CHAIN: {Categorise(from)} cleared, looking for the next part   replay={state} inLobby={_lastLobbyResult}");
        }
        catch { }

        var outer = t_chainFrom;
        t_chainFrom = from;
        try { _nextPartHook.OriginalFunction(questSys, arg); }
        finally { t_chainFrom = outer; }

        try { Write($"CHAIN: done, quest now {Categorise(_lastQuestId)}"); } catch { }
    }

    /// <summary>
    /// The town-part skip. The original always runs first; its answer is only
    /// changed while a part-chain search is running, the player is in an online
    /// session, the option is on, and the quest is one of the two town parts
    /// (or the mission straight after one, reached from its own ship part).
    /// </summary>
    private nint OnOccurrenceMet(nint questSys, nint questRecord, nint arg)
    {
        var result = _occurrenceHook.OriginalFunction(questSys, questRecord, arg);
        try
        {
            var from = t_chainFrom;
            if (from == 0 || !_skipTownParts || _lastLobbyResult != 1) return result;

            var id = Marshal.ReadInt32(questRecord + QuestRecordId);
            var met = (result & 0xFF) != 0;

            if (TownParts.TryGetValue(id, out var town) && from == town.ship && met)
            {
                Write($"TOWN SKIP: online, so not going into the town part {Categorise(id)}" +
                      (town.skipTo != 0 ? $" — going on to {Categorise(town.skipTo)} instead" : " — back to town instead"));
                return 0;
            }
            foreach (var (townId, t) in TownParts)
            {
                if (t.skipTo == id && t.ship == from && !met)
                {
                    Write($"TOWN SKIP: starting {Categorise(id)} straight after {Categorise(from)} (skipping {Categorise(townId)})");
                    return 1;
                }
            }
        }
        catch { }
        return result;
    }

    private void OnStartQuest(nint questSys, nint questIdPtr, nint section, nint a4, nint a5, nint a6)
    {
        try
        {
            var id = Marshal.ReadInt32(questIdPtr);
            Write($"CHAIN START: {Categorise(id)} section={(int)section} load={(byte)a5}   inLobby={_lastLobbyResult}");
        }
        catch { }
        _startQuestHook.OriginalFunction(questSys, questIdPtr, section, a4, a5, a6);
    }

    private nint OnQuestEnd(nint questSys, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint a8)
    {
        var quest = _lastQuestId;
        try
        {
            var state = Marshal.ReadInt32(questSys + QuestSysReplayState);
            Write($"QUEST END: leaving {Categorise(quest)}   replay={state} inLobby={_lastLobbyResult}");
        }
        catch { }
        var result = _questEndHook.OriginalFunction(questSys, a2, a3, a4, a5, a6, a7, a8);
        try
        {
            var town = Marshal.ReadInt32(questSys + QuestSysReturnTown);
            Write($"QUEST END: going to map 0x{town:X}{TownNote(town)}   inLobby={_lastLobbyResult}");
        }
        catch { }
        return result;
    }

    private static string TownNote(int map) => map switch
    {
        0xC00 => " (the lobby town)",
        >= 0xD00 and <= 0xD0F => " (a story town, not the lobby town)",
        _ => "",
    };

    private void OnUiAction(string name, IHook<UiActionFn> hook, nint self, nint arg)
    {
        try { Write($"UI: {name}   quest={Categorise(_lastQuestId)} inLobby={_lastLobbyResult}"); } catch { }
        hook.OriginalFunction(self, arg);
    }

    // ------------------------------------------------------------------- tick

    private void Tick()
    {
        // System.Threading.Timer does not wait for the previous callback. A
        // tick held up by slow disk (the config and log writes) would otherwise
        // run alongside the next one and log the same change twice.
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            ReloadConfig();

            var phase = ReadPhase();
            if (phase != _lastPhase)
            {
                var prev = _lastPhase;
                _lastPhase = phase;
                _phaseChangedMs = _clock.ElapsedMilliseconds;
                Write($"PHASE {Fmt(prev)} -> {Fmt(phase)}   quest={Categorise(_lastQuestId)} inLobby={_lastLobbyResult}");
            }

            var party = ReadPartySlots();
            if (party != _lastParty)
            {
                var prev = _lastParty;
                _lastParty = party;
                Write($"PARTY {prev} -> {party}   quest={Categorise(_lastQuestId)} inLobby={_lastLobbyResult}");
            }

            // Logged separately from PARTY because the interesting case is the
            // one PARTY cannot see: same slot count, different occupants.
            if (_partyComp != _lastPartyComp)
            {
                var prev = _lastPartyComp;
                _lastPartyComp = _partyComp;
                Write($"PARTY COMP [{prev}] -> [{_partyComp}]   quest={Categorise(_lastQuestId)} inLobby={_lastLobbyResult}");
            }

            var now = _clock.ElapsedMilliseconds;

            if (now - _lastHeartbeatMs < 15000) return;
            _lastHeartbeatMs = now;

            // Both sets are reset lazily, on the first event of a new quest, so
            // back in town they still hold the last story quest's counts. Report
            // them only for the quest they belong to.
            var quest = _lastQuestId;
            int signalsUsed, limitsHeld;
            lock (_sigLock) signalsUsed = _sigQuest == quest ? _sigUsed.Count : 0;
            lock (_limitLock) limitsHeld = _limitQuest == quest ? _limitsHeld.Count : 0;
            Write($"[hb] quest={Categorise(quest)} phase={Fmt(phase)} party={party} inLobby={_lastLobbyResult} unlock={_unlockApplied} lobbyCalls={_lobbyCalls} signalsUsed={signalsUsed} limitsHeld={limitsHeld}");
        }
        catch { /* the diagnostic must never take the game down */ }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>
    // The hang detector that used to live here has been removed.
    //
    // It fired on: party-heavy quest + two filled party slots + five minutes
    // with no quest change. Every one of those is also true of simply PLAYING
    // one of those quests. Chapter 4 is quest 102000 and was played normally
    // for well over five minutes on 2026-09-09; the detector would have called
    // it a hang.
    //
    // The deeper problem is that a hang and normal play are indistinguishable
    // in this log. Both show the same quest id, the same phase, the same party,
    // and a near-identical IsInLobby call rate. Nothing currently read here
    // changes during play but not during a hang. Detecting one needs a signal
    // that only moves while the player can act - position, or HP - and that
    // means finding those globals first.
    //
    // This is the second detector deleted for the same reason. Writing a third
    // without a new signal would be the mistake, not the fix.

    /// <summary>
    /// Records which quests the companion data mod <c>gbfr.coop.partyfix</c>
    /// overrides, so a log can be read months later without guessing whether
    /// the fix was in play. Twice now a run has been ambiguous for exactly this
    /// reason.
    ///
    /// This proves the files are *present*, not that the loader applied them —
    /// gbfrelink.utility.manager logs nothing about what it merged. Still much
    /// better than nothing.
    /// </summary>
    private void LogPartyFixState(string modDir)
    {
        try
        {
            // gbfrelink.utility.manager writes cached_files.txt listing every
            // loose file it merged into the archive, one per line as
            // "file|<archive path>|<timestamp>". That is ground truth for what
            // the game is actually reading.
            var modsDir = Directory.GetParent(modDir)?.FullName;
            if (modsDir is null) { Write("partyfix: cannot locate Mods directory"); return; }

            string cache = null;
            foreach (var d in Directory.GetDirectories(modsDir, "gbfrelink.utility.manager*"))
            {
                var c = Path.Combine(d, "cached_files.txt");
                if (File.Exists(c)) { cache = c; break; }
            }
            if (cache is null) { Write("partyfix: no manager cache found, cannot tell"); return; }

            var ids = new List<string>();
            foreach (var line in File.ReadLines(cache))
            {
                var i = line.IndexOf("quest/", StringComparison.OrdinalIgnoreCase);
                if (i < 0 || line.IndexOf("sectionlist.msg", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var rest = line.Substring(i + 6);
                var slash = rest.IndexOf('/');
                if (slash > 0) ids.Add(rest.Substring(0, slash).ToUpperInvariant());
            }
            ids.Sort();

            Write(ids.Count == 0
                ? "partyfix: NOT applied (manager merged no quest SectionList)"
                : $"partyfix: APPLIED to {string.Join(", ", ids)}");
        }
        catch (Exception ex)
        {
            Write($"partyfix: check failed ({ex.GetType().Name})");
        }
    }

    private static bool IsStory(int id) => id > 0 && id.ToString("X6")[0] == '1';

    /// <summary>
    /// Story quests whose SectionList marks two or more sections
    /// <c>isPartyChange: true</c>, i.e. sections that reassign the party
    /// mid-quest. Value is "party-change sections / total sections".
    ///
    /// The cut is at two rather than one because 101005 has exactly one such
    /// section and played through fine with two players; one reassignment is
    /// survivable, repeated ones are not.
    ///
    /// Derived by decoding <c>quest/&lt;id&gt;/SectionList.msg</c> for all 54
    /// main-story quests; only these seven qualify.
    ///
    /// This list marks where the game changes the party, NOT why co-op fails
    /// there. The earlier claim that these quests "hang online" was withdrawn:
    /// 102000 played normally online for minutes, and clearing every flag
    /// (the partyfix data mod) did not make the crew appear in 100001. The
    /// failure is the companions never spawning online. Kept as a heads-up.
    /// </summary>
    private static readonly Dictionary<int, string> PartyHeavyQuests = new()
    {
        [0x100000] = "6/6",
        [0x100001] = "14/14",
        [0x100006] = "4/5",
        [0x101000] = "7/9",
        [0x102000] = "5/7",
        [0x109000] = "4/4",
        [0x10AF02] = "2/2",
    };

    private static string Fmt(int v) => v == int.MinValue ? "n/a" : $"{v} (0x{v:X})";

    /// <summary>
    /// Quest ids are integers whose HEXADECIMAL representation is the documented
    /// quest number: 5245440 = 0x500A00 = quest 500A00, the 5xxxxx town/lobby
    /// range. This is also why FSM files like quest_40a330 and quest_83fb10
    /// exist — hex digits, impossible under a decimal reading.
    /// </summary>
    /// <summary>
    /// Turns a quest id into something readable, keeping the raw id in
    /// parentheses because that is still the key used everywhere else — the
    /// findings notes, the party-heavy table, the data patch.
    ///
    /// The chapter for each id is VERIFIED against the game's own
    /// <c>system/table/chapter_select.tbl</c>, not guessed from the digits.
    ///
    /// An earlier version read the id as <c>C_HH_SSS</c> (category, chapter,
    /// section) and called 102000 "Chapter 2". That was wrong — 102000 is
    /// Chapter 4. Reading chapters out of the digits does not work, and the
    /// wrong labels caused real confusion before anyone caught them.
    ///
    /// The table lists its 41 chapter-select missions in order, tagged
    /// <c>00_00</c>..<c>11_00</c>. Those tags occur 2, 3, 2, 3, 3, 5, 4, 4, 5,
    /// 6, 1, 3 times, which matches the mission count of every chapter on the
    /// Chapter Select screen exactly, so the grouping is unambiguous. Four
    /// independent checks against observed play agree: the Quakadile fight is
    /// 100001/Chapter 1, the mine shaft is 101003/Chapter 3, Furycane is
    /// 101004/Chapter 3, and the church quest is 102000/Chapter 4.
    ///
    /// Ids not in the table (100006, 109000, 10AF02, town, side quests) are not
    /// chapter-select missions and get no chapter label.
    /// </summary>
    private static readonly Dictionary<int, string> QuestChapters = new()
    {
        [0x100000] = "Prologue",  [0x100005] = "Prologue",
        [0x100001] = "Chapter 1", [0x101000] = "Chapter 1",
        [0x101F00] = "Chapter 2", [0x101002] = "Chapter 2",
        [0x101003] = "Chapter 3", [0x101004] = "Chapter 3", [0x101005] = "Chapter 3",
        [0x102000] = "Chapter 4", [0x102F00] = "Chapter 4", [0x102002] = "Chapter 4",
        [0x103F00] = "Chapter 5", [0x103001] = "Chapter 5",
        [0x104F00] = "Chapter 6", [0x104001] = "Chapter 6", [0x104002] = "Chapter 6",
        [0x105F00] = "Chapter 7", [0x105001] = "Chapter 7", [0x105F01] = "Chapter 7",
        [0x106F00] = "Chapter 8", [0x106001] = "Chapter 8", [0x106002] = "Chapter 8",
        [0x107000] = "Chapter 9", [0x107F00] = "Chapter 9", [0x107001] = "Chapter 9",
        [0x108F00] = "Final Chapter",
        [0x109001] = "Chapter Ø", [0x10A000] = "Chapter Ø", [0x10A010] = "Chapter Ø",
    };
    private static string Categorise(int id)
    {
        if (id <= 0) return "none";
        var hex = id.ToString("X6");

        if (hex[0] != '1')
        {
            var other = hex[0] switch
            {
                '2' => "Side quest",
                '3' => "Fate episode",
                '4' => "Multiplayer quest",
                '5' => "Town",
                '6' => "dummy",
                '7' => "Short story",
                // A full Conflux run (2026-09-13) produced 30 distinct 8xxxxx
                // ids and nothing else; no 8xxxxx id appears in any other log.
                '8' => "Conflux",
                _   => "unknown"
            };
            return $"{other} ({hex})";
        }

        // Only what the chapter_select table actually says. A story quest that
        // is not one of its 41 missions gets no chapter — better silent than
        // confidently wrong, which is how the last version went astray.
        return QuestChapters.TryGetValue(id, out var chapter)
            ? $"{chapter} ({hex})"
            : $"story quest {hex}";
    }

    // -------------------------------------------------------------------- log

    /// <summary>
    /// Opens probe.log, rotating it to probe.log.old first if it has grown past
    /// <see cref="MaxLogBytes"/>. Without this the file grows forever and gets
    /// unwieldy to share.
    /// </summary>
    private void OpenLog()
    {
        try
        {
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxLogBytes)
            {
                var old = _logPath + ".old";
                try { File.Delete(old); } catch { }
                File.Move(_logPath, old);
            }
            _file = new StreamWriter(_logPath, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            _log?.WriteLine($"[storyprobe] could not open probe.log: {ex.Message}");
        }
    }

    private void Write(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        try { lock (_logLock) _file?.WriteLine(stamped); } catch { }
        try { _log?.WriteLine($"[storyprobe] {line}"); } catch { }
    }

    // ----------------------------------------------------------------- IMod

    public void Suspend() { }
    public void Resume() { }
    public void Unload() { }
    public bool CanUnload() => false;
    public bool CanSuspend() => false;

    public Action Disposing => () =>
    {
        try { _tickTimer?.Dispose(); } catch { }
        try { _keyTimer?.Dispose(); } catch { }
        try { lock (_patchLock) SetUnlockPatchLocked(false); } catch { }  // leave the game as we found it
        try { lock (_logLock) { _file?.Flush(); _file?.Dispose(); } } catch { }
    };
}
