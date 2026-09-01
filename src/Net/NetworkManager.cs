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

    /// <summary>
    /// Our own peer id, remembered rather than asked for.
    ///
    /// The HUD used to call Multiplayer.GetUniqueId() every frame. An ENet peer
    /// stops being able to answer that the moment it goes inactive, and it goes
    /// inactive before anything we can observe changes -- guarding on the mode,
    /// on the peer being non-null, and on its connection status all still let
    /// the call through on a server drop.
    /// </summary>
    public int LocalPeerId { get; private set; }

    private Node _heroContainer;
    private double _connectDeadline;
    private PackedScene _heroScene;
    /// Which spawn slot each peer holds, so a slot vacated mid-session is reused
    /// rather than handed out twice.
    private readonly Dictionary<int, int> _slotByPeer = new();

    /// <summary>
    /// Which class this machine's player wants. Local preference only -- the
    /// server treats an arriving one as a request, not an instruction.
    /// </summary>
    public int PreferredClassId { get; private set; }

    public void SetPreferredClass(int classId)
    {
        PreferredClassId = ValidClassOr(classId, 0);
        Session.UserPrefs.Write("player", ("class", PreferredClassId));
    }

    private void LoadPreferences()
        => PreferredClassId = ValidClassOr(Session.UserPrefs.Read("player", "class", 0).AsInt32(), 0);

    /// <summary>
    /// A class id from anywhere untrusted, or the fallback.
    ///
    /// Out of range is not honoured and not fatal either: the peer simply gets
    /// the class it would have got before it asked. Only a modified client can
    /// produce one, and refusing to spawn it would punish a bug harder than a
    /// cheat -- there is nothing to gain by picking class 99.
    /// </summary>
    public static int ValidClassOr(int declared, int fallback)
    {
        int count = System.Enum.GetValues<Combat.HeroClass>().Length;
        return declared >= 0 && declared < count ? declared : fallback;
    }

    public override void _Ready()
    {
        Instance = this;
        _heroScene = GD.Load<PackedScene>("res://src/Player/Hero.tscn");
        LoadPreferences();

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
        LocalPeerId = ServerPeerId;
        Mode = dedicated ? NetMode.DedicatedServer : NetMode.Host;
        Status(dedicated
            ? $"Dedicated server listening on {port}"
            : $"Hosting on port {port} -- DEVELOPMENT ONLY, runs are not rankable");
        EmitSignal(SignalName.ModeChanged);

        // A dedicated server simulates but never gets a hero. A host does, and
        // its own preference needs no round trip to reach itself.
        if (!dedicated)
            SpawnHeroFor(ServerPeerId, PreferredClassId,
                         Session.PlayerIdentity.Id, Session.PlayerIdentity.DisplayName);

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

    /// <summary>We are leaving. Ours to close, so we close it.</summary>
    public void Leave()
    {
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = null;
        ForgetSession("Left the session.", dropOurs: true);
    }

    /// <summary>
    /// Drop what belongs to the session, WITHOUT touching the peer.
    ///
    /// Separate from Leave because the two cases are genuinely different. When we
    /// leave, the peer is ours to close. When the server goes away, the engine
    /// has already dropped the peer and already removed the nodes its spawners
    /// gave us -- and asking it to do that again makes it disconnect signals it
    /// has just disconnected, roughly a dozen times per client, every time a
    /// server restarts.
    /// </summary>
    private void ForgetSession(string reason, bool dropOurs)
    {
        Mode = NetMode.Offline;
        LocalPeerId = 0;
        _connectDeadline = 0.0;
        _slotByPeer.Clear();

        // Only when the session was ours to end. Heroes arrive through a
        // MultiplayerSpawner, so when the SERVER goes away the engine is already
        // taking them back; freeing them again from here races its bookkeeping.
        if (dropOurs && _heroContainer is not null)
        {
            foreach (Node child in _heroContainer.GetChildren())
                if (GodotObject.IsInstanceValid(child)) child.QueueFree();
        }

        Status(reason);
        EmitSignal(SignalName.ModeChanged);
    }

    // ---------------------------------------------------------------------
    // Hero spawning -- server only. MultiplayerSpawner replicates the result.
    // ---------------------------------------------------------------------

    private void SpawnHeroFor(int peerId, int classId, string playerId, string displayName)
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

        // What the player asked for, falling back to round robin by spawn slot.
        // The fallback still matters: it is what a peer gets if its declaration
        // was out of range, and it hands three players three different kits
        // rather than three copies of one.
        hero.ClassId = ValidClassOr(classId, slot % System.Enum.GetValues<Combat.HeroClass>().Length);

        // Server-stamped, because the server is the only party that knows how
        // much it checked. Nothing was checked, so: anonymous.
        hero.PlayerId = playerId;
        hero.PlayerName = displayName;
        hero.IdentityProvider = Session.PlayerIdentity.Anonymous;

        // Server-side only, and never replicated: where this hero returns on death.
        hero.SpawnPoint = spawn;

        _slotByPeer[peerId] = slot;
        _heroContainer.AddChild(hero, true);
        GD.Print($"[net] spawned hero for '{hero.PlayerName}' ({peerId}) in slot {slot} as " +
                 $"{Combat.PlayerKit.NameOf(hero.Class)} at {hero.NetPosition.Round()}");
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
        // Deliberately does NOT spawn. A hero's kit is fixed when it is built, so
        // the class has to be known first -- and the only peer who knows it is the
        // one that just connected. It asks, below, and the hero appears then.
        //
        // A peer that never asks never gets a hero, which costs nobody but itself.
        Status($"Peer {id} connected.");
    }

    /// <summary>
    /// "This is who I am and what I would like to play." A request, not an
    /// instruction, and every part of it is untrusted.
    ///
    /// The one client message that cannot go through CommandRouter's single
    /// door, because every command there acts on a Hero and the entire point of
    /// this one is that the Hero does not exist yet.
    ///
    /// Note what is NOT a parameter: provenance. A client that could tell the
    /// server how much to trust its own identity would make the field worthless,
    /// so the server stamps that itself, from what it actually verified -- which
    /// today is nothing, hence anonymous.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void DeclarePlayer(int classId, string playerId, string displayName)
    {
        if (!IsServer) return;

        int peerId = Multiplayer.GetRemoteSenderId();
        if (peerId == 0) return;

        // Asking twice does not get you a second hero.
        if (_slotByPeer.ContainsKey(peerId)) return;

        // A malformed id gets a throwaway rather than a refusal, for the same
        // reason a malformed class does: only a modified client produces one and
        // there is nothing to win by it. It cannot collide with anybody.
        string id = Session.PlayerIdentity.IsWellFormedId(playerId)
            ? playerId
            : System.Guid.NewGuid().ToString("N");

        // Re-cleaned here. The client cleaning its own name proves nothing about
        // what actually arrived, and this string reaches the server's log.
        SpawnHeroFor(peerId, classId, id, Session.PlayerIdentity.CleanName(displayName));
    }

    private void OnPeerDisconnected(long id)
    {
        Status($"Peer {id} disconnected.");
        if (IsServer) DespawnHeroFor((int)id);
    }

    private void OnConnectedToServer()
    {
        _connectDeadline = 0.0;
        LocalPeerId = Multiplayer.GetUniqueId();
        Status($"Connected as peer {LocalPeerId}.");

        // Nothing of ours exists on the server until it knows what to build.
        RpcId(ServerPeerId, MethodName.DeclarePlayer, PreferredClassId,
              Session.PlayerIdentity.Id, Session.PlayerIdentity.DisplayName);
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

        // Deferred, and pointedly NOT Leave(): we are inside the engine's own
        // disconnect handling, the peer is already gone, and the replicated nodes
        // are already going. All that is left is to forget the session.
        //
        // Only reachable once a server closes a session first. Every test before
        // the latency rig had the clients quitting first, so this path had never
        // run.
        CallDeferred(nameof(ForgetSession), "Server closed the session.", false);
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
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out int parsed))
                port = parsed;

            // A headless client has no lobby to choose in, so it says so here.
            //   --class 2   (0 Warden, 1 Ember, 2 Verdant)
            if (args[i] == "--class" && int.TryParse(args[i + 1], out int chosen))
                SetPreferredClass(chosen);

            // A headless client has no lobby to type a name into, and a run log
            // full of "hero 825107506" is unreadable.
            //   --name alice
            if (args[i] == "--name") Session.PlayerIdentity.SetDisplayName(args[i + 1]);
        }

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
