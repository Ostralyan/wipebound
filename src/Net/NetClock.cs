using Godot;
using System.Collections.Generic;

namespace Wipebound.Net;

/// <summary>
/// A clock every peer agrees on.
///
/// Nothing in a networked encounter can be honest without this. A telegraph that
/// says "2 seconds" has to mean the same two seconds on five machines, and a boss
/// that resolves a mechanic has to name an instant rather than "now". Local time
/// cannot do that: every machine booted at a different moment.
///
/// The estimate is the standard NTP trick. The client stamps a probe, the server
/// echoes it back with its own clock reading, and the client assumes the trip was
/// symmetric -- so the server's clock at the moment the reply left was
/// serverTime, and that moment was about rtt/2 ago:
///
///     offset = serverTime + rtt/2 - now
///
/// Any single sample is polluted by whatever queueing the packet happened to hit,
/// so we keep a window and trust the sample with the LOWEST round trip. The
/// fastest trip is the least distorted one; averaging would fold the jitter in
/// rather than reject it.
/// </summary>
public partial class NetClock : Node
{
    public static NetClock Instance { get; private set; }

    private const int WarmupProbes = 10;
    private const double WarmupInterval = 0.25;
    private const double SteadyInterval = 2.0;
    private const int WindowSize = 12;

    /// <summary>
    /// A client reports its own round trip so the server can size the resolve
    /// grace. That means a modified client could claim a huge one and delay
    /// mechanics for the whole raid, so the server never credits more than this.
    ///
    /// It is therefore also the LATENCY CEILING FOR RANKED PLAY, and that is not
    /// an accident of this number, it is what this number means. Every grace
    /// sized from a round trip -- the speed-change hold, the knockback
    /// acknowledgement -- stops growing here, so a player further away than this
    /// starts being billed for movement they made honestly.
    ///
    /// Measured with tools/latency-test.sh, four runs each. 80ms with 1% loss is
    /// clean every time. 300ms with 3% loss is clean three runs in four and once
    /// billed 172cm of 200 -- it is the EDGE of the envelope, not the middle of
    /// it, because it sits just under this ceiling. 600ms with 8% is billed
    /// heavily and correctly.
    ///
    /// Raising this widens the ceiling and the cheat window together, and at that
    /// distance a 1.6s telegraph leaves under a second to react, so the fight is
    /// lost before the ladder is.
    /// </summary>
    public const double MaxCreditedRtt = 0.35;

    private readonly List<(double Rtt, double Offset)> _samples = new();
    private readonly Dictionary<int, double> _peerRtt = new();

    private double _offset;
    private double _rtt;
    private double _nextProbeAt;
    private int _probesSent;

    /// Monotonic seconds since this process started. Never wall-clock: wall-clock
    /// can step backwards.
    public double LocalTime => Time.GetTicksUsec() / 1_000_000.0;

    /// The shared timeline. Every cast start, cast end and resolve deadline is
    /// expressed in this, on every peer.
    public double ServerTime => LocalTime + _offset;

    public double Rtt => _rtt;
    public double OffsetSeconds => _offset;

    /// False on a client until the first probe lands. Do not schedule anything
    /// against ServerTime before this is true.
    public bool Synced { get; private set; }

    /// <summary>
    /// Measured round trip to one peer, or zero if it has not reported yet.
    /// Server-side; clients have their own Rtt.
    /// </summary>
    public double RttFor(int peerId) => _peerRtt.TryGetValue(peerId, out double rtt) ? rtt : 0.0;

    /// <summary>
    /// Worst round trip among connected clients, which is how long the server must
    /// wait past a telegraph's visual end before it is fair to resolve it.
    /// Server-side only; zero if nobody is connected.
    /// </summary>
    public double WorstPeerRtt
    {
        get
        {
            double worst = 0.0;
            foreach (double rtt in _peerRtt.Values)
                if (rtt > worst) worst = rtt;
            return worst;
        }
    }

    public override void _Ready()
    {
        Instance = this;
        Multiplayer.PeerDisconnected += id => _peerRtt.Remove((int)id);
        NetworkManager.Instance.ModeChanged += OnModeChanged;
        OnModeChanged();
    }

    private void OnModeChanged()
    {
        _samples.Clear();
        _peerRtt.Clear();
        _offset = 0.0;
        _rtt = 0.0;
        _probesSent = 0;
        _nextProbeAt = 0.0;

        // The server IS the timeline, so it is synced by definition. An offline
        // session is trivially its own authority too.
        Synced = NetworkManager.Instance.Mode != NetworkManager.NetMode.Client;
    }

    public override void _Process(double delta)
    {
        if (NetworkManager.Instance.Mode != NetworkManager.NetMode.Client) return;

        // Probing a peer that is still handshaking, or already torn down, throws.
        MultiplayerPeer peer = Multiplayer.MultiplayerPeer;
        if (peer is null || peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return;

        double now = LocalTime;
        if (now < _nextProbeAt) return;

        _nextProbeAt = now + (_probesSent < WarmupProbes ? WarmupInterval : SteadyInterval);
        _probesSent++;
        RpcId(NetworkManager.ServerPeerId, MethodName.Probe, now, _rtt);
    }

    /// <summary>Client -> server. Carries the client's last measured round trip as a
    /// free passenger, so sizing the resolve grace costs no extra traffic.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    public void Probe(double clientStamp, double reportedRtt)
    {
        if (!NetworkManager.Instance.IsServer) return;

        int sender = Multiplayer.GetRemoteSenderId();
        if (sender == 0) return;

        _peerRtt[sender] = Mathf.Clamp(reportedRtt, 0.0, MaxCreditedRtt);
        RpcId(sender, MethodName.ProbeReply, clientStamp, ServerTime);
    }

    /// <summary>Server -> the one client that asked.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    public void ProbeReply(double clientStamp, double serverStamp)
    {
        double now = LocalTime;
        double rtt = now - clientStamp;
        if (rtt < 0.0) return;

        _samples.Add((rtt, serverStamp + rtt * 0.5 - now));
        if (_samples.Count > WindowSize) _samples.RemoveAt(0);

        // Trust the least-delayed sample rather than the average: the fastest trip
        // is the one least distorted by queueing.
        var best = _samples[0];
        foreach (var sample in _samples)
            if (sample.Rtt < best.Rtt) best = sample;

        _offset = best.Offset;
        _rtt = best.Rtt;
        Synced = true;
    }
}
