using Godot;
using Wipebound.Combat;
using Wipebound.Combat.Commands;
using Wipebound.Net;

namespace Wipebound.Player;

/// <summary>
/// A player hero: right-click to move, Q to attack the boss.
///
/// SPLIT AUTHORITY -- the security model lives in _EnterTree below.
///
///   MoveSync  (authority = the owning client) replicates NetPosition/NetFacing.
///             The client owns where it stands, so dodging is instant with zero
///             prediction code. In a PvE game that is the correct trade, not a
///             shortcut: what you see is what resolves.
///
///   StatsSync (authority = the server) replicates Health. A client CANNOT write
///             it. If both sets of properties shared one client-authoritative
///             synchronizer, any player could set their own health to 999999 and
///             the engine would replicate it faithfully to everyone.
/// </summary>
public partial class Hero : CharacterBody3D, ICombatant
{
    public const string GroupName = "hero";

    /// Nothing legitimate is ever outside this, so claims beyond it are clamped.
    public const float ArenaRadius = 46f;

    [ExportGroup("Movement")]
    [Export] public float MoveSpeed = 7.0f;
    [Export] public float TurnSpeed = 14.0f;
    [Export] public float ArriveDistance = 0.4f;

    [ExportGroup("Stats")]
    [Export] public float ManaRegenPerSecond = 7f;

    /// Left empty, PlayerKit fills this in. Assign abilities here to override.
    [ExportGroup("Abilities")]
    [Export] public Godot.Collections.Array<Ability> Kit { get; set; } = new();

    // --- Replicated by MoveSync. Authority: the owning client. ---
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetFacing { get; set; }

    // --- Replicated by StatsSync. Authority: the server. Read-only everywhere else. ---
    //
    // These proxy into plain C# pools. A ResourcePool is not a Node and so cannot be
    // replicated directly; exposing [Export] scalars over it keeps the synchronizer
    // working while the logic lives somewhere testable.
    private readonly ResourcePool _health = new(100f);
    private readonly ResourcePool _mana = new(100f);
    private readonly StatusTracker _status = new();

    [Export] public float Health { get => _health.Current; set => _health.Current = value; }
    [Export] public float HealthMax { get => _health.Max; set => _health.Max = value; }
    [Export] public float Mana { get => _mana.Current; set => _mana.Current = value; }
    [Export] public float ManaMax { get => _mana.Max; set => _mana.Max = value; }

    /// The whole status set as one small string. See StatusTracker for why a string.
    [Export] public string StatusPayload { get => _status.Encoded; set => _status.Decode(value); }

    /// The peer that owns this hero, carried by the node's name (see NetworkManager).
    public int PeerId { get; private set; }

    public bool IsLocalPlayer => PeerId == Multiplayer.GetUniqueId();

    // --- ICombatant ---
    public string CombatName => $"hero {PeerId}";
    public int CombatId => PeerId;
    public Team Team => Team.Players;
    public bool IsAlive => !_health.IsEmpty;
    public ResourcePool HealthPool => _health;
    public ResourcePool ManaPool => _mana;
    public StatusTracker Status => _status;
    public Node3D Node => this;

    /// <summary>
    /// Move speed after buffs. Both the client (to move) and the server (to size the
    /// speed clamp) read this, and they agree because statuses replicate. The clamp's
    /// 1.5x margin covers the brief window where a status update is still in flight.
    /// </summary>
    public float EffectiveMoveSpeed => _status.Rooted ? 0f : MoveSpeed * _status.MoveSpeedMultiplier;

    /// <summary>
    /// What the SERVER believes, which is the speed-clamped copy rather than the
    /// position the client last claimed. Every area and range test in the game reads
    /// this, which is what stops a modified client asserting it dodged.
    /// </summary>
    public Vector3 CombatPosition => ServerPosition;

    /// <summary>
    /// Where this hero starts, and where it returns on death.
    ///
    /// It rides on StatsSync rather than MoveSync, and that is not arbitrary.
    /// A MultiplayerSynchronizer's spawn state is gathered and sent by ITS
    /// AUTHORITY -- and MoveSync's authority is the owning client, which does not
    /// exist at the moment the server spawns the node. A client-authoritative
    /// synchronizer therefore cannot carry a server-decided starting position at
    /// all: the property would arrive as a default zero and the client would
    /// immediately publish that back, stomping the spawn point.
    ///
    /// So the starting position travels on the server-authoritative channel, which
    /// is where a server decision belongs anyway. It is marked spawn-only, so it
    /// costs one value once and nothing thereafter.
    /// </summary>
    [Export] public Vector3 SpawnPoint { get; set; }

    /// <summary>
    /// The server's own copy of this hero's position, which can only ever move at a
    /// legal speed. Every server-side range and area check uses THIS, never the raw
    /// NetPosition a client reported -- otherwise a modified client could claim to
    /// be outside every telegraph, or in melee range from across the arena.
    /// </summary>
    public Vector3 ServerPosition { get; private set; }

    private static bool IsServer => NetworkManager.Instance.IsServer;
    private static double Now => NetClock.Instance.ServerTime;

    private NavigationAgent3D _agent;
    private Label3D _label;
    private MeshInstance3D _body;
    private MeshInstance3D _nose;
    private Vector3 _moveTarget;
    private bool _hasTarget;
    private bool _useNavigation;
    private int _navProbeFrames;

    // Server-only. Never replicated, never accepted from a client. These are the
    // real cooldowns; the client's copies below are display state.
    private readonly CooldownSet _serverCooldowns = new();
    private readonly CooldownSet _clientCooldowns = new();

    /// While the server itself is moving this hero -- a knockback, a respawn --
    /// the client's reported position legitimately disagrees with the server's.
    /// The speed clamp stands down until this passes rather than fighting it.
    private double _clampGraceUntil;

    // Client-side knockback slide.
    private Vector3 _pushFrom;
    private Vector3 _pushTo;
    private double _pushStart;
    private double _pushEnd;
    private bool _pushing;

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
        AddToGroup(GroupName);
        AddToGroup(Combatants.GroupName);

        if (Kit.Count == 0) Kit = PlayerKit.Build();
        _serverCooldowns.Resize(Kit.Count);
        _clientCooldowns.Resize(Kit.Count);

        _agent = GetNode<NavigationAgent3D>("NavAgent");
        _label = GetNode<Label3D>("NameLabel");
        _body = GetNode<MeshInstance3D>("Body");
        _nose = GetNode<MeshInstance3D>("Nose");

        // SpawnPoint arrived with the spawn packet on the server-authoritative
        // synchronizer, so it is correct on every peer before anything moves.
        GlobalPosition = SpawnPoint;
        NetPosition = SpawnPoint;
        ServerPosition = SpawnPoint;
        _moveTarget = SpawnPoint;

        _body.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = IsLocalPlayer ? new Color("4ade80") : new Color("94a3b8"),
        };

        if (IsServer)
        {
            _health.Fill();
            _mana.Fill();
        }

        if (IsLocalPlayer)
        {
            GD.Print($"[hero] local hero ready (peer {PeerId})");
            NetworkManager.Instance.EmitSignal(NetworkManager.SignalName.LocalHeroReady, this);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        ProbeNavigation();

        if (IsLocalPlayer) DriveLocally(dt);
        else               InterpolateRemote(dt);

        if (IsServer)
        {
            UpdateServerPosition(dt);

            _status.Tick(this, Now);
            _mana.RegenPerSecond = ManaRegenPerSecond * _status.ManaRegenMultiplier;
            _mana.Tick(dt);
        }
        else
        {
            // Drop what has visibly ended so the buff bar stays honest between updates.
            _status.PruneForDisplay(Now);
        }

        UpdateAppearance();
    }

    // ---------------------------------------------------------------------
    // Health
    // ---------------------------------------------------------------------

    /// <summary>
    /// Server only. Nothing on a client can reach this, and no client-supplied
    /// number reaches it either -- callers compute damage from server-side data.
    /// </summary>
    public void ApplyDamage(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;

        _health.Drain(Combatants.ResolveIncoming(amount, source, this));

        if (!IsAlive)
        {
            // Death clears everything: a slow that outlived you would apply to the
            // hero that comes back, which is a different fight.
            _status.Clear();
            GD.Print($"[combat] {CombatName} died to {label}");
        }
    }

    public void Heal(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;
        _health.Restore(amount);
    }

    /// ICombatant knockback entry point; see ServerPush for why it is a request.
    public void Displace(Vector3 destination, float travelSeconds) => ServerPush(destination, travelSeconds);

    /// <summary>
    /// Death lasts until the encounter resets. A timed respawn made a wipe cost
    /// nothing -- and worse, players came back before the wipe reset could fire and
    /// cancelled it, so the reset was unreachable in practice.
    /// </summary>
    public void OnEncounterReset()
    {
        if (!IsServer) return;

        _health.Fill();
        _mana.Fill();
        _status.Clear();
        ClearCooldowns();
        ServerTeleport(SpawnPoint);
        GD.Print($"[combat] {CombatName} revived");
    }

    /// <summary>
    /// Cooldowns do NOT survive a wipe.
    ///
    /// A fight you learn by repetition is only learnable if every attempt starts
    /// from the same state, and the reset delay is shorter than the longest
    /// cooldown -- so leaving them running meant every single retry deterministically
    /// began without the raid's biggest ability. That is not a cost, it is noise.
    /// The boss clears its own cooldowns on reset for exactly the same reason;
    /// leaving the players' running was an asymmetry with no argument behind it.
    /// </summary>
    private void ClearCooldowns()
    {
        _serverCooldowns.Clear();

        // The client's copy is display state and will not clear itself.
        for (int slot = 0; slot < Kit.Count; slot++)
            AcknowledgeCast(slot, 0.0);
    }

    // ---------------------------------------------------------------------
    // Server-initiated movement.
    //
    // The server cannot simply set a client-authoritative hero's position: the
    // client would overwrite it on its next tick. It has to adopt the destination
    // as the validated position AND ask the owner to go there. A modified client
    // could refuse -- in PvE that costs the cheater a mechanic and nobody else
    // anything, which is the price of prediction-free dodging everywhere else.
    // ---------------------------------------------------------------------

    public void ServerTeleport(Vector3 destination)
    {
        if (!IsServer) return;

        ServerPosition = destination;
        _clampGraceUntil = Now + 1.0;
        RpcId(PeerId, MethodName.SnapTo, destination);
    }

    public void ServerPush(Vector3 destination, float travelSeconds)
    {
        if (!IsServer || !IsAlive) return;

        ServerPosition = destination;

        // The client slides over travelSeconds, so its reported position trails the
        // server's for that long. Suspend the clamp rather than have it read a
        // mechanic as cheating and drag the hero back.
        _clampGraceUntil = Now + travelSeconds + 0.4;
        RpcId(PeerId, MethodName.BeginPush, destination, travelSeconds);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void SnapTo(Vector3 destination)
    {
        if (!IsLocalPlayer) return;

        GlobalPosition = destination;
        NetPosition = destination;
        _moveTarget = destination;
        _hasTarget = false;
        _pushing = false;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void BeginPush(Vector3 destination, float travelSeconds)
    {
        if (!IsLocalPlayer) return;

        _pushFrom = GlobalPosition;
        _pushTo = destination;
        _pushStart = Now;
        _pushEnd = _pushStart + travelSeconds;
        _pushing = true;
        _hasTarget = false;
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
        if (_pushing)
        {
            SlideThroughKnockback();
            return;
        }

        Vector3 flatToTarget = _moveTarget - GlobalPosition;
        flatToTarget.Y = 0f;

        if (_hasTarget && flatToTarget.Length() > ArriveDistance && IsAlive)
        {
            Vector3 dir = SteeringDirection();

            if (dir != Vector3.Zero)
            {
                float speed = EffectiveMoveSpeed;
                Velocity = new Vector3(dir.X * speed, 0f, dir.Z * speed);

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
        PublishPosition();
    }

    /// <summary>
    /// Which way to walk, in the ground plane.
    ///
    /// Two things here are scar tissue, and both once froze movement completely.
    ///
    /// A baked navigation mesh does NOT sit at the height of the ground it was
    /// baked from -- Recast places it a couple of voxels up, half a metre in this
    /// arena. So the agent's next waypoint is routinely directly ABOVE the hero,
    /// and any comparison that keeps the Y axis reads that as "somewhere to go"
    /// when there is nowhere to go. Flatten before measuring, never after.
    ///
    /// And when the agent hands back a point we are already standing on, steer at
    /// the final destination rather than returning zero. Returning zero leaves
    /// Velocity untouched and the hero stands still forever with a live order,
    /// which is indistinguishable from the game being broken.
    /// </summary>
    private Vector3 SteeringDirection()
    {
        Vector3 step = _useNavigation ? _agent.GetNextPathPosition() : _moveTarget;

        Vector3 dir = new(step.X - GlobalPosition.X, 0f, step.Z - GlobalPosition.Z);

        if (dir.LengthSquared() < 0.0004f)
            dir = new Vector3(_moveTarget.X - GlobalPosition.X, 0f, _moveTarget.Z - GlobalPosition.Z);

        return dir.LengthSquared() > 0.0001f ? dir.Normalized() : Vector3.Zero;
    }

    private void SlideThroughKnockback()
    {
        double now = Now;

        if (now >= _pushEnd)
        {
            GlobalPosition = _pushTo;
            _pushing = false;
        }
        else
        {
            float t = (float)((now - _pushStart) / Mathf.Max(_pushEnd - _pushStart, 0.0001));
            float eased = 1f - (1f - t) * (1f - t);   // decelerate into the landing
            GlobalPosition = _pushFrom.Lerp(_pushTo, eased);
        }

        Velocity = Vector3.Zero;
        PublishPosition();
    }

    /// The one value a client is allowed to write.
    private void PublishPosition()
    {
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
        if (Now < _clampGraceUntil)
        {
            // The server put the hero here itself, so its own destination is the
            // truth and the client's report legitimately trails it. Hold, rather
            // than adopting whatever the client currently claims -- adopting it
            // would hand a cheater a free window every time a mechanic moved them.
            return;
        }

        // MoveSync is an untrusted input channel exactly like CommandRouter, and is
        // validated with the same discipline: garbage rejected, arena bounds
        // enforced, and a per-tick budget with no additive slack.
        ServerPosition = Untrusted.AdvanceValidatedPosition(
            ServerPosition, NetPosition, EffectiveMoveSpeed, dt, ArenaRadius);
    }

    private void UpdateAppearance()
    {
        _body.Visible = IsAlive;
        _nose.Visible = IsAlive;

        // Statuses on the nameplate, not just on your own HUD. Seeing that an ally
        // is shielded, slowed or carrying a bomb is what lets anyone react to it;
        // a buff bar only you can read helps only you.
        _label.Text = IsAlive
            ? $"{PeerId}\n{Mathf.RoundToInt(Health)}/{Mathf.RoundToInt(HealthMax)}{StatusLine()}"
            : $"{PeerId}\nDEAD";

        _label.Modulate = !IsAlive ? new Color("64748b")
            : _health.Fraction > 0.35f ? Colors.White
            : new Color("f87171");
    }

    /// Compact enough to sit over a head without becoming a wall of text.
    private string StatusLine()
    {
        if (_status.Active.Count == 0) return "";

        var line = new System.Text.StringBuilder("\n");

        foreach (ActiveStatus status in _status.Active)
        {
            if (line.Length > 1) line.Append(' ');
            line.Append(status.Definition.DisplayName);
            if (status.Stacks > 1) line.Append('x').Append(status.Stacks);
        }

        return line.ToString();
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

        if (@event is InputEventKey { Pressed: true, Echo: false } key
            && TryAbilitySlot(key.Keycode, out int slot)
            && RtsCamera.MouseGroundPoint(this, out Vector3 aim))
        {
            // Ask. Do not act. The server decides whether this happened at all.
            RequestAbility(slot, aim);
            GetViewport().SetInputAsHandled();
        }
    }

    // ---------------------------------------------------------------------
    // Abilities
    //
    // There is no cast RPC on Hero any more. Everything a player asks for goes
    // through CommandRouter's single door, so growing the kit does not grow the
    // number of places a client can reach.
    // ---------------------------------------------------------------------

    public Ability AbilityAt(int slot) => slot >= 0 && slot < Kit.Count ? Kit[slot] : null;

    public bool IsAbilityReady(int slot, double now) => _serverCooldowns.IsReady(slot, now);

    public double AbilityReadyAt(int slot) => _serverCooldowns.ReadyAt(slot);

    public void StartCooldown(int slot, double now)
    {
        Ability ability = AbilityAt(slot);
        if (ability is not null) _serverCooldowns.Start(slot, now, ability.Cooldown);
    }

    /// <summary>How much of the cooldown is left, 1 to 0, for the local player's HUD.</summary>
    public float CooldownFraction(int slot, double now)
        => _clientCooldowns.Fraction(slot, now, AbilityAt(slot)?.Cooldown ?? 0f);

    /// <summary>Server: tell the owner what its cooldown really is. Zero clears one.</summary>
    public void AcknowledgeCast(int slot, double readyAt)
    {
        if (!IsServer) return;
        RpcId(PeerId, MethodName.OnCastAcknowledged, slot, readyAt);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void OnCastAcknowledged(int slot, double readyAt)
    {
        if (!IsLocalPlayer) return;
        _clientCooldowns.SetReadyAt(slot, readyAt);
    }

    private void RequestAbility(int slot, Vector3 aimPoint)
    {
        Ability ability = AbilityAt(slot);
        if (ability is null) return;

        // Optimistic, so the button responds on the frame you pressed it. The
        // server's acknowledgement either confirms this or clears it.
        _clientCooldowns.Start(slot, Now, ability.Cooldown);

        CommandRouter.Send(ClientCommandType.CastAbility, new Godot.Collections.Dictionary
        {
            ["slot"] = slot,
            ["aim"] = aimPoint,
        });
    }

    private static bool TryAbilitySlot(Key keycode, out int slot)
    {
        slot = keycode switch { Key.Q => 0, Key.W => 1, Key.E => 2, Key.R => 3, Key.Key1 => 4, _ => -1 };
        return slot >= 0;
    }
}
