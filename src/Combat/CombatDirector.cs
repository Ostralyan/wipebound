using Godot;
using System.Collections.Generic;
using Wipebound.Net;

namespace Wipebound.Combat;

/// <summary>A patch of ground that keeps hurting. Server-side; clients draw it.</summary>
public sealed class HazardInstance
{
    /// Identifies the client-side visual, so it can be removed early.
    public long Id { get; init; }

    public Hazard Definition { get; init; }
    public ICombatant Owner { get; init; }
    public TelegraphArea Area { get; init; }
    public double ExpiresAt { get; init; }
    public double NextTickAt { get; set; }
}

/// <summary>One cast in flight. Server-side; clients only ever see the telegraph.</summary>
public sealed class CastInstance
{
    /// Flat, normalised, where this was aimed. A channel sweeps from here.
    public Vector3 AimDirection;

    /// Identifies the client-side telegraph, so an interrupted cast can take its
    /// warning off the ground instead of letting it fill and flash as if it landed.
    public long Id { get; init; }

    public Ability Ability { get; init; }
    public ICombatant Caster { get; init; }

    /// For AtTargetUnit abilities: who this was aimed at. Resolved again at the
    /// moment it lands, so a targeted heal follows the person rather than the
    /// ground they were standing on when it was cast.
    public int TargetId { get; init; }
    public TelegraphArea Area { get; init; }
    public double StartAt { get; init; }
    public double CastEndAt { get; init; }
    public double ResolveAt { get; init; }

    /// Set instead of removing, so a cast can be cancelled while the list that
    /// holds it is being walked.
    public bool Cancelled { get; set; }
}

/// <summary>
/// Runs every cast in the game, whoever started it.
///
/// Casts used to be four fields on Boss -- _casting, _area, _castEndAt, _resolveAt
/// -- which meant a boss could only ever have ONE mechanic in flight. Real
/// encounters violate that constantly: a slow arena-wide winds up while spot
/// mechanics fire underneath it. Reifying a cast as an object and holding a LIST
/// of them removes that ceiling, and makes interrupts a list removal rather than
/// a special case.
///
/// It also means a player ability and a boss mechanic travel the identical path.
/// The only asymmetry left in combat is where the aim point comes from.
/// </summary>
public partial class CombatDirector : Node
{
    public static CombatDirector Instance { get; private set; }

    /// Floor for the resolve grace; the real value also accounts for the worst
    /// connected round trip. See ComputeGrace.
    [Export] public float MinimumResolveGrace { get; set; } = 0.12f;

    /// Fires on every peer when a telegraph appears, so HUDs can draw a cast bar
    /// without knowing anything about the encounter.
    [Signal] public delegate void CastStartedEventHandler(int casterTeam, string casterName, string label,
                                                          double startTime, double endTime, Color color);

    /// Fires when a cast ends without resolving.
    [Signal] public delegate void CastInterruptedEventHandler(int casterTeam);

    private readonly CastQueue _casts = new();
    private readonly HazardField _hazards = new();
    private readonly ChannelField _channels = new();
    private readonly ProjectileField _projectiles = new();

    /// Reused so a tick that finishes nothing allocates nothing.
    private readonly List<ChannelInstance> _finished = new();
    private long _nextId = 1;

    /// Negative and unique, so a minion's statuses can be told apart from another's
    /// the same way one player's are told apart from another's.
    private int _nextMinionId = -100;

    private PackedScene _minionScene;

    private static bool IsServer => NetworkManager.Instance.IsServer;

    /// <summary>
    /// Whether there is anybody to broadcast to. Session boundaries fire while a
    /// client is still connecting and again while it is tearing down, and calling
    /// Rpc in either window errors out -- which is exactly what reset-on-join did.
    /// </summary>
    private bool CanBroadcast
        => IsServer
           && Multiplayer.MultiplayerPeer is not null
           && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;
    private static double Now => NetClock.Instance.ServerTime;

    public override void _Ready()
    {
        Instance = this;
        _minionScene = GD.Load<PackedScene>("res://src/Combat/Minion.tscn");
        NetworkManager.Instance.ModeChanged += OnSessionBoundary;
    }

    /// <summary>
    /// Hosting, joining or leaving ends whatever was happening.
    ///
    /// Session lifecycle was previously spread across NetworkManager, Boss and this
    /// class with nobody owning the boundary, so stale casts and boss health
    /// survived a leave-and-rehost. The director owns it because it is the one
    /// thing that already knows about every cast in flight.
    /// </summary>
    private void OnSessionBoundary()
    {
        ResetEncounter();

        foreach (Node node in GetTree().GetNodesInGroup(Boss.GroupName))
            if (node is Boss boss) boss.ResetForNewSession();

        foreach (Node node in GetTree().GetNodesInGroup(Combatants.GroupName))
            if (node is ICombatant combatant && combatant.Team == Team.Players)
                combatant.OnEncounterReset();
    }

    /// A channel counts. Otherwise an interrupt would sail straight past the one
    /// kind of cast that is actually worth interrupting -- the long one.
    public bool IsCasting(ICombatant caster)
        => _casts.IsCasting(caster) || _channels.IsChannelling(caster);

    /// <summary>
    /// Drop whatever this caster had in flight. Interrupts and wipes both land here;
    /// with casts as objects, stopping one is a list removal rather than a special
    /// case threaded through whoever was casting.
    /// </summary>
    public void CancelFor(ICombatant caster)
    {
        List<CastInstance> stopped = _casts.CancelFor(caster);

        // Projectiles already in the air keep flying. Interrupting a channel stops
        // the caster, not the world -- what is out there was already fired, and
        // having it vanish would read as the game taking a hit back.
        List<ChannelInstance> silenced = _channels.CancelFor(caster);
        foreach (ChannelInstance channel in silenced)
            if (CanBroadcast) Rpc(MethodName.EndTelegraphView, channel.Id);

        if (stopped.Count == 0) return;

        // Take the warnings off the ground too. A telegraph that keeps filling for a
        // cast that will never resolve is worse than no telegraph, because the whole
        // premise is that the circle tells the truth.
        if (!CanBroadcast) return;

        foreach (CastInstance cast in stopped) Rpc(MethodName.EndTelegraphView, cast.Id);
        if (caster is not null) Rpc(MethodName.CastEnded, (int)caster.Team);
    }

    /// <summary>
    /// Wipe every trace of the current fight: casts in flight, ground still burning,
    /// and the visuals for both.
    ///
    /// Hazards outliving a reset was a live bug -- Cinders lasts fourteen seconds
    /// and a reset takes eight, so fire from the previous attempt was still burning
    /// when the raid came back to life in it.
    /// </summary>
    public void ResetEncounter()
    {
        _casts.CancelAll();
        _hazards.Clear();
        _channels.Clear();
        _projectiles.Clear();
        if (IsServer) ClearMinions();

        // A peer with nobody to talk to still has its own drawings to clear.
        if (CanBroadcast)
        {
            Rpc(MethodName.ClearTelegraphViews);
            Rpc(MethodName.ClearProjectileViews);
        }
        else
        {
            TelegraphView.EndAll(this);
            ProjectileView.EndAll(this);
        }
    }

        /// <summary>
    /// Put a minion on the field. Server only; MultiplayerSpawner replicates it,
    /// and from there it is an ordinary combatant.
    /// </summary>
    public void SpawnMinion(Vector3 at, float health, TargetRule targeting = TargetRule.Nearest)
    {
        if (!IsServer || _minionScene is null) return;

        Node container = GetTree().GetFirstNodeInGroup(Minion.ContainerGroup);
        if (container is null) return;

        var minion = _minionScene.Instantiate<Minion>();
        minion.CombatId = _nextMinionId--;
        minion.NetPosition = new Vector3(at.X, 0f, at.Z);
        minion.HealthMax = health;
        minion.Health = health;
        minion.Targeting = targeting;

        container.AddChild(minion, true);
    }

    /// <summary>Nothing summoned survives the attempt that summoned it.</summary>
    private void ClearMinions()
    {
        Node container = GetTree()?.GetFirstNodeInGroup(Minion.ContainerGroup);
        if (container is null) return;

        foreach (Node child in container.GetChildren()) child.QueueFree();
    }

    // ---------------------------------------------------------------------
    // Hazards
    // ---------------------------------------------------------------------

    public void SpawnHazard(ICombatant owner, Hazard definition, TelegraphArea area, double now)
    {
        if (!IsServer || definition is null) return;

        GD.Print($"[hazard] {definition.DisplayName} at {Flat(area.Center)} r={area.Radius} for {definition.Duration}s");

        long id = _nextId++;

        _hazards.Add(new HazardInstance
        {
            Id = id,
            Definition = definition,
            Owner = owner,
            Area = area,
            ExpiresAt = now + definition.Duration,
            NextTickAt = now,
        });

        // Drawn as a telegraph that is already full and simply persists: a hazard is
        // not counting down to anything, it is dangerous the whole time.
        Rpc(MethodName.ShowHazard, id, area.ToDictionary(), now, now + definition.Duration, definition.Tint);
    }

    /// <summary>
    /// Start the sweep. Direction comes from where the cast was aimed, and turns
    /// from there at a fixed rate for the length of the channel.
    /// </summary>
    private void BeginChannel(CastInstance cast, double now)
    {
        Ability ability = cast.Ability;

        _channels.Add(new ChannelInstance
        {
            Id = cast.Id,
            Ability = ability,
            Owner = cast.Caster,
            StartDirection = cast.AimDirection,
            RotationRate = Mathf.DegToRad(ability.ChannelRotationDegrees),
            StartAt = now,
            EndsAt = now + ability.ChannelSeconds,
            NextTickAt = now,
        });

        GD.Print($"[channel] {cast.Caster.CombatName} :: {ability.DisplayName} for {ability.ChannelSeconds}s " +
                 $"at {ability.ChannelRotationDegrees} deg/s");
    }

    /// <summary>
    /// Fire each channel that is due, pointing wherever it has turned to by now.
    ///
    /// The footprint is rebuilt every tick from a rotated aim POINT rather than by
    /// poking a facing angle into the area, so this never has to agree with
    /// TelegraphArea about which way zero radians points.
    /// </summary>
    private void TickChannels(double now)
    {
        _finished.Clear();

        foreach (ChannelInstance channel in _channels.Advance(now, _finished))
        {
            Ability ability = channel.Ability;
            ICombatant owner = channel.Owner;

            // A caster that died or was freed mid-channel stops, exactly as a cast
            // does. Without this a dead boss keeps spraying.
            if (owner?.Node is null || !GodotObject.IsInstanceValid(owner.Node) || !owner.IsAlive)
            {
                channel.Cancelled = true;
                continue;
            }

            Vector3 origin = owner.CombatPosition;
            Vector3 direction = channel.DirectionAt(now);
            Vector3 aimPoint = origin + direction * Mathf.Max(1f, ability.Radius);
            TelegraphArea area = ability.BuildArea(origin, aimPoint);

            List<ICombatant> candidates = Combatants.Living(this, owner, ability.Affects);
            var targets = new List<ICombatant>();

            // A channel may also have a footprint of its own -- a beam that burns
            // what it sweeps over as well as firing. Costs nothing when it does not.
            foreach (ICombatant candidate in candidates)
                if (area.Field(candidate.CombatPosition) <= 0f)
                    targets.Add(candidate);

            var context = new EffectContext
            {
                AbilityName = ability.DisplayName,
                Caster = owner,
                Area = area,
                AimDirection = direction,
                Targets = targets,
                Candidates = candidates,
                Now = now,
            };

            foreach (AbilityEffect effect in ability.Effects) effect?.Resolve(context);
        }
    }

    /// <summary>
    /// Advance everything in the air and hurt whoever it reached.
    ///
    /// Damage is applied out here rather than inside the field's walk, because
    /// damage kills and death reaches back into the world.
    /// </summary>
    private void TickProjectiles(double now)
    {
        if (_projectiles.Count == 0) return;

        List<ProjectileHit> hits = _projectiles.Advance(
            now, projectile => Combatants.Living(this, projectile.Owner, projectile.Definition.Affects));

        foreach (ProjectileHit hit in hits)
        {
            Projectile definition = hit.Projectile.Definition;
            hit.Target.ApplyDamage(definition.Damage, hit.Projectile.Owner, definition.Id);

            // Same spirit as the resolve log: the server says who it believes was
            // standing where, so "that one missed me" can be checked rather than
            // argued about.
            GD.Print($"[projectile] {definition.Id} hit {hit.Target.CombatName} at {Flat(hit.Target.CombatPosition)}");

            if (CanBroadcast) Rpc(MethodName.EndProjectile, hit.Projectile.Id);
            else ProjectileView.EndOne(this, hit.Projectile.Id);
        }
    }

    /// <summary>
    /// Put one projectile in the air. Server only.
    ///
    /// Clients are told once, at spawn, and compute the rest of the flight from
    /// the shared clock -- so a stream costs one packet per projectile rather
    /// than one per projectile per frame.
    /// </summary>
    public void FireProjectile(ICombatant owner, Projectile definition, Vector3 direction, double now)
    {
        if (!IsServer || definition is null || owner is null) return;

        var flat = new Vector3(direction.X, 0f, direction.Z);
        if (flat.LengthSquared() < 0.0001f) return;
        flat = flat.Normalized();

        long id = _nextId++;
        Vector3 origin = owner.CombatPosition;
        double expiresAt = now + definition.Lifetime;

        _projectiles.Add(new ProjectileInstance
        {
            Id = id,
            Definition = definition,
            Owner = owner,
            Origin = origin,
            Direction = flat,
            SpawnedAt = now,
            ExpiresAt = expiresAt,
        });

        if (CanBroadcast)
            Rpc(MethodName.ShowProjectile, id, origin, flat, definition.Speed,
                definition.Radius, now, expiresAt, definition.Tint);
        else
            ProjectileView.Spawn(this, id, origin, flat, definition.Speed,
                                 definition.Radius, now, expiresAt, definition.Tint);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ShowProjectile(long id, Vector3 origin, Vector3 direction, float speed,
                                float radius, double spawnedAt, double expiresAt, Color tint)
        => ProjectileView.Spawn(this, id, origin, direction, speed, radius, spawnedAt, expiresAt, tint);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void EndProjectile(long id) => ProjectileView.EndOne(this, id);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ClearProjectileViews() => ProjectileView.EndAll(this);

    private void TickHazards(double now)
    {
        foreach (HazardInstance hazard in _hazards.Advance(now))
        {
            List<ICombatant> candidates = Combatants.Living(this, hazard.Owner, hazard.Definition.Affects);
            var standing = new List<ICombatant>();

            foreach (ICombatant candidate in candidates)
                if (hazard.Area.Contains(candidate.CombatPosition))
                    standing.Add(candidate);

            if (standing.Count == 0) continue;

            // Same spirit as the resolve log: say who the server believes is standing
            // in it, so "I was not in that" can be checked rather than argued.
            GD.Print($"[hazard] {hazard.Definition.DisplayName} caught {standing.Count} " +
                     $"({string.Join(", ", standing.ConvertAll(c => c.CombatName))})");

            var context = new EffectContext
            {
                AbilityName = hazard.Definition.DisplayName,
                Caster = hazard.Owner,
                Area = hazard.Area,
                Targets = standing,
                Candidates = candidates,
                Now = now,
            };

            foreach (AbilityEffect effect in hazard.Definition.OnTick)
                effect?.Resolve(context);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ShowHazard(long id, Godot.Collections.Dictionary areaData, double from, double until, Color color)
        => TelegraphView.Spawn(this, id, TelegraphArea.FromDictionary(areaData), from, from, color, until);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void EndTelegraphView(long id) => TelegraphView.EndOne(this, id);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ClearTelegraphViews() => TelegraphView.EndAll(this);

    /// Lets a HUD stop showing a cast bar for something that was interrupted.
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void CastEnded(int casterTeam) => EmitSignal(SignalName.CastInterrupted, casterTeam);

    /// <summary>
    /// Start a cast. The caller is responsible for having already validated it --
    /// cost, cooldown, silence. By the time it reaches here it is happening.
    /// </summary>
    public CastInstance Begin(ICombatant caster, Ability ability, Vector3 aimPoint, int targetId = 0)
    {
        if (!IsServer || ability is null || caster is null) return null;

        double now = Now;
        TelegraphArea area = ability.BuildArea(caster.CombatPosition, aimPoint);

        var aimDirection = new Vector3(aimPoint.X - caster.CombatPosition.X, 0f,
                                       aimPoint.Z - caster.CombatPosition.Z);
        aimDirection = aimDirection.LengthSquared() > 0.0001f ? aimDirection.Normalized() : Vector3.Forward;
        double castEnd = now + ability.CastSeconds;

        var cast = new CastInstance
        {
            Id = _nextId++,
            Ability = ability,
            TargetId = targetId,
            Caster = caster,
            Area = area,
            AimDirection = aimDirection,
            StartAt = now,
            CastEndAt = castEnd,
            ResolveAt = castEnd + ComputeGrace(ability),
        };

        _casts.Add(cast);

        // castStart and castEnd are ABSOLUTE times on the shared clock, never
        // "starting now". That is what lets a client whose packet arrived late draw
        // an already part-filled telegraph that still finishes on time.
        Rpc(MethodName.ShowTelegraph, cast.Id, area.ToDictionary(), ability.DisplayName,
            now, castEnd, ability.TelegraphColor, (int)caster.Team, caster.CombatName,
            ability.DrawsTelegraph);

        return cast;
    }

    /// <summary>
    /// THE TRAILING EDGE.
    ///
    /// Resolving the instant a telegraph visually ends produces the worst bug in the
    /// genre: "I dodged that and still died." With one-way latency L a client only
    /// starts drawing at L, its circle finishes at L + duration, and a player
    /// stepping out right then has that move reach us at L + duration + L.
    /// Resolving at `duration` judges them on a position from before they moved.
    ///
    /// So we wait one full round trip past the visual end. The damage lands about a
    /// tenth of a second late, which nobody perceives, and the same wait guarantees
    /// every client finished seeing the warning.
    ///
    /// An ability with NO telegraph gets no grace at all. Nothing was shown, so
    /// nothing could be dodged, and waiting would only delay the caster's feedback.
    /// </summary>
    private double ComputeGrace(Ability ability)
        => ability.ShowTelegraph
            ? Mathf.Max(MinimumResolveGrace, NetClock.Instance.WorstPeerRtt)
            : 0.0;

    public override void _PhysicsProcess(double delta)
    {
        if (!IsServer) return;

        double now = Now;
        TickHazards(now);
        TickChannels(now);
        TickProjectiles(now);

        _casts.Process(
            now,
            cast => cast.Caster?.Node is not null
                    && GodotObject.IsInstanceValid(cast.Caster.Node)
                    && cast.Caster.IsAlive,
            cast => Resolve(cast, now));
    }

    private void Resolve(CastInstance cast, double now)
    {
        Ability ability = cast.Ability;

        // A channel does not resolve here. The telegraph that just filled was the
        // WINDUP; what it earns is an interval, not a moment, so hand it to the
        // channel field and let it tick.
        if (ability.IsChannelled)
        {
            BeginChannel(cast, now);
            return;
        }

        if (!TryAreaAt(cast, out TelegraphArea area))
        {
            GD.Print($"[resolve] {cast.Caster.CombatName} :: {ability.DisplayName} fizzled, its target is gone");
            return;
        }
        List<ICombatant> candidates = Combatants.Living(this, cast.Caster, ability.Affects);
        var targets = new List<ICombatant>();

        GD.Print($"[resolve] {cast.Caster.CombatName} :: {ability.DisplayName} at {Flat(area.Center)}");

        foreach (ICombatant candidate in candidates)
        {
            // CombatPosition is the validated position, never the raw claim. Field()
            // is negative inside and positive outside, in metres -- so this line is
            // also how you check the shader against the maths: stand on the edge and
            // watch the number cross zero.
            float field = area.Field(candidate.CombatPosition);
            bool hit = field <= 0f;
            if (hit) targets.Add(candidate);

            GD.Print($"[resolve]   {candidate.CombatName} at {Flat(candidate.CombatPosition)} " +
                     $"field={field:+0.00;-0.00}m -> {(hit ? "HIT" : "safe")}");
        }

        var context = new EffectContext
        {
            AbilityName = ability.DisplayName,
            Caster = cast.Caster,
            Area = area,
            AimDirection = cast.AimDirection,
            Targets = targets,
            Candidates = candidates,
            Now = now,
        };

        foreach (AbilityEffect effect in ability.Effects)
        {
            if (effect is null) continue;
            GD.Print($"[resolve]   {effect.Describe(context)}");
            effect.Resolve(context);
        }
    }

    /// <summary>
    /// Where the ability actually lands, or nothing if it no longer has a target.
    ///
    /// Frozen for every footprint that is a PLACE, because a telegraph rendered on
    /// a client must resolve where it was drawn. Recomputed for the one footprint
    /// that is a PERSON, so a targeted heal follows whoever it was cast on.
    ///
    /// A target that died in the meantime FIZZLES. Falling back to the ground they
    /// were standing on would land a heal, or a mark, on whoever happened to walk
    /// over that spot -- an ability aimed at a person should hit that person or
    /// nobody.
    /// </summary>
    private bool TryAreaAt(CastInstance cast, out TelegraphArea area)
    {
        area = cast.Area;
        if (!cast.Ability.RequiresTarget) return true;

        ICombatant target = Combatants.ById(this, cast.TargetId);
        if (target is null || !target.IsAlive) return false;

        area = cast.Ability.BuildArea(cast.Caster.CombatPosition, target.CombatPosition);
        return true;
    }

    /// <summary>
    /// Draw the warning. Runs on every peer including the server, which ignores it
    /// when headless -- the visual has no authority over anything.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ShowTelegraph(long id, Godot.Collections.Dictionary areaData, string label,
                               double castStart, double castEnd, Color color,
                               int casterTeam, string casterName, bool drawFootprint)
    {
        if (drawFootprint)
            TelegraphView.Spawn(this, id, TelegraphArea.FromDictionary(areaData), castStart, castEnd, color);

        EmitSignal(SignalName.CastStarted, casterTeam, casterName, label, castStart, castEnd, color);
    }

    private static string Flat(Vector3 v) => $"({v.X:0.0}, {v.Z:0.0})";
}
