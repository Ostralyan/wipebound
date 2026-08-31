using Godot;
using System.Collections.Generic;
using Wipebound.Net;

namespace Wipebound.Combat;

/// <summary>
/// The encounter loop: decide, warn, resolve, recover.
///
/// The whole state machine runs on the server. Clients receive one broadcast per
/// cast and draw a picture; they hold no encounter state at all, because a
/// telegraph is a RENDERING OF A SERVER DECISION, not a piece of game state. If a
/// client never draws it, the damage still lands.
/// </summary>
public partial class Boss : Node3D, ICombatant
{
    public const string GroupName = "boss";

    [Export] public string DisplayName { get; set; } = "The Wipebringer";

    /// Seconds past a telegraph's visible end before the server resolves it. See
    /// BeginCast for why this exists at all; the real value also accounts for the
    /// worst connected round trip.
    [Export] public float MinimumResolveGrace { get; set; } = 0.12f;

    /// How long after a wipe or a kill before the encounter restarts, so you can
    /// iterate without relaunching.
    [Export] public float ResetSeconds { get; set; } = 8f;

    /// Left empty, DefaultEncounter fills this in. Assign .tres phases here to
    /// override without touching code.
    [Export] public Godot.Collections.Array<BossPhase> Phases { get; set; } = new();

    // --- Replicated by StatsSync. Authority: the server. ---
    private readonly ResourcePool _health = new(4000f);

    [Export] public float Health { get => _health.Current; set => _health.Current = value; }
    [Export] public float HealthMax { get => _health.Max; set => _health.Max = value; }
    [Export] public int PhaseIndex { get; set; }

    /// Fires on every peer the moment a telegraph appears, so the HUD can draw a
    /// cast bar without knowing anything about the encounter.
    [Signal] public delegate void CastStartedEventHandler(string label, double startTime, double endTime, Color color);

    // --- ICombatant ---
    public string CombatName => DisplayName;
    public Team Team => Team.Enemies;
    public Vector3 CombatPosition => GlobalPosition;
    public bool IsAlive => !_health.IsEmpty;
    public ResourcePool HealthPool => _health;
    public Node3D Node => this;

    /// Bosses are anchored. Knockback effects are safe to point at one; they simply
    /// do nothing, rather than every ability needing to ask what it hit.
    public void Displace(Vector3 destination, float travelSeconds) { }

    private static bool IsServer => NetworkManager.Instance.IsServer;
    private static double Now => NetClock.Instance.ServerTime;

    private Label3D _label;

    // --- Server-only encounter state. None of it is replicated. ---
    private Ability _casting;
    private TelegraphArea _area;
    private double _castEndAt;
    private double _resolveAt;
    private double _nextCastAt;
    private double _resetAt;
    private readonly Dictionary<Ability, double> _readyAt = new();
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        AddToGroup(GroupName);
        AddToGroup(Combatants.GroupName);
        _label = GetNode<Label3D>("NameLabel");
        _rng.Randomize();

        if (Phases.Count == 0)
            Phases = DefaultEncounter.Build();

        if (IsServer)
        {
            _health.Fill();
            PhaseIndex = 0;
        }
    }

    public BossPhase CurrentPhase =>
        Phases.Count == 0 ? null : Phases[Mathf.Clamp(PhaseIndex, 0, Phases.Count - 1)];

    public override void _PhysicsProcess(double delta)
    {
        UpdateLabel();

        if (!IsServer) return;

        double now = Now;

        if (!IsAlive)
        {
            if (now >= _resetAt) RestartEncounter();
            return;
        }

        UpdatePhase();

        // A cast in flight owns the loop until its deadline passes.
        if (_casting is not null)
        {
            if (now >= _resolveAt) Resolve(now);
            return;
        }

        if (now < _nextCastAt) return;

        // Nothing to fight if nobody is alive to fight it. Without this the boss
        // would keep casting into an empty arena on a dedicated server.
        if (Combatants.Living(this, this, TargetFilter.Enemies).Count == 0) return;

        Ability next = PickAbility(now);
        if (next is not null) BeginCast(next, now);
    }

    // ---------------------------------------------------------------------
    // Decide
    // ---------------------------------------------------------------------

    private void UpdatePhase()
    {
        float percent = _health.Fraction * 100f;
        int wanted = 0;

        // Phases are listed highest threshold first, so the last one whose gate we
        // are under is the one we are in.
        for (int i = 0; i < Phases.Count; i++)
            if (Phases[i] is not null && percent <= Phases[i].EntersAtHealthPercent)
                wanted = i;

        if (wanted == PhaseIndex) return;

        PhaseIndex = wanted;
        GD.Print($"[boss] entering phase {PhaseIndex}: {CurrentPhase?.Name}");
    }

    private Ability PickAbility(double now)
    {
        BossPhase phase = CurrentPhase;
        if (phase is null) return null;

        var ready = new List<Ability>();
        foreach (Ability ability in phase.Abilities)
        {
            if (ability is null) continue;
            if (_readyAt.TryGetValue(ability, out double readyAt) && now < readyAt) continue;
            ready.Add(ability);
        }

        if (ready.Count == 0) return null;
        return ready[(int)(_rng.Randi() % (uint)ready.Count)];
    }

    /// <summary>
    /// Where the mechanic is aimed. What the footprint then DOES with that point is
    /// the ability's AbilityOrigin, not this method's business -- which is why the
    /// old "a cone centred on the boss has nothing to aim at" special case is gone.
    /// </summary>
    private Vector3 AimPointFor(Ability ability)
    {
        List<ICombatant> enemies = Combatants.Living(this, this, TargetFilter.Enemies);
        if (enemies.Count == 0) return GlobalPosition;

        return ability.AiTargeting switch
        {
            AiTargeting.ArenaCentre => Vector3.Zero,
            AiTargeting.Self => GlobalPosition,
            AiTargeting.NearestEnemy => Combatants.ByDistance(enemies, GlobalPosition, nearest: true).CombatPosition,
            AiTargeting.FarthestEnemy => Combatants.ByDistance(enemies, GlobalPosition, nearest: false).CombatPosition,
            _ => enemies[(int)(_rng.Randi() % (uint)enemies.Count)].CombatPosition,
        };
    }

    // ---------------------------------------------------------------------
    // Warn
    // ---------------------------------------------------------------------

    private void BeginCast(Ability ability, double now)
    {
        _casting = ability;
        _area = ability.BuildArea(GlobalPosition, AimPointFor(ability));
        _castEndAt = now + ability.CastSeconds;
        _readyAt[ability] = now + ability.Cooldown;

        // ---- THE TRAILING EDGE ----
        //
        // Resolving the instant the telegraph visually ends produces the single
        // most infuriating bug in the genre: "I dodged that and still died."
        //
        // With one-way latency L, a client only starts drawing at L. Its circle
        // finishes at L + duration, and a player who steps out right then has that
        // move reach us at L + duration + L. Resolving at `duration` would judge
        // them on a position from L ago -- before they moved.
        //
        // So we wait one full round trip past the visual end. The damage lands
        // about a tenth of a second after the circle fills, which nobody perceives,
        // and the same wait also guarantees every client actually finished seeing
        // the warning before it bit them. One number, both problems.
        double grace = Mathf.Max(MinimumResolveGrace, NetClock.Instance.WorstPeerRtt);
        _resolveAt = _castEndAt + grace;

        // castStart and castEnd are ABSOLUTE times on the shared clock, never
        // "starting now". That is what lets a client whose packet arrived late draw
        // an already-partly-filled telegraph that still finishes on time, instead of
        // a full-length one that finishes late.
        Rpc(MethodName.ShowTelegraph, _area.ToDictionary(), ability.DisplayName,
            now, _castEndAt, ability.TelegraphColor);

        GD.Print($"[boss] cast {ability.DisplayName} ({ability.Shape}) " +
                 $"at {Flat(_area.Center)} r={_area.Radius} " +
                 $"telegraph={ability.CastSeconds:0.00}s grace={grace:0.000}s");
    }

    /// <summary>
    /// Draw the warning. Runs on every peer including the server, which simply
    /// ignores it when headless -- the visual has no authority over anything.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ShowTelegraph(Godot.Collections.Dictionary areaData, string label,
                               double castStart, double castEnd, Color color)
    {
        TelegraphArea area = TelegraphArea.FromDictionary(areaData);
        TelegraphView.Spawn(this, area, castStart, castEnd, color);
        EmitSignal(SignalName.CastStarted, label, castStart, castEnd, color);
    }

    // ---------------------------------------------------------------------
    // Resolve
    // ---------------------------------------------------------------------

    private void Resolve(double now)
    {
        Ability ability = _casting;
        _casting = null;
        _nextCastAt = now + (CurrentPhase?.RecoverySeconds ?? 2.0);

        List<ICombatant> candidates = Combatants.Living(this, this, ability.Affects);
        var targets = new List<ICombatant>();

        GD.Print($"[resolve] {ability.DisplayName} at {Flat(_area.Center)}");

        foreach (ICombatant candidate in candidates)
        {
            // CombatPosition is the validated position, never the raw claim.
            // Field() is negative inside and positive outside, in metres -- so this
            // line is also how you check the shader against the maths: stand on the
            // edge and watch the number cross zero.
            float field = _area.Field(candidate.CombatPosition);
            bool hit = field <= 0f;
            if (hit) targets.Add(candidate);

            GD.Print($"[resolve]   {candidate.CombatName} at {Flat(candidate.CombatPosition)} " +
                     $"field={field:+0.00;-0.00}m -> {(hit ? "HIT" : "safe")}");
        }

        var context = new EffectContext
        {
            AbilityName = ability.DisplayName,
            Caster = this,
            Area = _area,
            Targets = targets,
            Candidates = candidates,
        };

        foreach (AbilityEffect effect in ability.Effects)
        {
            if (effect is null) continue;
            GD.Print($"[resolve]   {effect.Describe(context)}");
            effect.Resolve(context);
        }
    }

    // ---------------------------------------------------------------------
    // Health and lifecycle
    // ---------------------------------------------------------------------

    public void ApplyDamage(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;

        _health.Drain(amount);

        if (IsAlive) return;

        _casting = null;
        _resetAt = Now + ResetSeconds;
        GD.Print($"[boss] {DisplayName} defeated. Resetting in {ResetSeconds}s.");
    }

    public void Heal(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;
        _health.Restore(amount);
    }

    private void RestartEncounter()
    {
        _health.Fill();
        PhaseIndex = 0;
        _readyAt.Clear();
        _casting = null;
        _nextCastAt = Now + 2.0;
        GD.Print($"[boss] {DisplayName} reset.");
    }

    // ---------------------------------------------------------------------

    private void UpdateLabel()
    {
        if (_label is null) return;

        _label.Text = IsAlive
            ? $"{DisplayName}\n{Mathf.RoundToInt(Health)}/{Mathf.RoundToInt(HealthMax)}"
            : $"{DisplayName}\nDEFEATED";
    }

    private static string Flat(Vector3 v) => $"({v.X:0.0}, {v.Z:0.0})";
}
