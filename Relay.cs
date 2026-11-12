using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Host-to-guest messages over the game's own network sync flags (see
/// <see cref="RelayCodec"/>), and the three features that use them:
/// the leader's cutscene skip applies to everyone, a ready check before a
/// Chapter Select start, and story signals the host gets but the guest
/// misses (the Chapter 7 hole).
///
/// Nothing here opens a connection: every message is a flag change sent by
/// the game's own setter to the players in the current online session.
/// </summary>
public partial class Mod
{
    // ------------------------------------------------------------ signatures

    /// <summary>Sync-flag setter (mgr, player, id, value); also the receive path's sink. 2.0.6 +0x74A4D0 (2.0.5 +0x74A0F0).</summary>
    private const string SigSetSyncFlag =
        "41 57 41 56 56 57 55 53 48 83 EC 28 44 89 CB 44 89 C7 89 D6 49 89 CE 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 89 F1 89 C2";
    /// <summary>QuestReadyIsReady Evaluate: holds the flag manager global, the online-state global and the local-index function. 2.0.6 +0x1D2F320.</summary>
    private const string SigQuestReadyIsReadyEval =
        "56 48 83 EC 20 48 8B 35 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 89 F1 89 C2 45 31 C0 E8 ?? ?? ?? ?? 0F B6 C0 83 F0 03 48 83 C4 20 5E";
    /// <summary>IsOnlineLeader Evaluate: the lobby global and its leader/local index fields. 2.0.6 +0x1D39740.</summary>
    private const string SigIsOnlineLeaderEval =
        "48 8B 05 ?? ?? ?? ?? 48 8D 90 28 C8 06 00 4C 8D 80 2C C8 06 00 4C 8D 88 20 C8 06 00 80 B8 E8 CC 06 00 00";
    /// <summary>"Is a cutscene playing" check; holds the event-player global. 2.0.6 +0x6461D0.</summary>
    private const string SigIsCutPlaying =
        "48 83 EC 28 48 8B 0D ?? ?? ?? ?? 48 85 C9 74 68 80 B9 20 07 00 00 00 74 5F 4C 8B 81 18 07 00 00 45 85 C0 74 2A 4C 89 C0 48 C1 E8 30 75 21 48 8D 54 24 20 E8";
    /// <summary>Button query (device, button, mode, callback) used by the in-engine skip. 2.0.6 +0xA887A0.</summary>
    private const string SigInputPressed =
        "55 41 57 41 56 56 57 53 48 81 EC 98 01 00 00 48 8D AC 24 80 00 00 00 48 C7 85 10 01 00 00 FE FF FF FF 4D 89 CE 44 89 C6 89 D7 B8 BF 00 00 00 85";
    /// <summary>RecvSignal slots 2-4; Evaluate (slot 4, "count reached target") is 0x20 in. 2.0.6 +0x321A4D0.</summary>
    private const string SigRecvSignalEval =
        "80 79 3C 00 74 07 C7 41 38 00 00 00 00 C3 CC CC C7 41 38 00 00 00 00 C3 CC CC CC CC CC CC CC CC 8B 41 20 3B 41 38 0F 96 C0 C3 CC CC CC CC CC CC 80 79 18 00 74 54";
    private const int RecvSignalEvalOffset = 0x20;
    /// <summary>JumpPhaseChapterSelect Execute (vtable slot 9). 2.0.6 +0x1BE8970.</summary>
    private const string SigUiJumpPhaseChapterSelect =
        "55 41 57 41 56 41 55 41 54 56 57 53 48 83 EC 68 48 8D 6C 24 60 48 C7 45 00 FE FF FF FF 80 79 30 00 49 BE 25 23 22 84 E4 9C F2 CB 48 BF B3 01 00";
    /// <summary>FadeIn Execute (slot 9): reads only +0x30 (time) and +0x40 (colour). 2.0.6 +0x1C08790.</summary>
    private const string SigUiFadeIn =
        "56 57 53 48 83 EC 40 48 89 CF 48 63 41 40 48 8D 0D ?? ?? ?? ?? 8B 34 81 48 83 3D ?? ?? ?? ?? 00 74 36 48 8B 0D ?? ?? ?? ?? 48 8D 54 24 38 41 B8 43 90 D9 6F E8";

    [Function(CallingConventions.Microsoft)]
    private delegate void SetSyncFlagFn(nint mgr, nint player, nint id, nint value);
    [Function(CallingConventions.Microsoft)]
    private delegate int LocalIndexFn(nint net);
    [Function(CallingConventions.Microsoft)]
    private delegate nint InputPressedFn(nint device, nint button, nint mode, nint callback);
    [Function(CallingConventions.Microsoft)]
    private delegate nint RecvEvalFn(nint listener);
    [Function(CallingConventions.Microsoft)]
    private delegate void RunCallbacksFn();

    private IHook<SetSyncFlagFn> _setFlagHook;
    private IHook<InputPressedFn> _inputHook;
    private IHook<RecvEvalFn> _recvEvalHook;
    private IHook<UiActionFn> _jumpChapterHook;
    private IHook<RunCallbacksFn> _runCallbacksHook;
    private LocalIndexFn _localIndex;
    private UiActionFn _fadeIn;

    // Addresses of game globals (each holds a pointer).
    private nint _syncMgrPtr, _netPtr, _lobbyPtr, _cutPlayerPtr, _cutHudPtr, _uiMgrPtr;

    // Offsets used (2.0.5 = 2.0.6).
    private const int LobbyAltSel = 0x6CCE8, LobbyLeaderA = 0x6C820, LobbyLeaderB = 0x6C824, LobbyLocalA = 0x6C828, LobbyLocalB = 0x6C82C;
    private const int CutActive = 0x720, CutMode = 0x718, CutHudSkip = 0x46;
    private const int UiSelectedChapter = 0xB34, UiProductionPastSkip = 0xDE2;
    private const int SkipButton = 0x11;

    // ---------------------------------------------------------------- options

    private volatile bool _leaderSkip = true, _readyCheck = true, _relaySignals = true;

    // ------------------------------------------------------------------ state

    // Refreshed by the pump (game main thread); read anywhere.
    private volatile int _localSlot = -1;
    private volatile bool _isLeader, _relayActive;

    private readonly object _relayLock = new();
    private readonly RelayCodec.Decoder[] _decoders = { new(), new(), new(), new() };
    private readonly ConcurrentQueue<(int from, RelayCodec.Frame frame)> _inbox = new();
    private readonly Queue<(int id, bool value)> _outbox = new();
    private readonly long[] _peerSeen = new long[4];          // last HELLO per slot (ms), 0 = never
    private readonly bool[] _peerLeader = new bool[4];
    private readonly (uint key, long at)[] _peerReady = new (uint, long)[4];
    private long _lastHelloSent = -100_000, _pumpCalls, _tokens, _lastTokenMs;
    private int _frameErrorsLogged;

    // Leader skip.
    private long _pendingCutSkip, _pendingStageSkip, _cutSkipWrittenAt, _lastSkipSent = -100_000;
    [ThreadStatic] private static bool t_inSkipStage, t_forcePress, t_pressSeen;

    // Ready check.
    private nint _heldJump, _heldJumpArg;
    private uint _heldKey;
    private long _heldSince, _guestWaitSince;
    private uint _guestWaitKey;
    private int _guestWaitQuest, _heldQuest;
    private const long SkipArmMs = 3000;
    private long _lastErrorLogMs = -100_000;
    private bool ErrorLogDue() { var now = _clock.ElapsedMilliseconds; if (now - _lastErrorLogMs < 10_000) return false; _lastErrorLogMs = now; return true; }
    private const long ReadyTimeoutMs = 60_000, ReadyFreshMs = 120_000, PeerAliveMs = 45_000;

    // Story signals.
    private sealed class PendingSignal { public uint Hash; public int Quest; public long Expires; public readonly HashSet<nint> Delivered = new(); public bool Logged; }
    private readonly List<PendingSignal> _pendingSignals = new();
    private volatile int _pendingSignalCount;
    private readonly Dictionary<uint, long> _ownSignalAt = new();
    private readonly Dictionary<uint, long> _sentSignalAt = new();

    // ------------------------------------------------------------------ setup

    private void RegisterRelayScans(IStartupScanner scanner)
    {
        scanner.AddMainModuleScan(SigQuestReadyIsReadyEval, r =>
        {
            if (!r.Found) { Write("MISS: QuestReadyIsReady (relay off)"); return; }
            var at = _baseAddr + r.Offset;
            _syncMgrPtr = Rip(at, 8, 12);
            _netPtr = Rip(at, 15, 19);
            _localIndex = _hooks.CreateWrapper<LocalIndexFn>((long)Rip(at, 20, 24), out _);
            Write($"FOUND QuestReadyIsReady @ 0x{at:X} (+0x{r.Offset:X}) -> sync flags +0x{_syncMgrPtr - _baseAddr:X}, net +0x{_netPtr - _baseAddr:X}");
        });
        scanner.AddMainModuleScan(SigIsOnlineLeaderEval, r =>
        {
            if (!r.Found) { Write("MISS: IsOnlineLeader (relay off)"); return; }
            var at = _baseAddr + r.Offset;
            _lobbyPtr = Rip(at, 3, 7);
            Write($"FOUND IsOnlineLeader @ 0x{at:X} (+0x{r.Offset:X}) -> lobby +0x{_lobbyPtr - _baseAddr:X}");
        });
        scanner.AddMainModuleScan(SigSetSyncFlag, r =>
        {
            if (!r.Found) { Write("MISS: SetSyncFlag (relay off)"); return; }
            var at = _baseAddr + r.Offset;
            Write($"FOUND SetSyncFlag @ 0x{at:X} (+0x{r.Offset:X})");
            _setFlagHook = _hooks.CreateHook<SetSyncFlagFn>(OnSetSyncFlag, at);
            _setFlagHook.Activate();
        });
        scanner.AddMainModuleScan(SigIsCutPlaying, r =>
        {
            if (!r.Found) { Write("MISS: IsCutPlaying (leader movie skip off)"); return; }
            var at = _baseAddr + r.Offset;
            _cutPlayerPtr = Rip(at, 7, 11);
            Write($"FOUND IsCutPlaying @ 0x{at:X} (+0x{r.Offset:X})");
        });
        scanner.AddMainModuleScan(SigInputPressed, r =>
        {
            if (!r.Found) { Write("MISS: InputPressed (leader in-engine skip off)"); return; }
            var at = _baseAddr + r.Offset;
            Write($"FOUND InputPressed @ 0x{at:X} (+0x{r.Offset:X})");
            _inputHook = _hooks.CreateHook<InputPressedFn>(OnInputPressed, at);
            _inputHook.Activate();
        });
        scanner.AddMainModuleScan(SigRecvSignalEval, r =>
        {
            if (!r.Found) { Write("MISS: RecvSignal Evaluate (story signal relay off)"); return; }
            var at = _baseAddr + r.Offset + RecvSignalEvalOffset;
            Write($"FOUND RecvSignal Evaluate @ 0x{at:X} (+0x{r.Offset + RecvSignalEvalOffset:X})");
            _recvEvalHook = _hooks.CreateHook<RecvEvalFn>(OnRecvSignalEval, at);
            _recvEvalHook.Activate();
        });
        scanner.AddMainModuleScan(SigUiJumpPhaseChapterSelect, r =>
        {
            if (!r.Found) { Write("MISS: JumpPhaseChapterSelect (ready check off)"); return; }
            var at = _baseAddr + r.Offset;
            Write($"FOUND UI JumpPhaseChapterSelect @ 0x{at:X} (+0x{r.Offset:X})");
            _jumpChapterHook = _hooks.CreateHook<UiActionFn>(OnJumpChapterSelect, at);
            _jumpChapterHook.Activate();
        });
        scanner.AddMainModuleScan(SigUiFadeIn, r =>
        {
            if (!r.Found) { Write("MISS: FadeIn (ready check off)"); return; }
            var at = _baseAddr + r.Offset;
            _fadeIn = _hooks.CreateWrapper<UiActionFn>((long)at, out _);
            Write($"FOUND UI FadeIn @ 0x{at:X} (+0x{r.Offset:X})");
        });

        // The pump: Steam's callback runner, called by the game's main loop.
        try
        {
            var steam = GetModuleHandleW("steam_api64.dll");
            var fn = steam != 0 ? GetProcAddress(steam, "SteamAPI_RunCallbacks") : 0;
            if (fn == 0) Write("MISS: SteamAPI_RunCallbacks (relay pump falls back to the lobby check)");
            else
            {
                _runCallbacksHook = _hooks.CreateHook<RunCallbacksFn>(OnRunCallbacks, (long)fn);
                _runCallbacksHook.Activate();
                Write("FOUND relay pump (SteamAPI_RunCallbacks)");
            }
        }
        catch (Exception ex) { Write($"relay pump hook failed: {ex.Message}"); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);

    /// <summary>Target of a RIP-relative operand: disp32 at +dispAt, instruction ends at +end.</summary>
    private static nint Rip(nint at, int dispAt, int end) => at + end + Marshal.ReadInt32(at + dispAt);

    // ------------------------------------------------------------------ roles

    private (bool online, int leader, int local) LobbyRoles()
    {
        if (_lobbyPtr == 0) return (false, -1, -1);
        var g = Marshal.ReadIntPtr(_lobbyPtr);
        if (g == 0) return (false, -1, -1);
        var alt = Marshal.ReadByte(g + LobbyAltSel) != 0;
        var leader = Marshal.ReadInt32(g + (alt ? LobbyLeaderB : LobbyLeaderA));
        var local = Marshal.ReadInt32(g + (alt ? LobbyLocalB : LobbyLocalA));
        return (leader != -1, leader, local);
    }

    private bool NetOnline()
    {
        if (_netPtr == 0) return false;
        var net = Marshal.ReadIntPtr(_netPtr);
        return net != 0 && Marshal.ReadInt32(net + 4) == 3;
    }

    // ------------------------------------------------------------------ pump

    private void OnRunCallbacks()
    {
        _runCallbacksHook.OriginalFunction();
        Interlocked.Increment(ref _pumpCalls);
        Interlocked.Exchange(ref _lastMainPumpMs, _clock.ElapsedMilliseconds);
        Pump();
    }

    private long _lastMainPumpMs = -100_000, _lastFallbackMs, _lastOwnerCheckMs = -100_000;
    private bool _ownsLobbySent;
    private int _pumping;

    /// <summary>
    /// If Steam's callback runner is not being called (hook missing, or the
    /// game stopped calling it), run the pump from the lobby check instead,
    /// at most 60 times a second.
    /// </summary>
    private void FallbackPump()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now - Interlocked.Read(ref _lastMainPumpMs) < 3000 || now - _lastFallbackMs < 16) return;
        _lastFallbackMs = now;
        Pump();
    }

    /// <summary>All relay work that touches game state runs here, on the game's main loop.</summary>
    private void Pump()
    {
        if (Interlocked.Exchange(ref _pumping, 1) == 1) return;
        try
        {
            var now = _clock.ElapsedMilliseconds;
            RefreshRoles();
            DrainInbox(now);
            if (_relayActive && now - _lastOwnerCheckMs >= 1000)
            {
                // The lobby-owner bit in HELLO: re-announce at once when it changes.
                _lastOwnerCheckMs = now;
                var (_, owns) = LobbyOwnership();
                if (owns != _ownsLobbySent) _lastHelloSent = -100_000;
            }
            if (_relayActive && now - _lastHelloSent > 20_000)
            {
                _lastHelloSent = now;
                var (_, ownsLobby) = LobbyOwnership();
                _ownsLobbySent = ownsLobby;
                Send(RelayCodec.TypeHello, new[] { ownsLobby ? RelayCodec.HelloOwnsLobby : (byte)0 });
            }
            FlushOutbox(now);
            PumpCutSkip(now);
            PumpReady(now);
            PumpOpenLater(now);
            PumpAnnounce(now);
        }
        catch (Exception ex) { RelayError("pump", ex); }
        finally { Interlocked.Exchange(ref _pumping, 0); }
    }

    private void RefreshRoles()
    {
        var online = NetOnline();
        var slot = online && _localIndex != null && _netPtr != 0 ? _localIndex(Marshal.ReadIntPtr(_netPtr)) : -1;
        var (inLobby, leader, local) = LobbyRoles();
        _localSlot = slot;
        _isLeader = inLobby && leader == local;
        var active = online && slot >= 0 && slot < 4 && inLobby && _setFlagHook != null && _syncMgrPtr != 0;
        if (active != _relayActive)
        {
            _relayActive = active;
            Write(active
                ? $"RELAY: on (this PC is {(_isLeader ? "the host" : "a guest")}, player slot {slot})"
                : "RELAY: off (not in an online session)");
            if (active) _lastHelloSent = -100_000;
            else { lock (_relayLock) { Array.Clear(_peerSeen); Array.Clear(_peerOwner); _outbox.Clear(); } ForgetOpened(); }
        }
    }

    /// <summary>Queues a frame. Safe from any thread.</summary>
    private void Send(byte type, byte[] payload)
    {
        if (!_relayActive) return;
        var ops = RelayCodec.Encode(_isLeader ? RelayCodec.FlagFromLeader : (byte)0, type, payload);
        lock (_relayLock) foreach (var op in ops) _outbox.Enqueue(op);
    }

    // At most ~200 flag changes a second, so a burst never floods the session.
    private void FlushOutbox(long now)
    {
        if (!_relayActive) return;
        // One token per 5 ms. Only whole tokens advance the clock, so a fast
        // frame rate (under 5 ms a frame) still refills.
        var add = (now - _lastTokenMs) / 5;
        if (add > 0) { _tokens = Math.Min(60, _tokens + add); _lastTokenMs += add * 5; }
        if (_tokens >= 60) _lastTokenMs = now;
        var mgr = Marshal.ReadIntPtr(_syncMgrPtr);
        if (mgr == 0) return;
        while (_tokens > 0)
        {
            (int id, bool value) op;
            lock (_relayLock) { if (_outbox.Count == 0) return; op = _outbox.Dequeue(); }
            _setFlagHook.OriginalFunction(mgr, (nint)_localSlot, (nint)op.id, op.value ? (nint)1 : (nint)0);
            _tokens--;
        }
    }

    /// <summary>Every flag change, local or received, passes here; ours are decoded, all are forwarded.</summary>
    private void OnSetSyncFlag(nint mgr, nint player, nint id, nint value)
    {
        RelayCodec.Frame interact = null;
        try
        {
            var flag = (int)(id & 0xFFFF);
            var from = (int)player;
            if (RelayCodec.IsRelayId(flag) && from >= 0 && from < 4 && from != _localSlot)
            {
                RelayCodec.Frame frame;
                int errors;
                lock (_relayLock)
                {
                    frame = _decoders[from].Feed(flag, (value & 0xFF) != 0);
                    errors = _decoders[from].Errors;
                }
                if (frame != null && frame.Type == RelayCodec.TypeInteract) interact = frame;
                else if (frame != null) _inbox.Enqueue((from, frame));
                if (errors > _frameErrorsLogged && Interlocked.Exchange(ref _frameErrorsLogged, errors) < errors && ErrorLogDue())
                    Write($"RELAY: dropped a damaged message from player slot {from} (total {errors})");
            }
        }
        catch { }
        _setFlagHook.OriginalFunction(mgr, player, id, value);
        // A guest's press runs where the game runs its own request: in this receive path.
        if (interact != null) try { RunRelayedInteract((int)player, interact.Payload); } catch (Exception ex) { RelayError("relayed interaction", ex); }
    }

    private void DrainInbox(long now)
    {
        while (_inbox.TryDequeue(out var m))
        {
            var (from, f) = m;
            var p = f.Payload;
            switch (f.Type)
            {
                case RelayCodec.TypeHello:
                    var fresh = _peerSeen[from] == 0 || now - _peerSeen[from] > PeerAliveMs;
                    if (fresh) _peerReady[from] = (0, 0);   // a slot reused by someone new starts not ready
                    _peerSeen[from] = Math.Max(1, now); _peerLeader[from] = f.FromLeader;   // 0 means "never"
                    _peerOwner[from] = p.Length >= 1 && (p[0] & RelayCodec.HelloOwnsLobby) != 0;
                    if (fresh)
                    {
                        Write($"RELAY: player slot {from} has the mod{(f.FromLeader ? " (host)" : "")}");
                        _lastHelloSent = -100_000;   // answer so they know about us too
                    }
                    break;
                case RelayCodec.TypeSkipCut when f.FromLeader && p.Length >= 4 && !_isLeader:
                    if (_leaderSkip && (int)RelayCodec.ReadU32(p, 0) == _lastQuestId) _pendingCutSkip = now + SkipArmMs;
                    break;
                case RelayCodec.TypeSkipStage when f.FromLeader && p.Length >= 4 && !_isLeader:
                    if (_leaderSkip && (int)RelayCodec.ReadU32(p, 0) == _lastQuestId) Interlocked.Exchange(ref _pendingStageSkip, now + SkipArmMs);
                    break;
                case RelayCodec.TypeReady when p.Length >= 4 && _isLeader:
                    _peerReady[from] = (RelayCodec.ReadU32(p, 0), Math.Max(1, now));
                    Write($"READY: player slot {from} is ready (Chapter Select key 0x{RelayCodec.ReadU32(p, 0):X})");
                    break;
                case RelayCodec.TypeWaiting when f.FromLeader && p.Length >= 4 && !_isLeader:
                    Write($"READY: the host is waiting for you: pick the same Chapter Select part (key 0x{RelayCodec.ReadU32(p, 0):X})");
                    break;
                case RelayCodec.TypeSignal when f.FromLeader && p.Length >= 8 && !_isLeader:
                    QueueRelayedSignal((int)RelayCodec.ReadU32(p, 0), RelayCodec.ReadU32(p, 4), now);
                    break;
            }
        }
    }

    // ------------------------------------------------------------ leader skip

    /// <summary>Host: a movie/event cut was skipped here (called from the SkipCut hook).</summary>
    private void RelayCutSkip(nint skipCutSelf)
    {
        if (!_leaderSkip || !_relayActive || !_isLeader || !IsStory(_lastQuestId)) return;
        if (Marshal.ReadByte(skipCutSelf + 0x30) == 0) return;   // SkipCut(skip=false) cancels, it does not skip
        Send(RelayCodec.TypeSkipCut, RelayCodec.U32((uint)_lastQuestId));
        Write($"LEADER SKIP: told the others to skip this cutscene too   quest={Categorise(_lastQuestId)}");
    }

    private bool CutPlaying()
    {
        if (_cutPlayerPtr == 0) return false;
        var cut = Marshal.ReadIntPtr(_cutPlayerPtr);
        if (cut == 0 || Marshal.ReadByte(cut + CutActive) == 0) return false;
        var mode = Marshal.ReadInt32(cut + CutMode);
        return mode == 1 || mode == 5;
    }

    /// <summary>Guest: perform a relayed movie/event skip, once, only while one plays.</summary>
    private void PumpCutSkip(long now)
    {
        if (_cutHudPtr == 0) return;
        var hud = Marshal.ReadIntPtr(_cutHudPtr);
        if (hud == 0) return;
        if (_pendingCutSkip > now && CutPlaying())
        {
            _pendingCutSkip = 0;
            Marshal.WriteByte(hud + CutHudSkip, 1);
            _cutSkipWrittenAt = now;
            Write($"LEADER SKIP: the host skipped, so this cutscene was skipped here too   quest={Categorise(_lastQuestId)}");
        }
        // The game clears the flag when the skip completes. If the cut ended
        // first, clear it ourselves: a stuck flag keeps the HUD hidden.
        if (_cutSkipWrittenAt != 0 && now - _cutSkipWrittenAt > 2000 && !CutPlaying())
        {
            if (Marshal.ReadByte(hud + CutHudSkip) != 0) Marshal.WriteByte(hud + CutHudSkip, 0);
            _cutSkipWrittenAt = 0;
        }
    }

    /// <summary>
    /// The skip action polls the skip button every frame while a skippable
    /// in-engine scene plays. On the host a real press is relayed; on a guest a
    /// relayed skip is a simulated press, so the game's own skip logic (and its
    /// own "too late to skip" gate) does the rest.
    /// </summary>
    private nint RunSkipStage(nint self)
    {
        var force = !_isLeader && Interlocked.Read(ref _pendingStageSkip) > _clock.ElapsedMilliseconds;
        t_inSkipStage = true; t_forcePress = force; t_pressSeen = false;
        nint result;
        try { result = _skipStageHook.OriginalFunction(self); }
        finally { t_inSkipStage = false; t_forcePress = false; }
        if (!t_pressSeen) return result;
        try { AfterSkipPress(force); } catch (Exception ex) { RelayError("skip", ex); }
        return result;
    }

    private void AfterSkipPress(bool force)
    {
        var pastWindow = _uiMgrPtr != 0 && Marshal.ReadIntPtr(_uiMgrPtr) is var ui && ui != 0 && Marshal.ReadByte(ui + UiProductionPastSkip) != 0;
        if (force)
        {
            Interlocked.Exchange(ref _pendingStageSkip, 0);
            Write($"LEADER SKIP: the host skipped, so this scene was skipped here too{(pastWindow ? " (too late, the scene could not be skipped)" : "")}   quest={Categorise(_lastQuestId)}");
        }
        else if (!pastWindow)
        {
            // A held button reads as pressed for several frames: one skip.
            var now = _clock.ElapsedMilliseconds;
            if (now - _lastSkipSent <= 1000) return;
            _lastSkipSent = now;
            Write($"CUTSCENE: in-engine scene skipped on this PC   quest={Categorise(_lastQuestId)} inLobby={_lastLobbyResult}");
            if (_leaderSkip && _relayActive && _isLeader && IsStory(_lastQuestId))
            {
                Send(RelayCodec.TypeSkipStage, RelayCodec.U32((uint)_lastQuestId));
                Write($"LEADER SKIP: told the others to skip this scene too   quest={Categorise(_lastQuestId)}");
            }
        }
    }

    private nint OnInputPressed(nint device, nint button, nint mode, nint callback)
    {
        var r = _inputHook.OriginalFunction(device, button, mode, callback);
        if (t_inSkipStage && (int)button == SkipButton)
        {
            if (t_forcePress) r = (r & ~(nint)0xFF) | 1;
            if ((r & 0xFF) != 0) t_pressSeen = true;
        }
        return r;
    }

    // ------------------------------------------------------------ ready check

    private bool PeerAlive(int slot, long now) => _peerSeen[slot] != 0 && now - _peerSeen[slot] <= PeerAliveMs;

    /// <summary>
    /// The host's Chapter Select start is held until every guest that has the
    /// mod has picked the same part ("Ready"). A guest's own pick is its ready
    /// answer and does not start anything. Offline, or with nobody else
    /// running the mod, the start runs as normal.
    /// </summary>
    private void OnJumpChapterSelect(nint self, nint arg)
    {
        bool passThrough = true;
        try { passThrough = !DecideChapterJump(self, arg); }
        catch (Exception ex) { RelayError("ready check", ex); passThrough = true; }
        if (passThrough) _jumpChapterHook.OriginalFunction(self, arg);
    }

    /// <summary>True when the mod took over the jump (held or turned into a ready answer).</summary>
    private bool DecideChapterJump(nint self, nint arg)
    {
        if (!_readyCheck || !_relayActive || _uiMgrPtr == 0) return false;
        if (Marshal.ReadByte(self + 0x30) != 0) return false;   // "replay the previous part": not a pick
        var ui = Marshal.ReadIntPtr(_uiMgrPtr);
        if (ui == 0) return false;
        var key = (uint)Marshal.ReadInt32(ui + UiSelectedChapter);
        var now = _clock.ElapsedMilliseconds;

        if (!_isLeader)
        {
            var hostHasMod = false;
            for (int i = 0; i < 4; i++) if (i != _localSlot && _peerLeader[i] && PeerAlive(i, now)) hostHasMod = true;
            if (!hostHasMod) return false;
            Send(RelayCodec.TypeReady, RelayCodec.U32(key));
            _guestWaitSince = Math.Max(1, now); _guestWaitKey = key; _guestWaitQuest = _lastQuestId;
            Write($"READY: told the host you are ready (Chapter Select key 0x{key:X}); waiting for the host to start");
            return true;
        }

        if (_heldJump != 0)
        {
            // The host picked again while waiting. The same part: start now,
            // without the others (the game's own jump runs). Another part:
            // that pick replaces the held one.
            var same = key == _heldKey;
            DropHeld();
            if (same) { Write("READY: host picked the same part again, starting without waiting"); return false; }
        }
        var waiting = GuestsNotReady(key, now);
        if (waiting.total == 0) return false;               // nobody else runs the mod
        if (waiting.notReady == 0) { Write("READY: everyone is ready, starting"); ClearReady(); return false; }

        var copy = Marshal.AllocHGlobal(0x80);
        for (int i = 0; i < 0x80; i += 8) Marshal.WriteInt64(copy, i, i < 0x40 ? Marshal.ReadInt64(self + i) : 0);
        Marshal.WriteByte(copy + 0x30, 0);
        _heldJump = copy; _heldJumpArg = arg; _heldKey = key; _heldSince = now; _heldQuest = _lastQuestId;
        Send(RelayCodec.TypeWaiting, RelayCodec.U32(key));
        Write($"READY: waiting for {waiting.notReady} of {waiting.total} player(s) to pick the same part (key 0x{key:X}); 60 s");
        return true;
    }

    private (int total, int notReady) GuestsNotReady(uint key, long now)
    {
        int total = 0, notReady = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == _localSlot || !PeerAlive(i, now)) continue;
            total++;
            var r = _peerReady[i];
            if (r.key != key || r.at == 0 || now - r.at > ReadyFreshMs) notReady++;
        }
        return (total, notReady);
    }

    private void ClearReady() { for (int i = 0; i < 4; i++) _peerReady[i] = (0, 0); }

    private void PumpReady(long now)
    {
        if (_heldJump != 0)
        {
            var w = GuestsNotReady(_heldKey, now);
            if (!_relayActive || _lastQuestId != _heldQuest)
            {
                // The session ended or the game moved on: never start late.
                Write("READY: the session or area changed while waiting; cancelled");
                DropHeld(); FadeBackIn();
            }
            else if (w.notReady == 0)
            {
                Write(w.total == 0 ? "READY: nobody left to wait for, starting" : "READY: everyone is ready, starting");
                ReleaseHeld();
            }
            else if (now - _heldSince > ReadyTimeoutMs)
            {
                Write($"READY: {w.notReady} player(s) did not pick the part in time; cancelled (pick it again to retry)");
                DropHeld(); FadeBackIn();
            }
        }
        if (_guestWaitSince != 0)
        {
            if (_lastQuestId != _guestWaitQuest)
                _guestWaitSince = 0;          // the host started and the game pulled this PC in
            else if (now - _guestWaitSince > ReadyTimeoutMs || !_relayActive)
            {
                _guestWaitSince = 0;
                Write("READY: the host did not start in time; you are back in town");
                FadeBackIn();
            }
        }
    }

    private void ReleaseHeld()
    {
        var copy = _heldJump; var arg = _heldJumpArg; var key = _heldKey;
        _heldJump = 0;
        ClearReady();
        var ui = Marshal.ReadIntPtr(_uiMgrPtr);
        if (ui != 0) Marshal.WriteInt32(ui + UiSelectedChapter, (int)key);
        _jumpChapterHook.OriginalFunction(copy, arg);
        // The copy is not freed: the game might keep the pointer for the frame.
    }

    private void DropHeld() { _heldJump = 0; ClearReady(); }

    private void FadeBackIn()
    {
        if (_fadeIn == null) return;
        var fake = Marshal.AllocHGlobal(0x80);
        for (int i = 0; i < 0x80; i += 8) Marshal.WriteInt64(fake, i, 0);
        Marshal.WriteInt32(fake + 0x30, 20);   // frames
        Marshal.WriteInt32(fake + 0x40, 0);    // black
        _fadeIn(fake, 0);
    }

    // ---------------------------------------------------------- story signals

    /// <summary>Host: a story signal reached a listener here (called from the RecvSignal hook).</summary>
    private void RelaySignal(long type, ulong hash)
    {
        if (!_relaySignals || !_relayActive || !_isLeader || type != 1) return;
        var quest = _lastQuestId;
        if (!IsStory(quest)) return;
        var h = (uint)hash; var now = _clock.ElapsedMilliseconds;
        lock (_relayLock)
        {
            if (_sentSignalAt.TryGetValue(h, out var at) && now - at < 250) return;   // one raise reaching several listeners at once
            _sentSignalAt[h] = now;
        }
        Send(RelayCodec.TypeSignal, RelayCodec.U32((uint)quest, h));
    }

    /// <summary>Guest: its own copy of a signal arrived, so a relayed one would count twice.</summary>
    private void NoteOwnSignal(ulong hash)
    {
        var h = (uint)hash; var now = _clock.ElapsedMilliseconds;
        lock (_relayLock)
        {
            _ownSignalAt[h] = now;
            _pendingSignals.RemoveAll(p => p.Hash == h);
            _pendingSignalCount = _pendingSignals.Count;
        }
    }

    private void QueueRelayedSignal(int quest, uint hash, long now)
    {
        if (!_relaySignals || quest != _lastQuestId) return;
        lock (_relayLock)
        {
            if (_ownSignalAt.TryGetValue(hash, out var own) && now - own < 5000) return;
            _pendingSignals.RemoveAll(p => p.Expires < now);
            _pendingSignals.Add(new PendingSignal { Hash = hash, Quest = quest, Expires = now + 20_000 });
            _pendingSignalCount = _pendingSignals.Count;
        }
    }

    /// <summary>
    /// A waiting story listener checks "has my signal arrived" every frame. If
    /// the host relayed the signal it waits for, count it as delivered, the way
    /// the game's own delivery does (count +1). Same thread as the game's own.
    /// </summary>
    private nint OnRecvSignalEval(nint listener)
    {
        if (_pendingSignalCount > 0)
        {
            try { InjectRelayed(listener); } catch { }
        }
        return _recvEvalHook.OriginalFunction(listener);
    }

    private void InjectRelayed(nint listener)
    {
        var expected = Marshal.ReadIntPtr(listener + 0x28);
        if (expected == 0 || Marshal.ReadInt64(expected + 0x08) != 1) return;
        var h = (uint)Marshal.ReadInt64(expected + 0x20);
        var now = _clock.ElapsedMilliseconds;
        string line = null;
        lock (_relayLock)
        {
            foreach (var p in _pendingSignals)
            {
                if (p.Hash != h || p.Expires < now || p.Quest != _lastQuestId || p.Delivered.Contains(listener)) continue;
                var count = Marshal.ReadInt32(listener + 0x38);
                if ((uint)count >= (uint)Marshal.ReadInt32(listener + 0x20)) continue;
                Marshal.WriteInt32(listener + 0x38, count + 1);
                p.Delivered.Add(listener);
                if (!p.Logged) { p.Logged = true; line = $"STORY RELAY: the host's story signal (hash low 0x{h:X8}) reached this PC   quest={Categorise(_lastQuestId)}"; }
            }
        }
        if (line != null) Write(line);
    }

    /// <summary>For the heartbeat: on/off, role, other modded players, pump calls.</summary>
    private string RelaySummary()
    {
        if (!_relayActive) return "off";
        var now = _clock.ElapsedMilliseconds; var peers = 0;
        for (int i = 0; i < 4; i++) if (i != _localSlot && PeerAlive(i, now)) peers++;
        return $"{(_isLeader ? "host" : "guest")},peers={peers},pump={Interlocked.Read(ref _pumpCalls)}";
    }

    private void RelayError(string where, Exception ex)
    {
        if (Interlocked.Increment(ref _relayErrors) <= 5) Write($"RELAY error in {where}: {ex.GetType().Name}: {ex.Message}");
    }
    private int _relayErrors;
}
