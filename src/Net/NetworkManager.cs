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
    private PackedScene _heroScene;
    private readonly List<int> _peersWithHeroes = new();

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
            : $"Hosting on port {port}");
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
        Status($"Connecting to {address}:{port}...");
        EmitSignal(SignalName.ModeChanged);
        return true;
    }

    public void Leave()
    {
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = null;
        Mode = NetMode.Offline;
        _peersWithHeroes.Clear();

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
        if (_peersWithHeroes.Contains(peerId)) return;

        var hero = _heroScene.Instantiate<Hero>();

        // The node's NAME carries the owning peer id to every client. This looks
        // like a hack and is in fact the standard Godot idiom: MultiplayerSpawner
        // replicates names, so every peer can work out who owns what in _EnterTree,
        // before the first sync packet lands.
        hero.Name = peerId.ToString();

        // Marked spawn=true in the replication config, so this initial placement
        // rides along with the spawn packet even though the CLIENT owns position
        // from here on.
        hero.NetPosition = SpawnPointFor(_peersWithHeroes.Count);

        _peersWithHeroes.Add(peerId);
        _heroContainer.AddChild(hero, true);
        GD.Print($"[net] spawned hero for peer {peerId} at {hero.NetPosition.Round()}");
    }

    private void DespawnHeroFor(int peerId)
    {
        if (!IsServer || _heroContainer is null) return;
        _peersWithHeroes.Remove(peerId);
        _heroContainer.GetNodeOrNull(peerId.ToString())?.QueueFree();
    }

    private static Vector3 SpawnPointFor(int index)
    {
        // Ring around the arena centre, facing inward at the dummy.
        const float radius = 10f;
        float angle = Mathf.Tau * index / MaxPlayers;
        return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
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
        => Status($"Connected as peer {Multiplayer.GetUniqueId()}.");

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
