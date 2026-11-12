using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Story-progress warning. Online, each client resolves the next story part
/// from its OWN save (nothing in the game shares a story position), so two
/// players whose saves are at different points end up in different chapters or
/// an endless loading screen. Every player's mod reads how many story quests
/// its own save has cleared (plus a fingerprint of which) and shares those two
/// numbers with the other players over the same message channel as the ready
/// check. When they differ, the host's Chapter Select start is held once with
/// the reason; picking the same part again starts anyway.
///
/// Where the data lives (2.0.6, read from the game's quest-system code):
/// the QuestSystem object (global, found from the Chapter Select start action)
/// holds a vector of per-quest records at +0x108..+0x110, 0xD48 bytes each:
/// quest id at +0, cleared count of the save at +8. Story quests are
/// ids 0x1xxxxx. No game function is called.
/// </summary>
public partial class Mod
{
    /// <summary>Chapter Select start action: holds the QuestSystem global. 2.0.6 +0x1BE89A7 (global 0x7C53D30).</summary>
    private const string SigQuestSystemGlobal =
        "48 8B 35 ?? ?? ?? ?? C6 86 A0 0D 00 00 01 8B 86 D4 0D 00 00 83 F8 04 74 ?? 83 F8 02";

    internal const int QuestSysRecordsBegin = 0x108, QuestSysRecordsEnd = 0x110, QuestRecordStride = 0xD48, QuestRecIdAt = 0x0, QuestRecClearedAt = 0x8;
    internal const int MaxQuestRecords = 4000, ProgressReadMs = 3000, ProgressResendMs = 20_000, ProgressStableMs = 5000, ProgressFreshMs = 90_000;

    private nint _questSysGlobal;
    private volatile bool _warnProgress = true;

    private (bool ok, int cleared, uint hash) _myProgress;
    private long _lastProgressReadMs = -100_000, _lastProgressSentMs = -100_000;
    private readonly (int cleared, uint hash, long at)[] _peerProgress = new (int, uint, long)[4];
    private (ulong sig, long since) _mismatchSeen;
    private ulong _loggedMismatch, _progressAck;
    private bool _heldForProgress;

    private void RegisterProgressScans(IStartupScanner scanner)
    {
        scanner.AddMainModuleScan(SigQuestSystemGlobal, r =>
        {
            if (!r.Found) { Write("MISS: QuestSystem (story progress warning off)"); return; }
            var at = _baseAddr + r.Offset;
            _questSysGlobal = Rip(at, 3, 7);
            Write($"FOUND QuestSystem @ 0x{at:X} (+0x{r.Offset:X}) -> global +0x{_questSysGlobal - _baseAddr:X}");
        });
    }

    /// <summary>
    /// (readable, number of story quests this save has cleared, fingerprint of
    /// which). Not readable (not loaded yet, or the layout is not what was
    /// verified) is reported as not ok, never as "no progress".
    /// </summary>
    internal (bool ok, int cleared, uint hash) ReadStoryProgress()
    {
        if (_questSysGlobal == 0) return default;
        var qs = Ptr(_questSysGlobal);
        if (qs == 0 || !Readable(qs + QuestSysRecordsBegin, 0x10)) return default;
        var begin = Marshal.ReadIntPtr(qs + QuestSysRecordsBegin);
        var end = Marshal.ReadIntPtr(qs + QuestSysRecordsEnd);
        var bytes = (long)end - (long)begin;
        if (begin == 0 || bytes <= 0 || bytes % QuestRecordStride != 0 || bytes / QuestRecordStride > MaxQuestRecords) return default;
        if (!Readable(begin, (int)bytes)) return default;
        var cleared = 0; var story = 0; var hash = 0u;
        for (long off = 0; off < bytes; off += QuestRecordStride)
        {
            var id = Marshal.ReadInt32(begin + (nint)off + QuestRecIdAt);
            if ((id >> 20) != 1) continue;                       // story quests are 0x1xxxxx
            story++;
            var count = Marshal.ReadInt32(begin + (nint)off + QuestRecClearedAt);
            if (count <= 0) continue;
            cleared++;
            hash += ((uint)id * 0x9E3779B1u) ^ ((uint)id >> 15);   // order does not matter
        }
        if (story == 0) return default;                          // no story quests at all: not the layout that was verified
        return (true, cleared, hash);
    }

    /// <summary>Pump: read our own progress, share it when it changes and every 20 s.</summary>
    private void PumpProgress(long now)
    {
        if (!_relayActive || !_warnProgress) { _myProgress = default; return; }   // option off: nothing is read or sent
        if (now - _lastProgressReadMs >= ProgressReadMs)
        {
            _lastProgressReadMs = now;
            var p = ReadStoryProgress();
            var changed = p != _myProgress;
            _myProgress = p;
            if (p.ok && (changed || now - _lastProgressSentMs >= ProgressResendMs))
            {
                _lastProgressSentMs = now;
                Send(RelayCodec.TypeProgress, RelayCodec.U32((uint)p.cleared, p.hash));
            }
        }
        LogProgressMismatch(now);
    }

    private void OnProgressFrame(int from, byte[] p, long now)
    {
        if (p.Length < 8 || from < 0 || from >= 4) return;
        _peerProgress[from] = ((int)RelayCodec.ReadU32(p, 0), RelayCodec.ReadU32(p, 4), Math.Max(1, now));
    }

    /// <summary>Players whose save is at a different point than ours (only players whose number is recent).</summary>
    internal List<(int slot, int cleared)> ProgressMismatches(long now)
    {
        var list = new List<(int, int)>();
        if (!_myProgress.ok) return list;
        for (int i = 0; i < 4; i++)
        {
            if (i == _localSlot || !PeerAlive(i, now)) continue;
            var (n, h, at) = _peerProgress[i];
            if (at == 0 || now - at > ProgressFreshMs) continue;
            if (n != _myProgress.cleared || h != _myProgress.hash) list.Add((i, n));
        }
        return list;
    }

    /// <summary>A number that identifies the current set of mismatches; 0 when there is none, or while the numbers are still settling.</summary>
    internal ulong MismatchSignature(long now)
    {
        if (!_warnProgress || !_relayActive) return 0;
        var m = ProgressMismatches(now);
        if (m.Count == 0) { _mismatchSeen = (0, 0); _progressAck = 0; _loggedMismatch = 0; return 0; }   // the difference is gone: a new one is a new warning
        ulong sig = 1469598103934665603UL;
        sig = (sig ^ (ulong)_myProgress.cleared) * 1099511628211UL; sig = (sig ^ _myProgress.hash) * 1099511628211UL;
        foreach (var (slot, n) in m) { sig = (sig ^ (ulong)slot) * 1099511628211UL; sig = (sig ^ (ulong)n) * 1099511628211UL; sig = (sig ^ _peerProgress[slot].hash) * 1099511628211UL; }
        if (sig == 0) sig = 1;
        if (_mismatchSeen.sig != sig) { _mismatchSeen = (sig, now); return 0; }   // changed just now: wait until it holds
        return now - _mismatchSeen.since >= ProgressStableMs ? sig : 0;
    }

    private void LogProgressMismatch(long now)
    {
        var sig = MismatchSignature(now);
        if (sig == 0 || sig == _loggedMismatch) return;
        _loggedMismatch = sig;
        foreach (var (slot, n) in ProgressMismatches(now))
        {
            Write($"PROGRESS: your save has cleared {_myProgress.cleared} story quest(s); player slot {slot}'s has cleared {n}. Different story points can mean endless loading or different chapters (see the README).");
            SetStatus("3note", $"Heads up: your story save differs from player slot {slot}'s ({_myProgress.cleared} vs {n} cleared).", 15_000);
        }
    }

    /// <summary>Host, at the Chapter Select start: hold once when the saves differ. True when the start was held.</summary>
    private bool HoldForProgress(nint copy, nint arg, uint key, long now)
    {
        var sig = MismatchSignature(now);
        if (sig == 0 || sig == _progressAck) return false;
        _heldJump = copy; _heldJumpArg = arg; _heldKey = key; _heldSince = now; _heldQuest = _lastQuestId; _heldForProgress = true;
        var who = string.Join(", ", ProgressMismatches(now).ConvertAll(m => $"slot {m.slot}: {m.cleared}"));
        Write($"PROGRESS: held the start. Saves differ (you: {_myProgress.cleared} cleared; {who}). Pick the same part again to start anyway (60 s).");
        SetStatus("2progress", $"Story saves differ (you: {_myProgress.cleared} cleared; {who}). Pick the same part again to start anyway.");
        return true;
    }

    /// <summary>For the heartbeat: our own number and how many players differ.</summary>
    private string ProgressSummary(long now) => !_myProgress.ok ? "?" : $"{_myProgress.cleared}/diff={ProgressMismatches(now).Count}";
}
