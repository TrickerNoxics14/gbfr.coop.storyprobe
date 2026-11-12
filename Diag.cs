using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Log-only diagnostics for story parts that stall online (Chapter 4's third
/// part never finished in the one two-PC test, with a build that logged none of
/// this). Nothing here changes the game:
///
/// - WAIT: a story step has waited for the same signal for 45 s or more
///   (once per signal per part, at most 8 per part).
/// - STALL at the end of a part: which signal steps were still waiting
///   (10 s or more), so a clear and a stall look different.
/// - STORY SCRIPT: the part's own clear-quest or game-over action ran on
///   this PC (so a defeat is told apart from a stall).
/// </summary>
public partial class Mod
{
    /// <summary>stage::quest::ClearQuest Execute (vtable slot 1). 2.0.6 +0x3200D70.</summary>
    private const string SigClearQuest =
        "41 57 41 56 41 55 41 54 56 57 55 53 48 83 EC 28 48 8B 41 20 8B 40 08 89 44 24 24 48 8B 3D ?? ?? ?? ?? F6 87 3E 0E 00 00 F0 74 11";
    /// <summary>stage::quest::GameOver Execute (vtable slot 1); returns at once if byte [self+0x28] is set. 2.0.6 +0x3204200.</summary>
    private const string SigGameOver =
        "56 48 83 EC 40 80 79 28 00 0F 85 9B 00 00 00 48 89 CE B9 02 00 00 00 BA 01 00 00 00 E8 ?? ?? ?? ?? 84 C0 75 10";

    internal const int WaitLogAfterMs = 45_000, WaitForgetAfterMs = 5_000, StallMinWaitMs = 10_000, MaxWaiters = 256, EndLinesMax = 6, WaitLinesPerQuest = 8;

    private IHook<QuestActionFn> _clearQuestHook, _gameOverHook;

    private sealed class Waiter { public uint Hash; public int Quest; public long First, Last; }
    private readonly Dictionary<nint, Waiter> _waiters = new();
    private readonly HashSet<uint> _waitLogged = new();
    private int _waitLogQuest = -1, _waitLogCount, _scriptLogQuest = -1, _gameOverLogQuest = -1;
    private long _lastWaitPruneMs;
    private readonly object _waitLock = new();

    private void RegisterDiagScans(IStartupScanner scanner)
    {
        Hook(scanner, SigClearQuest, "ClearQuest", "story end log off", a =>
        { _clearQuestHook = _hooks.CreateHook<QuestActionFn>(OnClearQuest, a); _clearQuestHook.Activate(); });
        Hook(scanner, SigGameOver, "GameOver", "story game-over log off", a =>
        { _gameOverHook = _hooks.CreateHook<QuestActionFn>(OnGameOver, a); _gameOverHook.Activate(); });
    }

    /// <summary>Story quest ids are 0x1xxxxx. Allocation-free (this runs every frame for every waiting step).</summary>
    private bool InStoryQuest() => (_lastQuestId >> 20) == 1;

    private nint OnClearQuest(nint self, nint a2, nint a3, nint a4)
    {
        try
        {
            if (NetOnline() && InStoryQuest() && Interlocked.Exchange(ref _scriptLogQuest, _lastQuestId) != _lastQuestId)
                Write($"STORY SCRIPT: clear-quest action reached on this PC ({(_isLeader ? "host" : "guest")})   quest={Categorise(_lastQuestId)}");
        }
        catch { }
        return _clearQuestHook.OriginalFunction(self, a2, a3, a4);
    }

    private nint OnGameOver(nint self, nint a2, nint a3, nint a4)
    {
        try
        {
            // The game's own action does nothing when this byte is set (already game over).
            if (NetOnline() && InStoryQuest() && self != 0 && Readable(self + 0x28, 1) && Marshal.ReadByte(self + 0x28) == 0
                && Interlocked.Exchange(ref _gameOverLogQuest, _lastQuestId) != _lastQuestId)
                Write($"STORY SCRIPT: game-over action ran on this PC ({(_isLeader ? "host" : "guest")})   quest={Categorise(_lastQuestId)}");
        }
        catch { }
        return _gameOverHook.OriginalFunction(self, a2, a3, a4);
    }

    /// <summary>
    /// Called after every check of a waiting signal step (every frame), with
    /// whether the step passed. Remembers when each step started waiting and
    /// forgets it when it passes; logs a step that has waited 45 s. Only online,
    /// in story areas. The fields read are the ones the hooked check itself reads.
    /// </summary>
    private void NoteWaiter(nint listener, long now, bool finished = false)
    {
        if (!NetOnline() || !InStoryQuest()) return;
        if (finished) { lock (_waitLock) _waiters.Remove(listener); return; }
        if (!Readable(listener + 0x20, 0x1C)) return;
        if (Marshal.ReadInt32(listener + 0x38) >= Marshal.ReadInt32(listener + 0x20)) { lock (_waitLock) _waiters.Remove(listener); return; }   // its signal already arrived
        var expected = Marshal.ReadIntPtr(listener + 0x28);
        if (expected == 0 || !Readable(expected, 0x28) || Marshal.ReadInt64(expected + 0x08) != 1) return;
        var hash = (uint)Marshal.ReadInt64(expected + 0x20);
        string line = null;
        lock (_waitLock)
        {
            if (_waitLogQuest != _lastQuestId) { _waitLogQuest = _lastQuestId; _waitLogged.Clear(); _waitLogCount = 0; }
            if (!_waiters.TryGetValue(listener, out var w) || w.Hash != hash || w.Quest != _lastQuestId || now - w.Last > WaitForgetAfterMs)
            {
                if (_waiters.Count >= MaxWaiters) PruneWaiters(now);
                if (_waiters.Count >= MaxWaiters) return;
                _waiters[listener] = w = new Waiter { Hash = hash, Quest = _lastQuestId, First = now };
            }
            w.Last = now;
            if (now - w.First >= WaitLogAfterMs && _waitLogCount < WaitLinesPerQuest && _waitLogged.Add(hash))
            {
                _waitLogCount++;
                var n = 0; foreach (var o in _waiters.Values) if (o.Hash == hash && now - o.Last <= WaitForgetAfterMs) n++;
                line = $"WAIT: a story step has waited {(now - w.First) / 1000} s for signal 0x{hash:X8} ({n} step(s)) on this PC ({(_isLeader ? "host" : "guest")})   quest={Categorise(_lastQuestId)}";
            }
        }
        if (line != null) Write(line);
    }

    private void PruneWaiters(long now)
    {
        if (now - _lastWaitPruneMs < 1000) return;
        _lastWaitPruneMs = now;
        var dead = new List<nint>();
        foreach (var p in _waiters) if (now - p.Value.Last > WaitForgetAfterMs) dead.Add(p.Key);
        foreach (var k in dead) _waiters.Remove(k);
    }

    /// <summary>At quest end: the signal steps that were still being checked in the last seconds and had waited 10 s or more.</summary>
    private void LogStallAtQuestEnd(long now)
    {
        var lines = new List<string>();
        lock (_waitLock)
        {
            var open = new List<Waiter>();
            foreach (var w in _waiters.Values)
                if (w.Quest == _lastQuestId && now - w.Last <= WaitForgetAfterMs && now - w.First >= StallMinWaitMs) open.Add(w);
            open.Sort((x, y) => x.First.CompareTo(y.First));
            foreach (var w in open)
            {
                if (lines.Count >= EndLinesMax) break;
                lines.Add($"STALL: at the end of the part a step was still waiting for signal 0x{w.Hash:X8} (waited {(now - w.First) / 1000} s)   quest={Categorise(w.Quest)}");
            }
            _waiters.Clear();
        }
        foreach (var l in lines) Write(l);
    }
}
