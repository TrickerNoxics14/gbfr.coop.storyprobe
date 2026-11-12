using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Guests can use story objects online (talk to Rackam, Depart, quest objects).
///
/// The game already has the whole guest-to-host design: a guest's press on a
/// world object (C::Interact, 2.0.6 +0x219D130) runs nothing on the guest and
/// sends the host a request (network type 2, subtype 0x44); the host's
/// receiver (+0xB9FBC0) runs the same function as if the host had pressed,
/// and the result reaches everyone. Two things stop it in story areas:
///
/// 1. <b>The guest never gets the prompt.</b> A world object's interaction
///    (+0x96E100) waits on a guest until the host announces the object
///    (placement+0x235). For an "Interact" object, once the host is known to
///    run this mod, the guest stops waiting, so the prompt appears.
/// 2. <b>The request can be lost.</b> The host's message filter drops a 0x44
///    from a player in some states, and nothing is sent when the object has no
///    network id. The guest's mod also sends the same request over the relay;
///    the host's mod hands it to the game's own receiver.
///
/// 3. <b>The host says it again (0.29, for guests WITHOUT the mod).</b> The host
///    announces an object to the guests once (network type 10 / 1, the object's
///    id) and a guest that had not set the object up yet never hears it. The
///    host's mod repeats that same message for every Interact object that is
///    ready on the host, so any guest, modded or not (a console player), gets
///    the prompt and its press reaches the host as the game's own request.
///
/// On the host every request (the game's own or relayed) passes a gate first:
/// the pressing player must exist on the host (the game's own receiver would
/// crash otherwise), the object must be ready on the host (enabled), and one
/// press runs once (both copies arrive).
/// </summary>
public partial class Mod
{
    // ------------------------------------------------------------ signatures

    /// <summary>C::Interact(C, user entity, fromNetwork). 2.0.6 +0x219D130, 2.0.5 +0x219CB10.</summary>
    private const string SigCInteract =
        "41 57 41 56 41 55 41 54 56 57 53 48 81 EC C0 00 00 00 44 89 C3 48 89 D6 48 89 CF 48 8B 01 FF 50 78 84 C0 0F 84";
    /// <summary>Receiver of the guest's request (type 2 / 0x44). 2.0.6 +0xB9FBC0, 2.0.5 +0xB9F7B0.</summary>
    private const string SigInteractRequestRecv =
        "56 57 48 81 EC 88 00 00 00 C5 F8 29 74 24 70 48 89 CE C7 44 24 38 00 00 00 00 C5 C8 57 F6 C5 F8 11 74 24 40 48 8B 49 10 48 8D 54 24 38 E8";
    /// <summary>stage::placement::Interact vtable slot 9 (start). 2.0.6 +0x3A98DD0; identifies Interact objects.</summary>
    private const string SigInteractSlot9 =
        "55 56 48 83 EC 68 48 8D 6C 24 60 48 C7 45 00 FE FF FF FF 48 89 CE 83 B9";
    /// <summary>A world object starts its interaction (placement, callback). 2.0.6 +0x96E100, 2.0.5 +0x96DD20.</summary>
    private const string SigPlacementActivate =
        "55 41 56 56 57 53 48 81 EC 80 00 00 00 48 8D AC 24 80 00 00 00 48 C7 45 F8 FE FF FF FF 48 83 B9 10 03 00 00 00 0F 85 ?? ?? ?? ?? 48 89 CE";

    // Calls and globals read from inside the two functions (same offsets in 2.0.5 and 2.0.6).
    internal const int RecvNetIdCall = 0x2D, RecvEntityTable = 0x3A, RecvCompCall = 0xBF, RecvTailJump = 0x1AA;
    internal const int CInteractLobbyHolder = 0x63, CInteractNetCompCall = 0x151;
    /// <summary>C::Interact's own network send: <c>mov rax,[net interface]</c>; <c>[rax+0x38]</c> object, its slot 0x80 sends a message.</summary>
    internal const int CInteractNetIface = 0x256;
    internal const int CInteractNetIfaceUse = 0x25D;

    // Object layout (2.0.5 = 2.0.6).
    private const int PlacementCallback = 0x310, PlacementAllowed = 0x235, PlacementComp = 0x3E8, PlacementId = 0x220;
    private const int PlacementStatus = 0x134, PlacementMember = 0x200, PlacementLinked = 0x3F0;
    private const int CompEnabled = 0x68;
    private const int EntityHandle = 0x38, EntityGame = 0x70, PlayerNetValid = 0x4358, PlayerNetId = 0x4360, PlayerNetIndex = 0x4368;

    [Function(CallingConventions.Microsoft)]
    private delegate nint CInteractFn(nint comp, nint user, nint fromNetwork);
    [Function(CallingConventions.Microsoft)]
    private delegate nint InteractRecvFn(nint payload);
    [Function(CallingConventions.Microsoft)]
    private delegate nint NetIdToHandleFn(nint netId, nint outHandle);
    [Function(CallingConventions.Microsoft)]
    private delegate nint FromHandleFn(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint NativeVFn(nint self, nint arg);

    private IHook<CInteractFn> _cInteractHook;
    private IHook<InteractRecvFn> _interactRecvHook;
    private IHook<QuestActionFn> _placementActivateHook;
    private NetIdToHandleFn _netIdToHandle;
    private FromHandleFn _compFromHandle, _netCompOf;
    private nint _entityTablePtr, _lobbyHolderPtr, _interactSlot9;

    /// <summary>Calls a game object's virtual function (self, slot offset, arg). Replaced by the audit.</summary>
    private Func<nint, int, nint, nint> _vcall = NativeVCall;
    private static readonly Dictionary<nint, NativeVFn> VFns = new();

    private volatile bool _guestInteract = true;

    private readonly bool[] _peerOwner = new bool[4];        // from HELLO: that player owns the lobby
    private readonly object _interactLock = new();
    private readonly Dictionary<long, long> _interactRunAt = new();   // host: object net id -> last run (ms)
    private long _lastGuestSendId, _lastGuestSendMs = -100_000;
    private readonly List<nint> _opened = new();             // guest: objects this mod opened (this area)
    private readonly List<nint> _openLater = new();          // guest: waiting for the host's HELLO
    private int _openedQuest = -1;
    private const int MaxTracked = 512;

    // Host: Interact objects seen starting this area, and when each was last announced to the guests.
    internal const int AnnounceBatch = 10, AnnounceTickMs = 1000, AnnounceFirstMs = 1000, AnnounceFastMs = 3000, AnnounceFastForMs = 90_000, AnnounceSlowMs = 30_000;
    private nint _netIfacePtr;
    private readonly List<(nint p, ulong id)> _announceObjs = new();
    private readonly Dictionary<ulong, long> _announceReady = new();   // object id -> first seen ready on this host
    private readonly Dictionary<ulong, long> _announceLast = new();    // object id -> last announced (ms)
    private int _announceQuest = -1;
    private long _announceTickMs = -100_000;

    [ThreadStatic] private static nint t_scratch;
    [ThreadStatic] private static bool t_userChecked;

    // ------------------------------------------------------------------ setup

    private void RegisterInteractScans(IStartupScanner scanner)
    {
        Hook(scanner, SigInteractSlot9, "Interact start (slot 9)", "guest interaction off", a => _interactSlot9 = a);
        Hook(scanner, SigPlacementActivate, "PlacementActivate", "guest interaction off", a =>
        {
            _placementActivateHook = _hooks.CreateHook<QuestActionFn>(OnPlacementActivate, a);
            _placementActivateHook.Activate();
        });
        Hook(scanner, SigInteractRequestRecv, "InteractRequestRecv", "guest interaction off", a =>
        {
            if (Marshal.ReadByte(a + RecvNetIdCall) != 0xE8 || Marshal.ReadByte(a + RecvCompCall) != 0xE8 || Marshal.ReadByte(a + RecvTailJump) != 0xE9
                || Marshal.ReadInt16(a + RecvEntityTable) != unchecked((short)0x8B48))
            { Write("InteractRequestRecv: unexpected code, guest interaction off"); return; }
            _netIdToHandle = _hooks.CreateWrapper<NetIdToHandleFn>((long)Rip(a, RecvNetIdCall + 1, RecvNetIdCall + 5), out _);
            _compFromHandle = _hooks.CreateWrapper<FromHandleFn>((long)Rip(a, RecvCompCall + 1, RecvCompCall + 5), out _);
            _entityTablePtr = Rip(a, RecvEntityTable + 3, RecvEntityTable + 7);
            _interactRecvHook = _hooks.CreateHook<InteractRecvFn>(OnInteractRequestRecv, a);
            _interactRecvHook.Activate();
        });
        Hook(scanner, SigCInteract, "C::Interact", "guest interaction off", a =>
        {
            if (Marshal.ReadByte(a + CInteractNetCompCall) != 0xE8 || Marshal.ReadInt16(a + CInteractLobbyHolder) != unchecked((short)0x8B48))
            { Write("C::Interact: unexpected code, guest interaction off"); return; }
            _netCompOf = _hooks.CreateWrapper<FromHandleFn>((long)Rip(a, CInteractNetCompCall + 1, CInteractNetCompCall + 5), out _);
            _lobbyHolderPtr = Rip(a, CInteractLobbyHolder + 3, CInteractLobbyHolder + 7);
            // The game's network interface, read where C::Interact itself sends its request. Only the host announcement needs it.
            if (Marshal.ReadInt16(a + CInteractNetIface) == unchecked((short)0x8B48) && Marshal.ReadByte(a + CInteractNetIface + 2) == 0x05
                && Marshal.ReadInt32(a + CInteractNetIfaceUse) == 0x38488B48)
                _netIfacePtr = Rip(a, CInteractNetIface + 3, CInteractNetIface + 7);
            else Write("C::Interact: network interface not where expected, host announcement off");
            _cInteractHook = _hooks.CreateHook<CInteractFn>(OnCInteract, a);
            _cInteractHook.Activate();
        });
    }

    private bool GuestInteractReady() =>
        _guestInteract && _cInteractHook != null && _interactRecvHook != null && _placementActivateHook != null
        && _interactSlot9 != 0 && _lobbyHolderPtr != 0 && _entityTablePtr != 0 && _netPtr != 0;

    // ---------------------------------------------------------------- helpers

    private static nint NativeVCall(nint self, int slot, nint arg)
    {
        var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(self) + slot);
        NativeVFn d;
        lock (VFns) if (!VFns.TryGetValue(fn, out d)) VFns[fn] = d = Marshal.GetDelegateForFunctionPointer<NativeVFn>(fn);
        return d(self, arg);
    }

    private long _interactLogWindow; private int _interactLogCount;

    internal const int InteractLinesPerMinute = 25;   // the audit flags any line repeated more than 30 times a minute

    /// <summary>INTERACT lines: online only (story areas unless <paramref name="anyArea"/>), at most 25 a minute.</summary>
    private bool InteractLogDue(bool anyArea = false)
    {
        if (!NetOnline() || (!anyArea && !IsStory(_lastQuestId))) return false;
        var now = _clock.ElapsedMilliseconds;
        lock (_interactLock)
        {
            if (now - _interactLogWindow > 60_000) { _interactLogWindow = now; _interactLogCount = 0; }
            return ++_interactLogCount <= InteractLinesPerMinute;
        }
    }

    private static nint Ptr(nint at) => at != 0 && Readable(at, 8) ? Marshal.ReadIntPtr(at) : 0;

    /// <summary>The game's own "who am I in this lobby" test, as C::Interact makes it: (joined, owns the lobby).</summary>
    private (bool joined, bool owner) LobbyOwnership()
    {
        var net = Ptr(_netPtr);
        if (net == 0 || !Readable(net + 4, 4) || Marshal.ReadInt32(net + 4) != 3) return (false, false);
        var flag = Ptr(net + 0x4A0);
        if (flag == 0 || Marshal.ReadByte(flag) == 0) return (false, false);
        var lobby = Ptr(Ptr(Ptr(_lobbyHolderPtr) + 0x18) + 0x68);
        if (lobby == 0 || Ptr(lobby) == 0) return (false, false);
        if ((byte)_vcall(lobby, 0x108, 0) == 0) return (false, false);
        return (true, (byte)_vcall(lobby, 0x110, 0) != 0);
    }

    private bool OwnerHasMod(long now)
    {
        for (int i = 0; i < 4; i++) if (i != _localSlot && _peerOwner[i] && PeerAlive(i, now)) return true;
        return false;
    }

    private static nint Scratch()
    {
        if (t_scratch == 0) t_scratch = Marshal.AllocHGlobal(0x40);
        for (int i = 0; i < 0x40; i += 8) Marshal.WriteInt64(t_scratch, i, 0);
        return t_scratch;
    }

    /// <summary>Net id -> entity, validated exactly like the game's receiver does (0 if unknown here).</summary>
    private nint EntityOfNetId(long netId)
    {
        if (netId == -1 || _netIdToHandle == null) return 0;
        var h = Scratch();
        _netIdToHandle((nint)netId, h);
        var idx = Marshal.ReadInt32(h);
        if (idx <= 0) return 0;
        var tbl = Ptr(_entityTablePtr);
        var extras = Ptr(tbl + 0x48); var ents = Ptr(tbl + 0x20);
        if (extras == 0 || ents == 0 || !Readable(extras + (idx - 1) * 8, 8) || !Readable(ents + (idx - 1) * 8, 8)) return 0;
        if (Marshal.ReadInt64(extras + (idx - 1) * 8) != Marshal.ReadInt64(h + 0x10)) return 0;
        var e = Marshal.ReadIntPtr(ents + (idx - 1) * 8);
        return e != 0 && e == Marshal.ReadIntPtr(h + 8) ? e : 0;
    }

    private nint CompOfNetId(long netId)
    {
        if (netId == -1 || _netIdToHandle == null || _compFromHandle == null) return 0;
        var h = Scratch();
        _netIdToHandle((nint)netId, h);
        return Marshal.ReadInt32(h) == 0 ? 0 : _compFromHandle(h);
    }

    /// <summary>The object's network id the way C::Interact reads it; -1 if it has none.</summary>
    private long ObjectNetId(nint comp)
    {
        var w = Ptr(comp + 8);
        var e = Ptr(Ptr(Ptr(w) + 0x28) + 0x18);
        if (e == 0 || !Readable(e + EntityHandle, 0x18) || _netCompOf == null) return -1;
        var h = Scratch();
        for (int i = 0; i < 0x18; i += 8) Marshal.WriteInt64(h, i, Marshal.ReadInt64(e + EntityHandle + i));
        var n = _netCompOf(h);
        if (n == 0 || !Readable(n + 8, 0x10) || Marshal.ReadIntPtr(n + 8) == 0) return -1;
        return Marshal.ReadInt64(n + 0x10);
    }

    /// <summary>The pressing player's network id; -1 if it has none.</summary>
    private static long PlayerNetIdOf(nint user)
    {
        var g = Ptr(user + EntityGame);
        if (g == 0 || !Readable(g + PlayerNetValid, 0x18) || Marshal.ReadInt64(g + PlayerNetValid) == 0) return -1;
        return Marshal.ReadInt64(g + PlayerNetId);
    }

    /// <summary>
    /// The lists hold raw object addresses: they are dropped on every quest
    /// change and whenever the relay goes off. Within a quest each use
    /// re-checks the object (readable, still an Interact object). The map id
    /// is not used: it is read on a 3 s timer and lags behind the objects.
    /// </summary>
    private void ResetOpenedIfAreaChanged()
    {
        if (_openedQuest == _lastQuestId) return;
        _openedQuest = _lastQuestId; _opened.Clear(); _openLater.Clear();
    }

    private void ForgetOpened() { lock (_interactLock) { _opened.Clear(); _openLater.Clear(); _openedQuest = -1; } }

    private bool IsInteractObject(nint p)
    {
        if (p == 0 || !Readable(p, PlacementComp + 8)) return false;
        var vt = Marshal.ReadIntPtr(p);
        return vt != 0 && Readable(vt + 9 * 8, 8) && Marshal.ReadIntPtr(vt + 9 * 8) == _interactSlot9;
    }

    // ------------------------------------------------- guest: the prompt shows

    private nint OnPlacementActivate(nint placement, nint a2, nint a3, nint a4)
    {
        var r = _placementActivateHook.OriginalFunction(placement, a2, a3, a4);
        try { OpenForGuest(placement, _clock.ElapsedMilliseconds); } catch (Exception ex) { RelayError("guest interaction", ex); }
        try { TrackForAnnounce(placement); } catch (Exception ex) { RelayError("host announcement", ex); }
        return r;
    }

    /// <summary>True for an Interact object that, on this guest, still waits for the host's announcement.</summary>
    private bool WaitingInteract(nint p)
    {
        if (!IsInteractObject(p)) return false;
        return Marshal.ReadIntPtr(p + PlacementCallback) != 0 && Marshal.ReadByte(p + PlacementAllowed) == 0;
    }

    private void OpenForGuest(nint p, long now)
    {
        if (!GuestInteractReady() || !_relayActive || !IsStory(_lastQuestId) || !NetOnline() || !WaitingInteract(p)) return;
        var (joined, owner) = LobbyOwnership();
        if (!joined || owner) return;
        lock (_interactLock)
        {
            ResetOpenedIfAreaChanged();
            if (!OwnerHasMod(now)) { if (_openLater.Count < MaxTracked && !_openLater.Contains(p)) _openLater.Add(p); return; }
            Open(p);
        }
    }

    private void Open(nint p)
    {
        Marshal.WriteByte(p + PlacementAllowed, 1);
        if (_opened.Count < MaxTracked) _opened.Add(p);
        if (InteractLogDue())
        {
            var rec = Ptr(p + PlacementId);
            var id = rec != 0 && Readable(rec, 8) ? (ulong)Marshal.ReadInt64(rec) : 0;
            Write($"INTERACT: world object {id} can be used by this guest too (the host has the mod)   quest={Categorise(_lastQuestId)}");
        }
    }

    /// <summary>Pump (guest): the host's HELLO arrived after objects were set up; open the ones still waiting.</summary>
    private void PumpOpenLater(long now)
    {
        if (_openLater.Count == 0) return;
        lock (_interactLock) ResetOpenedIfAreaChanged();
        if (!OwnerHasMod(now)) return;
        var (joined, owner) = LobbyOwnership();
        lock (_interactLock)
        {
            ResetOpenedIfAreaChanged();
            if (joined && !owner && GuestInteractReady() && _relayActive && IsStory(_lastQuestId))
                foreach (var p in _openLater) if (WaitingInteract(p)) Open(p);
            _openLater.Clear();
        }
    }

    private bool OpenedByMod(nint comp)
    {
        lock (_interactLock)
        {
            ResetOpenedIfAreaChanged();
            foreach (var p in _opened)
                if (IsInteractObject(p) && Marshal.ReadIntPtr(p + PlacementComp) == comp) return true;
        }
        return false;
    }

    // ------------------------------------------------- guest: the press is sent

    private nint OnCInteract(nint comp, nint user, nint fromNetwork)
    {
        var send = false;
        try
        {
            if ((byte)fromNetwork == 0 && comp != 0 && user != 0 && GuestInteractReady() && _relayActive && IsStory(_lastQuestId))
            {
                var (joined, owner) = LobbyOwnership();
                if (joined && !owner && (byte)_vcall(comp, 0x78, 0) != 0)
                {
                    if ((byte)_vcall(comp, 0x60, 0) == 0) send = true;   // networked kind: the game sends, never runs it here
                    else if (OpenedByMod(comp))
                    {
                        // A kind that would run here, on an object only this mod opened: never on the guest alone.
                        if (InteractLogDue()) Write($"INTERACT: this object works on the host only; nothing done here   quest={Categorise(_lastQuestId)}");
                        return 0;
                    }
                }
            }
        }
        catch (Exception ex) { RelayError("guest interaction", ex); send = false; }

        var r = _cInteractHook.OriginalFunction(comp, user, fromNetwork);
        if (send) try { SendInteract(comp, user); } catch (Exception ex) { RelayError("guest interaction", ex); }
        return r;
    }

    private void SendInteract(nint comp, nint user)
    {
        var obj = ObjectNetId(comp);
        var who = PlayerNetIdOf(user);
        if (obj == -1 || who == -1)
        {
            if (InteractLogDue()) Write($"INTERACT: pressed, but this object has no network id here; the host cannot be asked   quest={Categorise(_lastQuestId)}");
            return;
        }
        var now = _clock.ElapsedMilliseconds;
        if (obj == _lastGuestSendId && now - _lastGuestSendMs < 1000) return;   // one press, not every frame it is held
        _lastGuestSendId = obj; _lastGuestSendMs = now;
        Send(RelayCodec.TypeInteract, RelayCodec.U64((ulong)obj, (ulong)who));
        if (InteractLogDue()) Write($"INTERACT: pressed as a guest; asked the host to run it   quest={Categorise(_lastQuestId)}");
    }

    // ------------------------------------------- host: say it again to the guests

    /// <summary>The placement's id the way the game announces it (u64 at the record the placement points to); 0 if unreadable.</summary>
    private static ulong PlacementIdOf(nint p)
    {
        var rec = Ptr(p + PlacementId);
        return rec != 0 && Readable(rec, 8) ? (ulong)Marshal.ReadInt64(rec) : 0;
    }

    private void ResetAnnounceIfAreaChanged()
    {
        if (_announceQuest == _lastQuestId) return;
        _announceQuest = _lastQuestId; _announceObjs.Clear(); _announceReady.Clear(); _announceLast.Clear();
    }

    private bool HostAnnounceReady() =>
        _guestInteract && _netIfacePtr != 0 && _compFromHandle != null && _interactSlot9 != 0 && _lobbyHolderPtr != 0
        && _netPtr != 0 && _localIndex != null;

    /// <summary>Remembers an Interact object that starts on this PC (the list is only read on the host).</summary>
    private void TrackForAnnounce(nint p)
    {
        if (!HostAnnounceReady() || !IsStory(_lastQuestId) || !NetOnline() || !IsInteractObject(p)) return;
        var id = PlacementIdOf(p);
        if (id == 0) return;
        lock (_interactLock)
        {
            ResetAnnounceIfAreaChanged();
            foreach (var o in _announceObjs) if (o.p == p && o.id == id) return;
            _announceObjs.RemoveAll(o => o.p == p);   // the address was reused by another object
            if (_announceObjs.Count < MaxTracked) _announceObjs.Add((p, id));
        }
    }

    /// <summary>The object's interactable, found the safe way (the game's own handle check), 0 if there is none.</summary>
    private nint HostComp(nint p)
    {
        if (_compFromHandle == null || !Readable(p, PlacementLinked + 0x18)) return 0;
        var h = Marshal.ReadInt32(p + PlacementLinked) != 0 ? p + PlacementLinked : p + PlacementMember;
        return Marshal.ReadInt32(h) == 0 ? 0 : _compFromHandle(h);
    }

    /// <summary>The same moment the game itself announces an object: its start is done on this PC and its interactable is on.</summary>
    private bool HostObjectReady(nint p, ulong id)
    {
        if (!IsInteractObject(p) || PlacementIdOf(p) != id) return false;
        if (Marshal.ReadIntPtr(p + PlacementCallback) != 0) return false;                 // still waiting for its own conditions
        if ((uint)(Marshal.ReadInt32(p + PlacementStatus) - 5) <= 1) return false;        // the game's own "not 5 or 6" test
        var c = HostComp(p);
        return c != 0 && Readable(c + CompEnabled, 1) && (Marshal.ReadByte(c + CompEnabled) & 1) != 0;
    }

    /// <summary>Pump (host): repeat the game's own announcement (network type 10 / 1) for the objects that are ready here.</summary>
    private void PumpAnnounce(long now)
    {
        if (_announceObjs.Count == 0 || now - _announceTickMs < AnnounceTickMs) return;
        _announceTickMs = now;
        if (!HostAnnounceReady() || !IsStory(_lastQuestId) || !NetOnline()) return;
        var (joined, owner) = LobbyOwnership();
        if (!joined || !owner) return;
        (nint p, ulong id)[] snap;
        lock (_interactLock) { ResetAnnounceIfAreaChanged(); snap = _announceObjs.ToArray(); }
        var due = new List<ulong>();
        foreach (var (p, id) in snap)
        {
            if (!IsInteractObject(p) || PlacementIdOf(p) != id)
            {
                // Gone (freed, reused): forget it, so retries of one part never fill the list.
                lock (_interactLock) { _announceObjs.RemoveAll(o => o.p == p && o.id == id); _announceReady.Remove(id); _announceLast.Remove(id); }
                continue;
            }
            if (!HostObjectReady(p, id)) { lock (_interactLock) _announceReady.Remove(id); continue; }
            long since, last; bool sentBefore;
            lock (_interactLock)
            {
                if (!_announceReady.TryGetValue(id, out since)) _announceReady[id] = since = now;
                sentBefore = _announceLast.TryGetValue(id, out last);
            }
            if (now - since < AnnounceFirstMs) continue;
            var period = now - since < AnnounceFastForMs ? AnnounceFastMs : AnnounceSlowMs;
            if (!sentBefore || now - last >= period) due.Add(id);
        }
        for (int i = 0; i < due.Count; i += AnnounceBatch)
        {
            var batch = due.GetRange(i, Math.Min(AnnounceBatch, due.Count - i));
            if (!SendAnnounce(batch)) return;
            lock (_interactLock)
                foreach (var id in batch)
                {
                    var first = !_announceLast.ContainsKey(id);
                    _announceLast[id] = now;
                    if (first && InteractLogDue()) Write($"INTERACT: told the guests about world object {id} (ready on this host)   quest={Categorise(_lastQuestId)}");
                }
        }
    }

    /// <summary>The game's own message, byte for byte as its placement manager builds it: type 10, subtype 1, size 0x68, reliable, count, ids.</summary>
    private bool SendAnnounce(List<ulong> ids)
    {
        var net = Ptr(_netPtr);
        if (net == 0 || !Readable(net + 4, 4) || Marshal.ReadInt32(net + 4) != 3 || _localIndex(net) == -1) return false;   // the game's own two tests
        var iface = Ptr(Ptr(_netIfacePtr) + 0x38);
        if (iface == 0 || ids.Count == 0 || ids.Count > AnnounceBatch) return false;
        var msg = Marshal.AllocHGlobal(0x68);
        try
        {
            for (int i = 0; i < 0x68; i += 8) Marshal.WriteInt64(msg, i, 0);
            Marshal.WriteInt32(msg, 0, 10); Marshal.WriteInt32(msg, 4, 1); Marshal.WriteInt32(msg, 8, 0x68); Marshal.WriteByte(msg, 0xC, 1);
            Marshal.WriteInt32(msg, 0x10, ids.Count);
            for (int i = 0; i < ids.Count; i++) Marshal.WriteInt64(msg, 0x18 + 8 * i, unchecked((long)ids[i]));
            _vcall(iface, 0x80, msg);
            return true;
        }
        finally { Marshal.FreeHGlobal(msg); }
    }

    // ------------------------------------------------------- host: the gate

    /// <summary>The game's receiver of a guest's request (also used for relayed ones).</summary>
    private nint OnInteractRequestRecv(nint payload)
    {
        t_userChecked = false;
        try { if (!AllowInteractRequest(payload, "the game's own message")) return 0; }
        catch (Exception ex)
        {
            RelayError("interaction request", ex);
            if (!t_userChecked) return 0;   // never hand the game a press whose player was not checked
        }
        return _interactRecvHook.OriginalFunction(payload);
    }

    private bool AllowInteractRequest(nint payload, string source)
    {
        if (payload == 0 || !Readable(payload, 0x20)) return true;
        var obj = Marshal.ReadInt64(payload + 0x10);
        var who = Marshal.ReadInt64(payload + 0x18);
        if (_netIdToHandle == null) return true;
        var known = EntityOfNetId(who) != 0;
        t_userChecked = known;
        if (!known)
        {
            // The game would run the press with no player and crash.
            if (InteractLogDue(anyArea: true)) Write($"INTERACT: ignored a press from a player this PC does not know yet ({source})");
            return false;
        }
        if (!_guestInteract || !IsStory(_lastQuestId)) return true;
        var comp = CompOfNetId(obj);
        if (comp == 0) return true;   // the game's own code finds nothing either and stops
        if (!Readable(comp + CompEnabled, 1) || (Marshal.ReadByte(comp + CompEnabled) & 1) == 0)
        {
            if (InteractLogDue()) Write($"INTERACT: a guest pressed an object that is not ready on the host yet; ignored ({source})   quest={Categorise(_lastQuestId)}");
            return false;
        }
        var now = _clock.ElapsedMilliseconds;
        lock (_interactLock)
        {
            if (_interactRunAt.TryGetValue(obj, out var at) && now - at < 1500) return false;   // the other copy of the same press
            if (_interactRunAt.Count > MaxTracked) _interactRunAt.Clear();
            _interactRunAt[obj] = Math.Max(1, now);
        }
        if (InteractLogDue()) Write($"INTERACT: a guest's press ran on the host ({source})   quest={Categorise(_lastQuestId)}");
        return true;
    }

    /// <summary>Host, inside the receive path: a guest's press arrived over the relay.</summary>
    private void RunRelayedInteract(int from, byte[] p)
    {
        if (p.Length < 16 || !GuestInteractReady() || !IsStory(_lastQuestId)) return;
        var (joined, owner) = LobbyOwnership();
        if (!joined || !owner) return;
        var obj = (long)RelayCodec.ReadU64(p, 0);
        var who = (long)RelayCodec.ReadU64(p, 8);
        var user = EntityOfNetId(who);
        var g = Ptr(user + EntityGame);
        if (user == 0 || g == 0 || PlayerNetIdOf(user) != who || !Readable(g + PlayerNetIndex, 4) || Marshal.ReadInt32(g + PlayerNetIndex) != from)
        {
            if (InteractLogDue()) Write($"INTERACT: ignored a relayed press from player slot {from} (not that player's character)   quest={Categorise(_lastQuestId)}");
            return;
        }
        // The game's own request, byte for byte: type 2, subtype 0x44, size 0x20, reliable.
        var msg = Marshal.AllocHGlobal(0x20);
        try
        {
            for (int i = 0; i < 0x20; i += 8) Marshal.WriteInt64(msg, i, 0);
            Marshal.WriteInt32(msg, 0, 2); Marshal.WriteInt32(msg, 4, 0x44); Marshal.WriteInt32(msg, 8, 0x20); Marshal.WriteByte(msg, 0xC, 1);
            Marshal.WriteInt64(msg, 0x10, obj); Marshal.WriteInt64(msg, 0x18, who);
            if (AllowInteractRequest(msg, $"relayed by player slot {from}")) _interactRecvHook.OriginalFunction(msg);
        }
        finally { Marshal.FreeHGlobal(msg); }
    }
}
