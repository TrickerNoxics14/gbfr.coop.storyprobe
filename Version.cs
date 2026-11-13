using System;
using System.Reflection;

namespace gbfr.coop.storyprobe;

/// <summary>
/// Version notice. Both players need the same mod version (a message that one
/// version sends and the other does not understand is ignored, so features
/// quietly stop working). Every HELLO carries the sender's version as three
/// bytes after the flags byte. When another player's version differs, both see
/// one on-screen note and one log line. Nothing is downloaded or updated here:
/// updates come through Reloaded-II's own update check (see the README).
/// </summary>
public partial class Mod
{
    internal const long VersionNoticeMs = 30_000;

    private readonly (byte major, byte minor, byte patch)[] _peerVersion = new (byte, byte, byte)[4];   // 0.0.0 = not announced (a build older than 0.32)
    private readonly bool[] _peerVersionKnown = new bool[4];
    private readonly bool[] _versionNoticeDone = new bool[4];
    private (byte major, byte minor, byte patch)[] _versionNoticeFor = new (byte, byte, byte)[4];

    /// <summary>This build's version: the assembly version, which the audit keeps equal to ModConfig.json's ModVersion.</summary>
    internal static (byte major, byte minor, byte patch) OwnVersion()
    {
        var v = typeof(Mod).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return ((byte)Math.Clamp(v.Major, 0, 255), (byte)Math.Clamp(v.Minor, 0, 255), (byte)Math.Clamp(v.Build < 0 ? 0 : v.Build, 0, 255));
    }

    private static string VersionText((byte major, byte minor, byte patch) v) => $"{v.major}.{v.minor}.{v.patch}";

    /// <summary>The HELLO payload: flags, then the version.</summary>
    private byte[] HelloPayload(byte flags)
    {
        var (a, b, c) = OwnVersion();
        return new[] { flags, a, b, c };
    }

    /// <summary>HELLO received: remember the sender's version (an older HELLO has none).</summary>
    private void OnHelloVersion(int from, byte[] p)
    {
        if (from < 0 || from >= 4) return;
        _peerVersionKnown[from] = true;
        _peerVersion[from] = p.Length >= 4 ? (p[1], p[2], p[3]) : ((byte)0, (byte)0, (byte)0);
    }

    private void ForgetVersions()
    {
        Array.Clear(_peerVersion); Array.Clear(_peerVersionKnown); Array.Clear(_versionNoticeDone); _versionNoticeFor = new (byte, byte, byte)[4];
        ClearStatus("5version");
    }

    /// <summary>
    /// Pump: one note per player whose mod version differs, shown for 30 s and
    /// logged once per version that player announced.
    /// </summary>
    private void PumpVersionNotice(long now)
    {
        if (!_relayActive) return;
        var own = OwnVersion();
        string text = null;
        for (int i = 0; i < 4; i++)
        {
            if (i == _localSlot || !_peerVersionKnown[i] || !PeerAlive(i, now)) continue;
            var theirs = _peerVersion[i];
            if (theirs == own) { _versionNoticeDone[i] = false; continue; }
            var older = theirs == ((byte)0, (byte)0, (byte)0);
            var their = older ? "an older version (before 0.32)" : "v" + VersionText(theirs);
            if (!_versionNoticeDone[i] || _versionNoticeFor[i] != theirs)
            {
                _versionNoticeDone[i] = true; _versionNoticeFor[i] = theirs;
                Write($"VERSION: player slot {i} runs the mod {their}; this PC runs v{VersionText(own)}. Both PCs need the same version, so update the older one (see the README).");
                SetStatus("5version", $"Mod versions differ: player slot {i} has {their}, you have v{VersionText(own)}. Update so both match.", VersionNoticeMs);
            }
            text ??= their;
        }
    }
}
