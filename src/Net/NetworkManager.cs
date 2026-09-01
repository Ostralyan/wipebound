using Godot;
using System.Collections.Generic;
using Wipebound.Player;

namespace Wipebound.Net;

/// <summary>
/// Autoload. Owns the multiplayer peer lifecycle and server-side hero spawning.
///
/// The one architectural rule this file exists to enforce: the SERVER is a role,
/// not a player. <see cref="NetMode.DedicatedServer"/> runs the whole simulation
/// with no hero of its own, so moving from "a friend hosts" to "a headless box on
/// a VPS hosts" is a launch flag, not a refactor.
/// </summary>
public partial class NetworkManager : Node
{
    public const int DefaultPort = 7777;
    public const int MaxPlayers = 8;

    /// ENet retries a dead address for a long time before admitting defeat, so
    /// without this the lobby sits on "Connecting..." indefinitely and looks hung.
    public const double ConnectTimeoutSeconds = 6.0;

    /// In Godot's high-level multiplayer the server is always peer 1.
    public const int ServerPeerId = 1;

    public enum NetMode { Offline, Host, DedicatedServer, Client }

    public static NetworkManager Instance { get; private set; }

    [Signal] public delegate void StatusChangedEventHandler(string message);
    [Signal] public delegate void ModeChangedEventHandler();

    /// Emitted once the hero belonging to THIS machine exists. The camera waits on it.
    [Signal] public delegate void LocalHeroReadyEventHandler(Node3D hero);

    public NetMode Mode { get; private set; } = NetMode.Offline;

    /// <summary>
    /// True where the authoritative simulation runs. Every gameplay decision that
    /// produces a number -- damage, cooldowns, resources, deaths -- must be guarded
    /// by this. Do not use Multiplayer.IsServer() directly: it also returns true
    /// while offline, because Godot installs an OfflineMultiplayerPeer by default.
    /// </summary>
    public bool IsServer => Mode is NetMode.Host or NetMode.DedicatedServer;

    public bool InSession => Mode != NetMode.Offline;

    private Node _heroContainer;
    private double _connectDeadline;
    private PackedScene _heroScene;
    /// Which spawn slot each peer holds, so a slot vacated mid-session is reused
    /// rather than handed out twice.
    private readonly Dictionary<int, int> _slotByPeer = new();

    public override void _Ready()
    {
        Instance = this;
        _heroScene = GD.Load<PackedScene>("res://src/Player/Hero.tscn");

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }

    /// <summary>Reports a session event to both the log and the UI.</summary>
    private void Status(string message)
    {
        GD.Print($"[net] {message}");
        EmitSignal(SignalName.StatusChanged, message);
    }

    /// <summary>Called by Main once the scene tree that heroes live in exists.</summary>
    public void RegisterWorld(Node heroContainer)
    {
        _heroContainer = heroContainer;
        HandleCommandLine();
    }

    // ---------------------------------------------------------------------
    // Transport seam.
    //
    // These two methods are the ONLY place that knows what carries our packets.
    // ENet works on localhost and LAN, and over the internet only with port
    // forwarding. To let friends connect without that, swap the bodies for
    // SteamMultiplayerPeer (relay + NAT punchthrough) -- nothing else changes,
    // because everything above this line only knows about MultiplayerPeer.
    // ---------------------------------------------------------------------

    private static MultiplayerPeer CreateServerPeer(int port, out string error)
    {
        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateServer(port, MaxPlayers);
        error = err == Error.Ok ? null : $"could not listen on port {port} ({err})";
        return err == Error.Ok ? peer : null;
    }

    private static MultiplayerPeer CreateClientPeer(string address, int port, out string error)
    {
        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateClient(address, port);
        error = err == Error.Ok ? null : $"could not reach {address}:{port} ({err})";
        return err == Error.Ok ? peer : null;
    }

    // ---------------------------------------------------------------------
    // Session lifecycle
    // ---------------------------------------------------------------------

    public bool Host(int port = DefaultPort, bool dedicated = false)
    {
        MultiplayerPeer peer = CreateServerPeer(port, out string error);
        if (peer is null)
        {
            Status(error);
            return false;
        }

        Multiplayer.MultiplayerPeer = peer;
        Mode = dedicated ? NetMode.DedicatedServer : NetMode.Host;
        Status(dedicated
            ? $"Dedicated server listening on {port}"
            : $"Hosting on port {port} -- DEVELOPMENT ONLY, runs are not rankable");
        EmitSignal(SignalName.ModeChanged);

        // A dedicated server simulates but never gets a hero. A host does.
        if (!dedicated)
            SpawnHeroFor(ServerPeerId);

        return true;
    }

    public bool Join(string address, int port = DefaultPort)
    {
        MultiplayerPeer peer = CreateClientPeer(address, port, out string error);
        if (peer is null)
        {
            Status(error);
            return false;
        }

        Multiplayer.MultiplayerPeer = peer;
        Mode = NetMode.Client;
        _connectDeadline = Clock() + ConnectTimeoutSeconds;
        Status($"Connecting to {address}:{port}...");
        EmitSignal(SignalName.ModeChanged);
        return true;
    }

    public void Leave()
    {
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = null;
        Mode = NetMode.Offline;
        _connectDeadline = 0.0;
        _slotByPeer.Clear();

        if (_heroContainer is not null)
            foreach (Node child in _heroContainer.GetChildren())
                child.QueueFree();

        Status("Left the session.");
        EmitSignal(SignalName.ModeChanged);
    }

    // ---------------------------------------------------------------------
    // Hero spawning -- server only. MultiplayerSpawner replicates the result.
    // ---------------------------------------------------------------------

    private void SpawnHeroFor(int peerId)
    {
        if (!IsServer || _heroContainer is null) return;
        if (_slotByPeer.ContainsKey(peerId)) return;

        int slot = SpawnRing.NextFreeIndex(new HashSet<int>(_slotByPeer.Values));
        Vector3 spawn = SpawnRing.PointFor(slot);

        var hero = _heroScene.Instantiate<Hero>();

        // The node's NAME carries the owning peer id to every client. This looks
        // like a hack and is in fact the standard Godot idiom: MultiplayerSpawner
        // replicates names, so every peer can work out who owns what in _EnterTree,
        // before the first sync packet lands.
        hero.Name = peerId.ToString();

        // Marked spawn=true in the replication config, so this initial placement
        // rides along with the spawn packet even though the CLIENT owns position
        // from here on.
        hero.NetPosition = spawn;

        // Server-side only, and never replicated: where this hero returns on death.
        hero.SpawnPoint = spawn;

        _slotByPeer[peerId] = slot;
        _heroContainer.AddChild(hero, true);
        GD.Print($"[net] spawned hero for peer {peerId} in slot {slot} at {hero.NetPosition.Round()}");
    }

    private void DespawnHeroFor(int peerId)
    {
        if (!IsServer || _heroContainer is null) return;
        _slotByPeer.Remove(peerId);

        Node node = _heroContainer.GetNodeOrNull(peerId.ToString());
        if (node is null) return;

        // Snapshot before freeing, or leaving would erase both the contribution and
        // the integrity evidence.
        if (node is Player.Hero hero) Session.RunRecorder.Instance?.CaptureDeparting(hero);
        node.QueueFree();
    }

    // ---------------------------------------------------------------------
    // Peer events
    // ---------------------------------------------------------------------

    private void OnPeerConnected(long id)
    {
        Status($"Peer {id} connected.");
        if (IsServer) SpawnHeroFor((int)id);
    }

    private void OnPeerDisconnected(long id)
    {
        Status($"Peer {id} disconnected.");
        if (IsServer) DespawnHeroFor((int)id);
    }

    private void OnConnectedToServer()
    {
        _connectDeadline = 0.0;
        Status($"Connected as peer {Multiplayer.GetUniqueId()}.");
    }

    private static double Clock() => Time.GetTicksMsec() / 1000.0;

    public override void _Process(double delta)
    {
        if (Mode != NetMode.Client || _connectDeadline <= 0.0) return;

        MultiplayerPeer peer = Multiplayer.MultiplayerPeer;
        if (peer is not null && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
        {
            _connectDeadline = 0.0;
            return;
        }

        if (Clock() < _connectDeadline) return;

        _connectDeadline = 0.0;
        Leave();
        Status("Could not reach a server there. Is one running?");
    }

    private void OnConnectionFailed()
    {
        Mode = NetMode.Offline;
        Multiplayer.MultiplayerPeer = null;
        Status("Connection failed.");
        EmitSignal(SignalName.ModeChanged);
    }

    private void OnServerDisconnected()
    {
        Status("Server closed the session.");
        Leave();
    }

    // ---------------------------------------------------------------------
    // Launch flags, so you can start a server or a client without clicking.
    //   godot --headless -- --server
    //   godot -- --host
    //   godot -- --join 127.0.0.1
    // ---------------------------------------------------------------------

    private void HandleCommandLine()
    {
        string[] args = OS.GetCmdlineUserArgs();
        if (args.Length == 0) return;
        GD.Print($"[net] launch args: [{string.Join(", ", args)}]");

        int port = DefaultPort;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out int parsed))
                port = parsed;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server":
                    Host(port, dedicated: true);
                    return;
                case "--host":
                    Host(port);
                    return;
                case "--join":
                    Join(i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "127.0.0.1", port);
                    return;
            }
        }
    }
}
