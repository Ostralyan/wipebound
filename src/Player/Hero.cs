using Godot;
using Wipebound.Net;
using Wipebound.World;

namespace Wipebound.Player;

/// <summary>
/// A player hero: right-click to move, Q to cast.
///
/// SPLIT AUTHORITY -- the whole security model lives in _EnterTree below.
///
///   MoveSync  (authority = the owning client) replicates NetPosition/NetFacing.
///             The client owns where it stands, so dodging is instant with zero
///             prediction code. In a PvE game that is the correct trade, not a
///             shortcut: what you see is what resolves.
///
///   StatsSync (authority = the server) replicates Health. A client CANNOT write
///             these -- if both sets of properties shared one synchronizer with
///             client authority, any player could set their own health to 999999
///             and the engine would faithfully replicate it to everyone.
/// </summary>
public partial class Hero : CharacterBody3D
{
    [ExportGroup("Movement")]
    [Export] public float MoveSpeed = 7.0f;
    [Export] public float TurnSpeed = 14.0f;
    [Export] public float ArriveDistance = 0.4f;

    [ExportGroup("Stats")]
    [Export] public float MaxHealth = 100f;

    [ExportGroup("Test Ability (placeholder for the real ability system)")]
    [Export] public float ZapCooldown = 1.0f;
    [Export] public float ZapRange = 14f;
    [Export] public float ZapDamage = 12f;

    // --- Replicated by MoveSync. Authority: the owning client. ---
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetFacing { get; set; }

    // --- Replicated by StatsSync. Authority: the server. Read-only everywhere else. ---
    [Export] public float Health { get; set; } = 100f;

    /// The peer that owns this hero, carried by the node's name (see NetworkManager).
    public int PeerId { get; private set; }

    public bool IsLocalPlayer => PeerId == Multiplayer.GetUniqueId();

    /// <summary>
    /// The server's own copy of this hero's position, which can only ever move at a
    /// legal speed. Every server-side range and area check uses THIS, never the raw
    /// NetPosition a client reported -- otherwise a modified client could claim to be
    /// in melee range from across the arena.
    /// </summary>
    public Vector3 ServerPosition { get; private set; }

    public bool IsAlive => Health > 0f;

    private NavigationAgent3D _agent;
    private Label3D _label;
    private MeshInstance3D _body;
    private Vector3 _moveTarget;
    private bool _hasTarget;
    private bool _useNavigation;
    private int _navProbeFrames;

    // Server-only. Never replicated, never trusted from a client.
    private double _zapReadyAt;

    public override void _EnterTree()
    {
        // MultiplayerSpawner replicated the node's name, so this is known on every
        // peer before the first sync packet arrives.
        PeerId = Name.ToString().ToInt();

        // Delegate exactly one thing to the client, and nothing else.
        GetNode<MultiplayerSynchronizer>("MoveSync").SetMultiplayerAuthority(PeerId);
        GetNode<MultiplayerSynchronizer>("StatsSync").SetMultiplayerAuthority(NetworkManager.ServerPeerId);
    }

    public override void _Ready()
    {
        _agent = GetNode<NavigationAgent3D>("NavAgent");
        _label = GetNode<Label3D>("NameLabel");
        _body = GetNode<MeshInstance3D>("Body");

        // NetPosition arrived with the spawn packet (spawn = true in the config).
        GlobalPosition = NetPosition;
        ServerPosition = NetPosition;
        _moveTarget = NetPosition;

        _body.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = IsLocalPlayer ? new Color("4ade80") : new Color("94a3b8"),
        };

        if (NetworkManager.Instance.IsServer)
            Health = MaxHealth;

        if (IsLocalPlayer)
            NetworkManager.Instance.EmitSignal(NetworkManager.SignalName.LocalHeroReady, this);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        ProbeNavigation();

        if (IsLocalPlayer) DriveLocally(dt);
        else               InterpolateRemote(dt);

        if (NetworkManager.Instance.IsServer)
            UpdateServerPosition(dt);

        _label.Text = $"{PeerId}\n{Mathf.RoundToInt(Health)}/{Mathf.RoundToInt(MaxHealth)}";
        _label.Modulate = Health > MaxHealth * 0.3f ? Colors.White : new Color("f87171");
    }

    // ---------------------------------------------------------------------
    // Movement
    // ---------------------------------------------------------------------

    /// <summary>
    /// Use the navigation mesh if the arena baked one, otherwise walk in a straight
    /// line. The fallback keeps an empty arena playable; the moment you add obstacle
    /// meshes under NavRegion and they bake, pathfinding takes over with no code change.
    /// </summary>
    private void ProbeNavigation()
    {
        if (_navProbeFrames > 2) return;
        if (_navProbeFrames++ < 2) return;

        var region = GetTree().GetFirstNodeInGroup("nav_region") as NavigationRegion3D;
        _useNavigation = region?.NavigationMesh is { } mesh && mesh.GetPolygonCount() > 0;
    }

    private void DriveLocally(float dt)
    {
        Vector3 flatToTarget = _moveTarget - GlobalPosition;
        flatToTarget.Y = 0f;

        if (_hasTarget && flatToTarget.Length() > ArriveDistance && IsAlive)
        {
            Vector3 step = _useNavigation ? _agent.GetNextPathPosition() : _moveTarget;
            Vector3 dir = step - GlobalPosition;
            dir.Y = 0f;

            if (dir.LengthSquared() > 0.0001f)
            {
                dir = dir.Normalized();
                Velocity = new Vector3(dir.X * MoveSpeed, 0f, dir.Z * MoveSpeed);

                // Godot's forward is -Z, so the yaw that points -Z along dir is this.
                float wanted = Mathf.Atan2(-dir.X, -dir.Z);
                Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, wanted, Smoothing(TurnSpeed, dt)), 0f);
            }
        }
        else
        {
            Velocity = Vector3.Zero;
            _hasTarget = false;
        }

        MoveAndSlide();

        // Publish where we ended up. This is the one value a client is allowed to write.
        NetPosition = GlobalPosition;
        NetFacing = Rotation.Y;
    }

    private void InterpolateRemote(float dt)
    {
        // Positions arrive at the synchronizer's rate, not the frame rate, so smooth
        // toward them instead of snapping. Exponential smoothing is framerate-independent.
        GlobalPosition = GlobalPosition.Lerp(NetPosition, Smoothing(18f, dt));
        Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, NetFacing, Smoothing(18f, dt)), 0f);
    }

    private void UpdateServerPosition(float dt)
    {
        // Speed clamp. A client can claim to be anywhere; the server's copy can only
        // chase that claim at a legal pace, so teleporting gains nothing that matters.
        float budget = MoveSpeed * 1.5f * dt + 0.05f;
        Vector3 offset = NetPosition - ServerPosition;
        ServerPosition += offset.Length() <= budget ? offset : offset.Normalized() * budget;
    }

    private static float Smoothing(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);

    // ---------------------------------------------------------------------
    // Input -- raw events for now. Swap to InputMap actions from the editor's
    // Project Settings > Input Map when you want rebindable keys.
    // ---------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsLocalPlayer || !IsAlive) return;

        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }
            && RtsCamera.MouseGroundPoint(this, out Vector3 order))
        {
            _moveTarget = order;
            _hasTarget = true;
            if (_useNavigation) _agent.TargetPosition = order;
            GetViewport().SetInputAsHandled();
        }

        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Q }
            && RtsCamera.MouseGroundPoint(this, out Vector3 aim))
        {
            // Ask. Do not act. The server decides whether this happened at all.
            RpcId(NetworkManager.ServerPeerId, MethodName.RequestCast, 0, aim);
            GetViewport().SetInputAsHandled();
        }
    }

    // ---------------------------------------------------------------------
    // The client -> server surface.
    //
    // This is the ONLY method on a hero a client may invoke, and it carries
    // INTENT, never an outcome: which ability, aimed where. No damage number
    // crosses the wire from a client, so "I do 1000000 damage" has nowhere to live.
    //
    // Every AnyPeer method is attack surface. Keep this list short enough to audit
    // by reading it.
    // ---------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    public void RequestCast(int abilityIndex, Vector3 aimPoint)
    {
        if (!NetworkManager.Instance.IsServer) return;

        // 1. Who really sent this? The transport says so; the payload cannot lie
        //    about it. Zero means it was called locally, i.e. by the host itself.
        int sender = Multiplayer.GetRemoteSenderId();
        if (sender == 0) sender = Multiplayer.GetUniqueId();

        // 2. Never trust an id in the arguments. This RPC can only ever affect the
        //    hero belonging to whoever sent it.
        if (sender != PeerId)
        {
            GD.PushWarning($"Peer {sender} tried to cast as hero {PeerId}. Ignored.");
            return;
        }

        // 3-5. Server-owned preconditions. The client's cooldown UI is decoration;
        //      this timer is the real one.
        if (!IsAlive) return;
        if (abilityIndex != 0) return;

        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _zapReadyAt) return;

        var dummy = GetTree().GetFirstNodeInGroup(TrainingDummy.GroupName) as TrainingDummy;
        if (dummy is null || !dummy.IsAlive) return;

        // Range measured against the VALIDATED position, not the claimed one.
        if (ServerPosition.DistanceTo(dummy.GlobalPosition) > ZapRange) return;

        _zapReadyAt = now + ZapCooldown;

        // 6. Only now does a damage number exist, and the server is the one that
        //    produced it -- from its own copy of the stats. A cheater editing the
        //    shipped ability data changes their own UI and nothing else.
        dummy.ApplyDamage(ZapDamage);
        Rpc(MethodName.PlayCastEffect, aimPoint);
    }

    /// Authority mode: only the server may broadcast this, so clients cannot fake
    /// effects (or, later, "boss died") at each other.
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void PlayCastEffect(Vector3 at)
    {
        GD.Print($"[fx] hero {PeerId} cast toward {at.Round()}");
    }
}
