using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Vote to skip story cutscenes (off by default: SkipVoteOnline).
///
/// Today the host's skip button skips for everyone and a guest's confirmed skip
/// skips only on the guest's own PC. With the vote on, a guest's confirmed skip
/// of a movie or event cut is sent to the host as a vote and the guest keeps
/// watching. When more than half of the players with the vote on have voted,
/// the host skips for everyone the same way its own skip does. The host's own
/// confirmed skip still skips at once. A second confirm within 10 s skips on
/// that guest's PC only (and withdraws the vote).
///
/// Players who count are those whose HELLO says the vote is on (HelloVotes),
/// so a player with the option off, or on an older build, is never waited for.
/// With only two such players (the host and one guest) a guest's vote could
/// never be a majority, so the guest's skip is not taken as a vote there.
///
/// Only movie and event cuts are voted on. In-engine scenes keep the old
/// behaviour (the host's skip is passed on; a guest's own skip stays local).
///
/// Game facts (2.0.6, read from the UI script and code): the confirmed skip
/// runs the SkipCut action, which only copies its skip byte to the HUD skip
/// byte (+0x46); the cutscene player then jumps to the end. "A cutscene is
/// playing" here is the active byte (+0x720) and mode (+0x718) 1 or 3, the main
/// condition of the game's own test at +0x6461D0 (which also excludes two cut
/// kinds and has a debug override; those are not mirrored).
/// </summary>
public partial class Mod
{
    internal const int VoteWindowMs = 10_000, VoteResendMs = 1000, VoteFreshMs = 3500;
    internal const uint VoteWithdrawn = 0xFFFFFFFF;

    private volatile bool _skipVote;
    private long _voteSince, _voteLastSent;
    private int _voteQuest = -1;
    private readonly (int quest, long at)[] _peerVote = new (int, long)[4];
    private readonly bool[] _peerVotes = new bool[4];        // from HELLO: that player has the vote on
    private bool _votesSent;
    private long _lastVoteSkipMs = -100_000;

    private bool VoteSupported() => _skipVote && _leaderSkip;

    private bool HostVotes(long now)
    {
        for (int i = 0; i < 4; i++) if (i != _localSlot && _peerLeader[i] && _peerVotes[i] && PeerAlive(i, now)) return true;
        return false;
    }

    /// <summary>Players that count in the vote: the host plus every guest with the vote on (this PC included when it is one).</summary>
    private int VotePlayers(long now)
    {
        var n = 1;   // the host
        for (int i = 0; i < 4; i++)
        {
            if (_peerLeader[i] && i != _localSlot) continue;
            if (i == _localSlot) { if (!_isLeader) n++; continue; }
            if (PeerAlive(i, now) && _peerVotes[i]) n++;
        }
        return n;
    }

    /// <summary>
    /// Guest, from the SkipCut hook: true when the confirmed skip was taken as a
    /// vote (the game's own skip must then not run).
    /// </summary>
    private bool TakeSkipAsVote(nint skipCutSelf)
    {
        var now = _clock.ElapsedMilliseconds;
        if (!VoteSupported() || !_relayActive || _isLeader || !IsStory(_lastQuestId)) return false;
        if (skipCutSelf == 0 || !Readable(skipCutSelf + 0x30, 1)) return false;
        if (Marshal.ReadByte(skipCutSelf + 0x30) == 0) return false;   // a cancel, not a skip
        if (!HostVotes(now) || !CutPlaying()) return false;
        if (_voteSince != 0 && _voteQuest == _lastQuestId && now - _voteSince < VoteWindowMs)
        {
            EndVote();
            Write($"SKIP VOTE: confirmed twice, skipping on this PC only   quest={Categorise(_lastQuestId)}");
            return false;
        }
        if (VotePlayers(now) <= 2) return false;   // the host and this PC only: a guest's vote could never be a majority
        _voteSince = Math.Max(1, now); _voteQuest = _lastQuestId; _voteLastSent = 0;
        SendVote(now, (uint)_lastQuestId);
        SetStatus("4skip", "You voted to skip. Waiting for the others... (confirm skip again to skip only for yourself)", VoteWindowMs);
        Write($"SKIP VOTE: voted to skip this cutscene; waiting for the others   quest={Categorise(_lastQuestId)}");
        return true;
    }

    private void SendVote(long now, uint quest)
    {
        _voteLastSent = Math.Max(1, now);
        Send(RelayCodec.TypeSkipVote, RelayCodec.U32(quest));
    }

    /// <summary>The vote is over (skipped here, or the cutscene ended or ran out): tell the host so it does not count a stale vote.</summary>
    private void EndVote()
    {
        if (_voteSince != 0) { _voteSince = 0; try { SendVote(_clock.ElapsedMilliseconds, VoteWithdrawn); } catch { } }
        ClearStatus("4skip");
    }

    private void OnVoteFrame(int from, byte[] p, long now)
    {
        if (!_isLeader || p.Length < 4 || from < 0 || from >= 4) return;
        var q = RelayCodec.ReadU32(p, 0);
        _peerVote[from] = q == VoteWithdrawn ? (0, 0) : ((int)q, Math.Max(1, now));
    }

    /// <summary>Pump: guest keeps its vote alive while the cut plays; host counts the votes.</summary>
    private void PumpSkipVote(long now)
    {
        if (!VoteSupported() || !_relayActive) { _voteSince = 0; return; }
        if (!_isLeader)
        {
            if (_voteSince == 0) return;
            if (!CutPlaying() || _voteQuest != _lastQuestId || now - _voteSince >= VoteWindowMs) { EndVote(); return; }
            if (now - _voteLastSent >= VoteResendMs) SendVote(now, (uint)_lastQuestId);
            return;
        }
        if (!IsStory(_lastQuestId) || !CutPlaying()) { Array.Clear(_peerVote); ClearStatus("4skip"); return; }
        var guests = 0; var voted = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i == _localSlot || !PeerAlive(i, now) || _peerLeader[i] || !_peerVotes[i]) continue;
            guests++;
            var v = _peerVote[i];
            if (v.at != 0 && v.quest == _lastQuestId && now - v.at <= VoteFreshMs) voted++;
        }
        if (voted == 0) { ClearStatus("4skip"); return; }
        var players = guests + 1; var needed = players / 2 + 1;
        if (voted < needed)
        {
            SetStatus("4skip", $"{voted} of {players - 1} other player(s) want to skip. Confirm skip to skip for everyone.", 4000);
            return;
        }
        if (now - _lastVoteSkipMs < 2000) return;
        _lastVoteSkipMs = Math.Max(1, now);
        Array.Clear(_peerVote); ClearStatus("4skip");
        SkipCutForEveryone(now, $"{voted} of {players} players voted");
    }

    /// <summary>Host: skip the playing cut here and tell the others, the same two things a confirmed skip of the host does.</summary>
    private void SkipCutForEveryone(long now, string why)
    {
        if (_cutHudPtr == 0) return;
        var hud = Marshal.ReadIntPtr(_cutHudPtr);
        if (hud == 0 || !Readable(hud + CutHudSkip, 1)) return;
        Marshal.WriteByte(hud + CutHudSkip, 1);
        _cutSkipWrittenAt = now;
        Send(RelayCodec.TypeSkipCut, RelayCodec.U32((uint)_lastQuestId));
        Write($"SKIP VOTE: {why}; skipped this cutscene for everyone   quest={Categorise(_lastQuestId)}");
    }
}
