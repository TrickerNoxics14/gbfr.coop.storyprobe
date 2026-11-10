using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Two online fixes that need no messages between players:
///
/// <b>Crew joins online</b> (Chapter 1 part 2, the Quakadile fight, and any
/// story section that adds party members). A story section's party step
/// (stage::quest::BeginSection Execute) ends in a routine that spawns the AI
/// party members for slots 1-3. Online, in a joined lobby, that routine skips
/// slots 1-3 entirely (2.0.5 +0x31F6FB2 / 2.0.6 +0x31F76F2: jnz over the
/// loop), so the scripted join changes the party data but no character ever
/// appears. For the length of a story section's party step only, the skip is
/// lifted, and any slot that already holds a character (the other players,
/// or allies already there) is left exactly as it is: its release, removal
/// and respawn are blocked. Only empty slots are filled, the same way CPU
/// allies fill empty slots in online quests.
///
/// <b>Full Main Menu online</b>. The pause menu has two ways to open
/// (ui_pausemenu_v1_0_1): PauseOpenSkip true fades out and in and builds the
/// whole menu at once (#913419018 -> PauseTop #882428490); false shows the
/// background first and waits on "Pause" timers before the list (#362981653).
/// Online that second way can stop at the background (no Party, Gear,
/// Skills...). In a story area online the answer is true, so the fade way runs.
/// </summary>
public partial class Mod
{
    // ------------------------------------------------------------ crew spawn

    /// <summary>BeginSection Execute (stage::quest::BeginSection vtable slot 1). 2.0.6 +0x31F5550, 2.0.5 +0x31F4E20.</summary>
    private const string SigBeginSection =
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC 48 02 00 00 48 8D AC 24 80 00 00 00 48 C7 85 C0 01 00 00 FE FF FF FF 48 89 CF 48 8B 05 ?? ?? ?? ?? 8B B0 3C 0E 00 00 E8 ?? ?? ?? ?? F7 C6 00 00 F0 00 74 21 48 8B 77 20 48 85 F6 0F 84 ?? ?? 00 00 48 83 7F 50 10 0F 82 80 01 00 00 48 8B 4F 38 E9 7B 01 00 00";
    /// <summary>The "joined lobby" test before the slot 1-3 loop; the jnz to patch is <see cref="CrewGateJumpOffset"/> in. 2.0.6 +0x31F76EA.</summary>
    private const string SigCrewSpawnGate =
        "FF 90 08 01 00 00 84 C0 0F 85 ?? ?? 00 00 48 8B 3D ?? ?? ?? ?? 48 8D 55 40 48 89 F9 E8";
    private const int CrewGateJumpOffset = 8;
    /// <summary>Party data for a slot (mgr, slot, ...). Same RVA in both builds (+0x371970).</summary>
    private const string SigPartyData =
        "41 57 41 56 41 55 41 54 56 57 55 53 48 83 EC 68 44 88 4C 24 2F 45 89 C7 89 D7 49 89 CE 0F B6 84 24 E0 00 00 00 88 44 24 2D 0F B6 AC 24 D8 00 00 00 0F B6 84 24 D0 00 00";
    /// <summary>Releases a slot's spawn handle before its character is removed. +0x23D660 in both builds.</summary>
    private const string SigPartySlotRelease =
        "41 57 41 56 56 57 53 48 83 EC 30 49 89 D6 48 89 CF 48 8D 71 10 48 89 F1 E8 ?? ?? ?? ?? 85 C0 0F 85 D3 00 00 00 81 7F 5C FF FF FF 7F 0F 84 D0 00 00 00 4C 8B 7F 70 48 8B";
    /// <summary>Removes the character in a party slot (slot). 2.0.6 +0x6C7140, 2.0.5 +0x6C6D70.</summary>
    private const string SigPartySlotRemove =
        "41 56 56 57 53 48 83 EC 28 83 F9 03 76 0A 48 83 C4 28 5B 5F 5E 41 5E C3 89 CB 48 8D 34 5B 48 8D 3D ?? ?? ?? ?? 4C 8D 34 F7 8B 04 F7 85 C0 74 37";
    /// <summary>Spawns a character (mgr, name "PlayerNPC"+slot, chara, ...). +0x23C3B0 in both builds.</summary>
    private const string SigPartySlotSpawn =
        "55 41 57 41 56 41 55 41 54 56 57 53 48 81 EC 08 01 00 00 48 8D AC 24 80 00 00 00 48 C7 85 80 00 00 00 FE FF FF FF 44 89 CE 44 89 C7 49 89 D6 48";

    private static readonly byte[] JnzNear = { 0x0F, 0x85 };
    private static readonly byte[] Nop6 = { 0x66, 0x0F, 0x1F, 0x44, 0x00, 0x00 };

    [Function(CallingConventions.Microsoft)]
    private delegate nint BeginSectionFn(nint self, nint a2, nint a3, nint a4);
    [Function(CallingConventions.Microsoft)]
    private delegate nint PartyDataFn(nint mgr, nint slot, nint a3, nint a4, nint a5, nint a6, nint a7);
    [Function(CallingConventions.Microsoft)]
    private delegate nint SlotReleaseFn(nint mgr, nint handle);
    [Function(CallingConventions.Microsoft)]
    private delegate void SlotRemoveFn(nint slot);
    [Function(CallingConventions.Microsoft)]
    private delegate nint SlotSpawnFn(nint mgr, nint name, nint chara, nint a4, nint a5, nint a6);

    private IHook<BeginSectionFn> _beginSectionHook;
    private IHook<PartyDataFn> _partyDataHook;
    private IHook<SlotReleaseFn> _slotReleaseHook;
    private IHook<SlotRemoveFn> _slotRemoveHook;
    private IHook<SlotSpawnFn> _slotSpawnHook;
    private nint _crewGateJump;           // address of the 6-byte jnz, 0 if not found or not as expected
    private byte[] _crewGateOriginal;

    private volatile bool _spawnCrewOnline = true, _fullMenuOnline = true;

    // Set only for the length of one patched BeginSection, on its own thread.
    [ThreadStatic] private static bool t_crewScope;
    [ThreadStatic] private static int t_crewSlot, t_occupied, t_spawned, t_kept;

    // Characters this mod spawned into a slot (per quest). A later party step
    // may swap those like the game does offline; every other filled slot (the
    // other players) stays protected.
    private readonly nint[] _crewEntity = new nint[4];
    private int _crewQuest = -1;

    // ------------------------------------------------------------- main menu

    /// <summary>PauseOpenSkip Evaluate (vtable slot 5): [global]+0x2C != 0. 2.0.6 +0x1D5FD60, 2.0.5 +0x1D5F740.</summary>
    private const string SigPauseOpenSkipEval =
        "48 8B 0D ?? ?? ?? ?? 31 C0 80 79 2C 01 83 D0 02 C3";
    private IHook<UiConditionFn> _pauseOpenSkipHook;
    private const byte CondTrue = 2;
    private long _lastMenuLogMs = -100_000;

    // ------------------------------------------------------------- tutorials

    /// <summary>
    /// stage::quest::SetTutorialWindow Execute (vtable slot 1). 2.0.6 +0x320E6E0,
    /// 2.0.5 +0x320DFA0. Unless the script node's displayAlsoInNetworkPlay_
    /// (+0x34) is set, it returns early when online ([net]+4 == 3), so every
    /// story tutorial is hidden in a lobby (all of them leave the field false).
    /// In a story area online the field is set for the length of the call.
    /// The tutorial's pause is local (PauseGame only counts on this PC).
    /// </summary>
    private const string SigSetTutorialWindow =
        "48 83 EC 48 80 79 28 00 75 ?? 48 8B 05 ?? ?? ?? ?? F6 80 3E 0E 00 00 F0 75 ?? 80 79 34 00 74 ?? 48 8B 05 ?? ?? ?? ?? 48 8D 15 ?? ?? ?? ?? 48 89 54 24 20";
    private const int TutorialAlsoOnline = 0x34;
    [Function(CallingConventions.Microsoft)]
    private delegate nint QuestActionFn(nint self, nint a2, nint a3, nint a4);
    private IHook<QuestActionFn> _setTutorialHook;
    private volatile bool _tutorialsOnline = true;
    private int _tutorialLogQuest = -1;

    // ------------------------------------------- interaction (log only, for a later fix)

    /// <summary>
    /// Log only. Online only the lobby owner's world interactions (Rackam,
    /// Depart, quest objects) move the story; a safe guest-to-host relay needs
    /// to know what a guest's press does. These record it: the press handler
    /// (2.0.6 +0x2335720), the interaction state change (+0x2337CE0, state 3 =
    /// interact requested) and a placement becoming interactable (+0x96E100,
    /// id = **(placement+0x220)).
    /// </summary>
    private const string SigInteractPress =
        "41 56 56 57 53 48 81 EC 88 00 00 00 48 89 CE 4C 8B 41 10 41 8B 88 60 11 00 00 85 C9 0F 84 ?? ?? ?? ?? 48 8B 15 ?? ?? ?? ?? 49 8B 80 68 11 00 00";
    private const string SigInteractSetState =
        "56 57 48 81 EC 88 00 00 00 89 54 24 2C 45 84 C0 0F 84 ?? ?? ?? ?? 89 D6 48 89 CF 48 8B 89 88 00 00 00 48 85 C9 0F 94 C2 39 77 50 0F 95 C0";
    private const string SigPlacementActivate =
        "55 41 56 56 57 53 48 81 EC 80 00 00 00 48 8D AC 24 80 00 00 00 48 C7 45 F8 FE FF FF FF 48 83 B9 10 03 00 00 00 0F 85 ?? ?? ?? ?? 48 89 CE";
    private IHook<QuestActionFn> _interactPressHook, _interactStateHook, _placementActivateHook;
    private long _interactLogWindow; private int _interactLogCount;

    // ------------------------------------------------------------------ setup

    private void RegisterCoopFixScans(IStartupScanner scanner)
    {
        scanner.AddMainModuleScan(SigCrewSpawnGate, r =>
        {
            if (!r.Found) { Write("MISS: crew spawn gate (crew join online off)"); return; }
            var at = _baseAddr + r.Offset + CrewGateJumpOffset;
            var bytes = new byte[6];
            Marshal.Copy(at, bytes, 0, 6);
            if (bytes[0] != JnzNear[0] || bytes[1] != JnzNear[1]) { Write($"crew spawn gate: unexpected bytes {Convert.ToHexString(bytes)}, crew join online off"); return; }
            _crewGateOriginal = bytes; _crewGateJump = at;
            Write($"FOUND crew spawn gate @ 0x{at:X} (+0x{r.Offset + CrewGateJumpOffset:X})");
        });
        Hook(scanner, SigBeginSection, "BeginSection", "crew join online off", a => { _beginSectionHook = _hooks.CreateHook<BeginSectionFn>(OnBeginSection, a); _beginSectionHook.Activate(); });
        Hook(scanner, SigPartyData, "PartyData", "crew join online off", a => { _partyDataHook = _hooks.CreateHook<PartyDataFn>(OnPartyData, a); _partyDataHook.Activate(); });
        Hook(scanner, SigPartySlotRelease, "PartySlotRelease", "crew join online off", a => { _slotReleaseHook = _hooks.CreateHook<SlotReleaseFn>(OnSlotRelease, a); _slotReleaseHook.Activate(); });
        Hook(scanner, SigPartySlotRemove, "PartySlotRemove", "crew join online off", a => { _slotRemoveHook = _hooks.CreateHook<SlotRemoveFn>(OnSlotRemove, a); _slotRemoveHook.Activate(); });
        Hook(scanner, SigPartySlotSpawn, "PartySlotSpawn", "crew join online off", a => { _slotSpawnHook = _hooks.CreateHook<SlotSpawnFn>(OnSlotSpawn, a); _slotSpawnHook.Activate(); });
        Hook(scanner, SigPauseOpenSkipEval, "PauseOpenSkip", "Main Menu fix off", a => { _pauseOpenSkipHook = _hooks.CreateHook<UiConditionFn>(OnPauseOpenSkip, a); _pauseOpenSkipHook.Activate(); });
        Hook(scanner, SigSetTutorialWindow, "SetTutorialWindow", "tutorials online off", a => { _setTutorialHook = _hooks.CreateHook<QuestActionFn>(OnSetTutorialWindow, a); _setTutorialHook.Activate(); });
        Hook(scanner, SigInteractPress, "InteractPress", "interaction log off", a => { _interactPressHook = _hooks.CreateHook<QuestActionFn>(OnInteractPress, a); _interactPressHook.Activate(); });
        Hook(scanner, SigInteractSetState, "InteractSetState", "interaction log off", a => { _interactStateHook = _hooks.CreateHook<QuestActionFn>(OnInteractSetState, a); _interactStateHook.Activate(); });
        Hook(scanner, SigPlacementActivate, "PlacementActivate", "interaction log off", a => { _placementActivateHook = _hooks.CreateHook<QuestActionFn>(OnPlacementActivate, a); _placementActivateHook.Activate(); });
    }

    // ------------------------------------------------------------- tutorials

    private nint OnSetTutorialWindow(nint self, nint a2, nint a3, nint a4)
    {
        var forced = false;
        try
        {
            if (_tutorialsOnline && self != 0 && NetOnline() && IsStory(_lastQuestId) && Marshal.ReadByte(self + TutorialAlsoOnline) == 0)
            {
                Marshal.WriteByte(self + TutorialAlsoOnline, 1);
                forced = true;
                if (Interlocked.Exchange(ref _tutorialLogQuest, _lastQuestId) != _lastQuestId)
                    Write($"TUTORIAL: showing the story's tutorials online too   quest={Categorise(_lastQuestId)}");
            }
        }
        catch { }
        try { return _setTutorialHook.OriginalFunction(self, a2, a3, a4); }
        finally { if (forced) try { Marshal.WriteByte(self + TutorialAlsoOnline, 0); } catch { } }
    }

    // ------------------------------------------------- interaction log only

    private bool InteractLogDue()
    {
        if (!NetOnline() || !IsStory(_lastQuestId)) return false;
        var now = _clock.ElapsedMilliseconds;
        if (now - _interactLogWindow > 60_000) { _interactLogWindow = now; _interactLogCount = 0; }
        return ++_interactLogCount <= 40;   // at most 40 lines a minute
    }

    private nint OnInteractPress(nint self, nint a2, nint a3, nint a4)
    {
        var r = _interactPressHook.OriginalFunction(self, a2, a3, a4);
        try { if (InteractLogDue()) Write($"INTERACT: interact pressed on this PC ({(_isLeader ? "host" : "guest")})   quest={Categorise(_lastQuestId)}"); } catch { }
        return r;
    }

    private nint OnInteractSetState(nint comp, nint state, nint ok, nint a4)
    {
        var r = _interactStateHook.OriginalFunction(comp, state, ok, a4);
        try { if ((int)state == 3 && InteractLogDue()) Write($"INTERACT: target set to 'interact requested' (ok={(byte)ok}) on this PC ({(_isLeader ? "host" : "guest")})   quest={Categorise(_lastQuestId)}"); } catch { }
        return r;
    }

    private nint OnPlacementActivate(nint placement, nint a2, nint a3, nint a4)
    {
        try
        {
            if (placement != 0 && InteractLogDue() && Readable(placement + 0x220, 8))
            {
                var p = Marshal.ReadIntPtr(placement + 0x220);
                var id = p != 0 && Readable(p, 8) ? (ulong)Marshal.ReadInt64(p) : 0;
                Write($"INTERACT: world object {id} became interactable on this PC ({(_isLeader ? "host" : "guest")})   quest={Categorise(_lastQuestId)}");
            }
        }
        catch { }
        return _placementActivateHook.OriginalFunction(placement, a2, a3, a4);
    }

    private void Hook(IStartupScanner scanner, string sig, string name, string missNote, Action<nint> create)
    {
        scanner.AddMainModuleScan(sig, r =>
        {
            if (!r.Found) { Write($"MISS: {name} ({missNote})"); return; }
            var at = _baseAddr + r.Offset;
            Write($"FOUND {name} @ 0x{at:X} (+0x{r.Offset:X})");
            try { create(at); } catch (Exception ex) { Write($"{name} hook failed: {ex.Message}"); }
        });
    }

    // ------------------------------------------------------------ crew spawn

    private bool CrewFixApplies() =>
        _spawnCrewOnline && _crewGateJump != 0 && _partyArrayAddr != 0 && NetOnline() && IsStory(_lastQuestId)
        && _slotReleaseHook != null && _slotRemoveHook != null && _slotSpawnHook != null && _partyDataHook != null;

    private nint OnBeginSection(nint self, nint a2, nint a3, nint a4)
    {
        bool scoped = false;
        try
        {
            if (CrewFixApplies() && !t_crewScope)
            {
                if (_crewQuest != _lastQuestId) { Array.Clear(_crewEntity); _crewQuest = _lastQuestId; }
                var occupied = 0;
                for (int i = 0; i < 4; i++)
                {
                    var e = Marshal.ReadIntPtr(_partyArrayAddr + i * 8);
                    if (e != 0 && e != _crewEntity[i]) occupied |= 1 << i;   // ours stay swappable
                }
                if (SetCrewGate(lifted: true))
                {
                    scoped = true;
                    t_crewScope = true; t_occupied = occupied; t_crewSlot = -1; t_spawned = 0; t_kept = 0;
                }
            }
        }
        catch (Exception ex) { RelayError("crew spawn setup", ex); }

        try { return _beginSectionHook.OriginalFunction(self, a2, a3, a4); }
        finally
        {
            if (scoped)
            {
                t_crewScope = false;
                try
                {
                    if (!SetCrewGate(lifted: false) || !CrewGateIs(_crewGateOriginal))
                    {
                        // Never leave the online skip lifted outside a party step.
                        if (!SetCrewGate(lifted: false) || !CrewGateIs(_crewGateOriginal))
                        {
                            _spawnCrewOnline = false;
                            Write("CREW: could not put the game's code back after a party step; crew join turned off for this session");
                        }
                    }
                    for (int i = 1; i < 4; i++)
                        if ((t_spawned & 1 << i) != 0) _crewEntity[i] = Marshal.ReadIntPtr(_partyArrayAddr + i * 8);
                    if (t_spawned != 0 || t_kept != 0)
                        Write($"CREW: story party step online: spawned {Bits(t_spawned)} into empty slot(s), kept {Bits(t_kept)} as they were   quest={Categorise(_lastQuestId)}");
                }
                catch (Exception ex) { RelayError("crew spawn restore", ex); }
            }
        }
    }

    private static string Bits(int mask)
    {
        var s = new StringBuilder();
        for (int i = 1; i < 4; i++) if ((mask & 1 << i) != 0) s.Append(s.Length > 0 ? "," : "").Append(i);
        return s.Length == 0 ? "none" : s.ToString();
    }

    /// <summary>Lifts (nop) or restores the "online: skip slots 1-3" jump. Only BeginSection reaches it, on one thread.</summary>
    private bool SetCrewGate(bool lifted)
    {
        var want = lifted ? Nop6 : _crewGateOriginal;
        if (!VirtualProtect(_crewGateJump, 6, PAGE_EXECUTE_READWRITE, out var old)) return false;
        try { Marshal.Copy(want, 0, _crewGateJump, 6); }
        finally { VirtualProtect(_crewGateJump, 6, old, out _); }
        return true;
    }

    private bool CrewGateIs(byte[] want)
    {
        var b = new byte[6];
        Marshal.Copy(_crewGateJump, b, 0, 6);
        return b.AsSpan().SequenceEqual(want);
    }

    private bool SlotKept(int slot) => slot >= 1 && slot <= 3 && (t_occupied & 1 << slot) != 0;

    private nint OnPartyData(nint mgr, nint slot, nint a3, nint a4, nint a5, nint a6, nint a7)
    {
        if (t_crewScope && (uint)(int)slot < 4) t_crewSlot = (int)slot;   // 32-bit argument
        return _partyDataHook.OriginalFunction(mgr, slot, a3, a4, a5, a6, a7);
    }

    private nint OnSlotRelease(nint mgr, nint handle)
    {
        if (t_crewScope && SlotKept(t_crewSlot)) { t_kept |= 1 << t_crewSlot; return 0; }
        return _slotReleaseHook.OriginalFunction(mgr, handle);
    }

    private void OnSlotRemove(nint slot)
    {
        if (t_crewScope && SlotKept((int)slot)) { t_kept |= 1 << (int)slot; return; }
        _slotRemoveHook.OriginalFunction(slot);
    }

    private nint OnSlotSpawn(nint mgr, nint name, nint chara, nint a4, nint a5, nint a6)
    {
        if (t_crewScope)
        {
            var slot = PlayerNpcSlot(name);
            if (slot > 0)
            {
                if (SlotKept(slot)) { t_kept |= 1 << slot; return 0; }
                t_spawned |= 1 << slot;
            }
        }
        return _slotSpawnHook.OriginalFunction(mgr, name, chara, a4, a5, a6);
    }

    /// <summary>The slot in a "PlayerNPC&lt;n&gt;" spawn name, or -1 for any other spawn.</summary>
    private static int PlayerNpcSlot(nint name)
    {
        if (name == 0) return -1;
        var b = new byte[11];
        for (int i = 0; i < b.Length; i++) { b[i] = Marshal.ReadByte(name + i); if (b[i] == 0) break; }
        if (Encoding.ASCII.GetString(b, 0, 9) != "PlayerNPC") return -1;
        var d = b[9] - '0';
        return d >= 0 && d <= 3 && b[10] == 0 ? d : -1;
    }

    // ------------------------------------------------------------- main menu

    private byte OnPauseOpenSkip(nint self)
    {
        var result = _pauseOpenSkipHook.OriginalFunction(self);
        try
        {
            if (result == CondFalse && _fullMenuOnline && NetOnline() && IsStory(_lastQuestId))
            {
                var now = _clock.ElapsedMilliseconds;
                if (now - _lastMenuLogMs > 10_000)
                {
                    _lastMenuLogMs = now;
                    Write($"MENU: Main Menu opened online in a story area: building it the full way   quest={Categorise(_lastQuestId)}");
                }
                return CondTrue;
            }
        }
        catch { }
        return result;
    }
}
