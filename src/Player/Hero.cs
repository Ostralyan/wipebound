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

    /// Headroom on top of the measured replication delay, for jitter and a lost packet.
    public const double SpeedChangeMargin = 0.15;

    /// How close a claim must land to a server-commanded destination to count as
    /// the client having acknowledged it.
    public const float AcknowledgeDistance = 1.0f;

    /// Floors for how long the server waits to be told a commanded move landed.
    /// The real round trip is ADDED to these, never assumed to be inside them.
    public const float TeleportAcknowledgeSeconds = 1.0f;
    public const float PushAcknowledgeSeconds = 0.6f;

    [ExportGroup("Movement")]
    [Export] public float MoveSpeed = 7.0f;
    [Export] public float TurnSpeed = 14.0f;

    [ExportGroup("Stats")]
    [Export] public float ManaRegenPerSecond = 7f;

    [ExportGroup("Abilities")]
    /// Assigned by the server at spawn and carried on the spawn packet, so every
    /// peer knows which kit to build before anything else runs.
    [Export] public int ClassId { get; set; }

    /// Left empty, PlayerKit fills this in from ClassId.
    [Export] public Godot.Collections.Array<Ability> Kit { get; set; } = new();

    public HeroClass Class => (HeroClass)ClassId;

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
    private readonly Contribution _contribution = new();
    private readonly MovementValidator _movement = new() { ArenaRadius = ArenaRadius };

    [Export] public float Health { get => _health.Current; set => _health.Current = value; }
    [Export] public float HealthMax { get => _health.Max; set => _health.Max = value; }
    [Export] public float Mana { get => _mana.Current; set => _mana.Current = value; }
    [Export] public float ManaMax { get => _mana.Max; set => _mana.Max = value; }

    [Export] public float DamageDone { get => _contribution.DamageDone; set => _contribution.DamageDone = value; }
    [Export] public float HealingDone { get => _contribution.HealingDone; set => _contribution.HealingDone = value; }
    [Export] public float DamageTaken { get => _contribution.DamageTaken; set => _contribution.DamageTaken = value; }

    /// <summary>
    /// Total metres this client has claimed beyond what it could legally have
    /// travelled. Honest play leaves this at essentially zero; it is the number a
    /// ladder would look at before accepting a run.
    /// </summary>
    [Export] public float Overreach { get; set; }

    /// The whole status set as one small string. See StatusTracker for why a string.
    [Export] public string StatusPayload { get => _status.Encoded; set => _status.Decode(value); }

    /// The peer that owns this hero, carried by the node's name (see NetworkManager).
    public int PeerId { get; private set; }

    /// <summary>
    /// Whether this hero is the one at these controls.
    ///
    /// Compared against the id NetworkManager remembered, not against one asked
    /// of the peer. This is read every physics frame by every hero and by the
    /// HUD, and an ENet peer stops being able to answer for its own id the
    /// moment it goes inactive -- so on a server drop the old form raised an
    /// error per hero per frame until the scene came down.
    /// </summary>
    public bool IsLocalPlayer => PeerId != 0 && PeerId == NetworkManager.Instance.LocalPeerId;

    // --- ICombatant ---
    public string CombatName => $"hero {PeerId}";
    public int CombatId => PeerId;
    public Team Team => Team.Players;
    public bool IsAlive => !_health.IsEmpty;
    public ResourcePool HealthPool => _health;
    public ResourcePool ManaPool => _mana;
    public StatusTracker Status => _status;
    public Contribution Contribution => _contribution;
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
    public Vector3 ServerPosition => _movement.Validated;

    private static bool IsServer => NetworkManager.Instance.IsServer;
    private static double Now => NetClock.Instance.ServerTime;

    private MultiplayerSynchronizer _statsSync;
    private Label3D _label;
    private MeshInstance3D _body;
    private MeshInstance3D _nose;

    // Server-only. Never replicated, never accepted from a client. These are the
    // real cooldowns; the client's copies below are display state.
    private readonly CooldownSet _serverCooldowns = new();
    private readonly CooldownSet _clientCooldowns = new();

    /// While the server itself is moving this hero -- a knockback, a respawn --
    /// the client's reported position legitimately disagrees with the server's.
    /// The speed clamp stands down until this passes rather than fighting it.
    private double _clampGraceUntil;
    private bool _awaitingAcknowledgement;

    // Speed used for billing, which lags a slowdown by the replication window.
    private float _billingSpeed = -1f;
    private double _billingHoldUntil;

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

        if (Kit.Count == 0) Kit = PlayerKit.For(Class);
        _serverCooldowns.Resize(Kit.Count);
        _clientCooldowns.Resize(Kit.Count);

        _statsSync = GetNode<MultiplayerSynchronizer>("StatsSync");
        _label = GetNode<Label3D>("NameLabel");
        _body = GetNode<MeshInstance3D>("Body");
        _nose = GetNode<MeshInstance3D>("Nose");

        // SpawnPoint arrived with the spawn packet on the server-authoritative
        // synchronizer, so it is correct on every peer before anything moves.
        GlobalPosition = SpawnPoint;
        NetPosition = SpawnPoint;
        _movement.Reset(SpawnPoint);

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
        if (!IsServer || !IsAlive) return;
        Combatants.ResolveHealing(amount, source, this);
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
        _contribution.Clear();
        Overreach = 0f;
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

        _movement.Reset(destination);
        _awaitingAcknowledgement = true;
        _clampGraceUntil = Now + TeleportAcknowledgeSeconds + RoundTrip();
        RpcId(PeerId, MethodName.SnapTo, destination);
    }

    public void ServerPush(Vector3 destination, float travelSeconds)
    {
        if (!IsServer || !IsAlive) return;

        _movement.Reset(destination);

        // The client slides over travelSeconds, so it reports stale ground until it
        // arrives. Wait for it to say so rather than chasing it backwards.
        _awaitingAcknowledgement = true;
        _clampGraceUntil = Now + travelSeconds + PushAcknowledgeSeconds + RoundTrip();
        RpcId(PeerId, MethodName.BeginPush, destination, travelSeconds);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void SnapTo(Vector3 destination)
    {
        if (!IsLocalPlayer) return;

        GlobalPosition = destination;
        NetPosition = destination;
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
    }

    // ---------------------------------------------------------------------
    // Movement
    // ---------------------------------------------------------------------

    /// <summary>
    /// Walk where the keys say, in the direction the SCREEN faces.
    ///
    /// Camera-relative, not world-relative: the rig sits at a fixed 45-degree
    /// yaw, so mapping W to world -Z would send you diagonally away from the way
    /// you pressed. W means "up the screen" and nothing else.
    ///
    /// Input.GetVector clamps the stick to length 1, which is doing real safety
    /// work here and not just feel. A raw W+D sum is 1.41 long, and since the
    /// server bills claimed distance against EffectiveMoveSpeed with a 1.25
    /// tolerance, an un-normalised diagonal would have every honest player
    /// walking north-east flagged as a speed hacker.
    ///
    /// Facing follows the CURSOR rather than the direction of travel, which is
    /// the whole point of the scheme: you can back away from a boss while still
    /// aiming at it. Facing is cosmetic -- every ability carries its own aim
    /// point to the server -- so this adds no authority surface.
    /// </summary>
    private void DriveLocally(float dt)
    {
        if (_pushing)
        {
            SlideThroughKnockback();
            return;
        }

        Velocity = IsAlive ? WishDirection() * EffectiveMoveSpeed : Vector3.Zero;
        MoveAndSlide();
        HoldInsideArena();

        if (IsAlive)
        {
            FaceCursor(dt);
            ReadAbilityInput();
        }

        PublishPosition();
    }

    private Vector3 WishDirection()
    {
        Vector2 keys = Input.GetVector(
            Bindings.MoveLeft, Bindings.MoveRight, Bindings.MoveUp, Bindings.MoveDown);

        if (keys == Vector2.Zero) return Vector3.Zero;
        if (!RtsCamera.GroundBasis(this, out Vector3 forward, out Vector3 right)) return Vector3.Zero;

        // GetVector's Y is negative for "up", so subtracting points W at the screen.
        return right * keys.X - forward * keys.Y;
    }

    /// <summary>
    /// The arena has no walls, only a radius, so nothing physical stops a player
    /// walking into the void. Under click-to-move the destination was always a
    /// point on the ground and this never came up; a held key has no destination
    /// to be sane. The server clamps to the same radius, so a client that skips
    /// this only desyncs itself.
    /// </summary>
    private void HoldInsideArena()
    {
        var flat = new Vector2(GlobalPosition.X, GlobalPosition.Z);
        if (flat.LengthSquared() <= ArenaRadius * ArenaRadius) return;

        flat = flat.Normalized() * ArenaRadius;
        GlobalPosition = new Vector3(flat.X, GlobalPosition.Y, flat.Y);
    }

    private void FaceCursor(float dt)
    {
        if (!RtsCamera.MouseGroundPoint(this, out Vector3 look)) return;

        Vector3 toCursor = new(look.X - GlobalPosition.X, 0f, look.Z - GlobalPosition.Z);
        if (toCursor.LengthSquared() < 0.04f) return;

        // Godot's forward is -Z, so the yaw that points -Z along toCursor is this.
        float wanted = Mathf.Atan2(-toCursor.X, -toCursor.Z);
        Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, wanted, Smoothing(TurnSpeed, dt)), 0f);
    }

    /// <summary>
    /// Quick-cast, polled rather than edge-triggered: pressing a key casts at the
    /// cursor immediately, and HOLDING it re-casts the moment the cooldown ends.
    /// That is what makes a 0.9s basic attack something you hold instead of
    /// something you mash.
    ///
    /// The client's own cooldown copy is what throttles this. Without that gate a
    /// held key would ask the server sixty times a second for something it has
    /// already refused fifty-nine times.
    /// </summary>
    private void ReadAbilityInput()
    {
        double now = Now;
        int slots = Mathf.Min(Kit.Count, Bindings.AbilitySlots);

        for (int slot = 0; slot < slots; slot++)
        {
            if (!Input.IsActionPressed(Bindings.Ability(slot))) continue;
            if (!_clientCooldowns.IsReady(slot, now)) continue;
            if (!RtsCamera.MouseGroundPoint(this, out Vector3 aim)) continue;

            // Ask. Do not act. The server decides whether this happened at all.
            RequestAbility(slot, aim);
        }
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
        if (_awaitingAcknowledgement)
        {
            // The server moved this hero itself. Its destination is authoritative
            // until the client confirms arriving there -- chasing the client's
            // trailing claim would drag the authoritative position back onto ground
            // the hero has already been pushed off, and combat would resolve there.
            //
            // Nothing is billed meanwhile, and the allowance keeps filling so there
            // is no cliff when the wait ends.
            bool acknowledged = _movement.DistanceFrom(NetPosition) <= AcknowledgeDistance;

            if (!acknowledged && Now < _clampGraceUntil)
            {
                _movement.Idle(BillingSpeed(Now), dt);
                return;
            }

            _awaitingAcknowledgement = false;
        }

        // MoveSync is an untrusted input channel exactly like CommandRouter, and is
        // validated with the same discipline: garbage rejected, arena bounds
        // enforced, and travel billed against an allowance that accrues with time
        // rather than against a single physics tick.
        float overreach = _movement.Accept(NetPosition, BillingSpeed(Now), dt);
        if (overreach > 0f) Overreach += overreach;
    }

    /// <summary>
    /// The speed a claim is billed against. Speeding up takes effect at once --
    /// the server learns of a haste before the client does, so there is no gap to
    /// cover. Slowing down waits, because the client cannot yet know.
    /// </summary>
    private float BillingSpeed(double now)
    {
        float current = EffectiveMoveSpeed;

        if (_billingSpeed < 0f || current >= _billingSpeed - 0.001f)
        {
            _billingSpeed = current;
            _billingHoldUntil = 0.0;
            return current;
        }

        if (_billingHoldUntil <= 0.0) _billingHoldUntil = now + SpeedChangeGrace();
        if (now < _billingHoldUntil) return _billingSpeed;

        _billingSpeed = current;
        _billingHoldUntil = 0.0;
        return current;
    }

    /// <summary>
    /// How long to keep billing at the old speed after a slow lands.
    ///
    /// MEASURED, not guessed. A previous version hard-coded 0.4s, which happened to
    /// cover a localhost round trip and would have started charging honest players
    /// again the moment anybody played from another city. The client cannot know
    /// about a slow until the status has been published (one synchronizer interval)
    /// and has travelled (one round trip), so the grace is exactly those two things
    /// plus headroom for jitter.
    /// </summary>
    /// <summary>
    /// How long a server-commanded move needs before the client's own claim can
    /// possibly reflect it: the order has to travel there and the reply back.
    ///
    /// The two constants it is added to were tuned on localhost, where this term
    /// is zero. That is the exact mistake SpeedChangeGrace was written to fix,
    /// made again two methods away and left there -- and it does not show up
    /// until somebody is knocked back on a real connection, because a hero that
    /// is never displaced never waits to acknowledge anything.
    ///
    /// Measured at 80ms round trip: three honest bots were billed 219cm of
    /// overreach against a 200cm ranked limit, and 0cm with this term present.
    /// </summary>
    private static double RoundTrip() => NetClock.Instance?.WorstPeerRtt ?? 0.0;

    private double SpeedChangeGrace()
    {
        double publish = _statsSync is null
            ? 0.1
            : Mathf.Max(_statsSync.ReplicationInterval, _statsSync.DeltaInterval);

        return publish + NetClock.Instance.RttFor(PeerId) + SpeedChangeMargin;
    }

    private void UpdateAppearance()
    {
        _body.Visible = IsAlive;
        _nose.Visible = IsAlive;

        // Statuses on the nameplate, not just on your own HUD. Seeing that an ally
        // is shielded, slowed or carrying a bomb is what lets anyone react to it;
        // a buff bar only you can read helps only you.
        _label.Text = IsAlive
            ? $"{PlayerKit.NameOf(Class)} {PeerId}\n{Mathf.RoundToInt(Health)}/{Mathf.RoundToInt(HealthMax)}{StatusLine()}"
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

        // Whatever the cursor is over, if this ability wants a person. There is no
        // selected target anywhere in this game: you aim with the mouse, always,
        // and what differs between abilities is only what the cursor resolves to.
        int targetId = 0;
        if (ability.RequiresTarget)
        {
            ICombatant hovered = Combatants.UnderCursor(this, aimPoint, this, ability.Affects);
            if (hovered is null) return;
            targetId = hovered.CombatId;
        }

        // Optimistic, so the button responds on the frame you pressed it. The
        // server's acknowledgement either confirms this or clears it.
        _clientCooldowns.Start(slot, Now, ability.Cooldown);

        CommandRouter.Send(ClientCommandType.CastAbility, new Godot.Collections.Dictionary
        {
            ["slot"] = slot,
            ["aim"] = aimPoint,
            ["target"] = targetId,
        });
    }
}
