using Godot;
using System.Collections.Generic;
using Wipebound.Net;
using Wipebound.Player;

namespace Wipebound.Combat;

/// <summary>
/// The encounter loop: decide, warn, resolve, recover.
///
/// The whole state machine runs on the server. Clients receive one broadcast per
/// cast and draw a picture; they hold no encounter state at all, because a
/// telegraph is a RENDERING OF A SERVER DECISION, not a piece of game state. If a
/// client never draws it, the damage still lands.
/// </summary>
public partial class Boss : Node3D
{
    public const string GroupName = "boss";

    [Export] public string DisplayName { get; set; } = "The Wipebringer";
    [Export] public float MaxHealth { get; set; } = 4000f;

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
    [Export] public float Health { get; set; } = 4000f;
    [Export] public int PhaseIndex { get; set; }

    /// Fires on every peer the moment a telegraph appears, so the HUD can draw a
    /// cast bar without knowing anything about the encounter.
    [Signal] public delegate void CastStartedEventHandler(string label, double startTime, double endTime, Color color);

    public bool IsAlive => Health > 0f;

    private static bool IsServer => NetworkManager.Instance.IsServer;
    private static double Now => NetClock.Instance.ServerTime;

    private Label3D _label;

    // --- Server-only encounter state. None of it is replicated. ---
    private BossAbility _casting;
    private TelegraphArea _area;
    private double _castEndAt;
    private double _resolveAt;
    private double _nextCastAt;
    private double _resetAt;
    private readonly Dictionary<BossAbility, double> _readyAt = new();
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        AddToGroup(GroupName);
        _label = GetNode<Label3D>("NameLabel");
        _rng.Randomize();

        if (Phases.Count == 0)
            Phases = DefaultEncounter.Build();

        if (IsServer)
        {
            Health = MaxHealth;
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
        if (LivingHeroes().Count == 0) return;

        BossAbility next = PickAbility(now);
        if (next is not null) BeginCast(next, now);
    }

    // ---------------------------------------------------------------------
    // Decide
    // ---------------------------------------------------------------------

    private void UpdatePhase()
    {
        float percent = Health / MaxHealth * 100f;
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

    private BossAbility PickAbility(double now)
    {
        BossPhase phase = CurrentPhase;
        if (phase is null) return null;

        var ready = new List<BossAbility>();
        foreach (BossAbility ability in phase.Abilities)
        {
            if (ability is null) continue;
            if (_readyAt.TryGetValue(ability, out double readyAt) && now < readyAt) continue;
            ready.Add(ability);
        }

        if (ready.Count == 0) return null;
        return ready[(int)(_rng.Randi() % (uint)ready.Count)];
    }

    /// <summary>
    /// Where the mechanic is aimed. Radial shapes land ON this point; directional
    /// shapes start at the boss and point AT it.
    /// </summary>
    private Vector3 TargetPointFor(BossAbility ability)
    {
        List<Hero> heroes = LivingHeroes();
        if (heroes.Count == 0) return GlobalPosition;

        switch (ability.Targeting)
        {
            case TargetingRule.ArenaCenter:
                return Vector3.Zero;

            case TargetingRule.BossPosition:
                // "The boss's own position" gives a cone nothing to aim at, so a
                // boss-centred directional mechanic sweeps toward whoever is closest.
                return ability.Shape is TelegraphShape.Cone or TelegraphShape.Rectangle
                    ? ByDistance(heroes, nearest: true).ServerPosition
                    : GlobalPosition;

            case TargetingRule.NearestPlayer:
                return ByDistance(heroes, nearest: true).ServerPosition;

            case TargetingRule.FarthestPlayer:
                return ByDistance(heroes, nearest: false).ServerPosition;

            default:
                return heroes[(int)(_rng.Randi() % (uint)heroes.Count)].ServerPosition;
        }
    }

    // ---------------------------------------------------------------------
    // Warn
    // ---------------------------------------------------------------------

    private void BeginCast(BossAbility ability, double now)
    {
        _casting = ability;
        _area = ability.BuildArea(GlobalPosition, TargetPointFor(ability));
        _castEndAt = now + ability.TelegraphSeconds;
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
                 $"telegraph={ability.TelegraphSeconds:0.00}s grace={grace:0.000}s");
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
        BossAbility ability = _casting;
        _casting = null;
        _nextCastAt = now + (CurrentPhase?.RecoverySeconds ?? 2.0);

        List<Hero> everyone = LivingHeroes();
        var inside = new List<Hero>();

        GD.Print($"[resolve] {ability.DisplayName} at {Flat(_area.Center)}");

        foreach (Hero hero in everyone)
        {
            // The validated position, never the raw claim. Field() is negative
            // inside and positive outside, in metres -- so this line is also how
            // you check the shader against the maths: stand on the edge and watch
            // the number cross zero.
            float field = _area.Field(hero.ServerPosition);
            bool hit = field <= 0f;
            if (hit) inside.Add(hero);

            GD.Print($"[resolve]   hero {hero.PeerId} at {Flat(hero.ServerPosition)} " +
                     $"field={field:+0.00;-0.00}m -> {(hit ? "HIT" : "safe")}");
        }

        var context = new EffectContext
        {
            AbilityName = ability.DisplayName,
            Area = _area,
            Inside = inside,
            Everyone = everyone,
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

    public void ApplyDamage(float amount)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;

        Health = Mathf.Max(0f, Health - amount);

        if (IsAlive) return;

        _casting = null;
        _resetAt = Now + ResetSeconds;
        GD.Print($"[boss] {DisplayName} defeated. Resetting in {ResetSeconds}s.");
    }

    private void RestartEncounter()
    {
        Health = MaxHealth;
        PhaseIndex = 0;
        _readyAt.Clear();
        _casting = null;
        _nextCastAt = Now + 2.0;
        GD.Print($"[boss] {DisplayName} reset.");
    }

    // ---------------------------------------------------------------------

    private List<Hero> LivingHeroes()
    {
        var living = new List<Hero>();
        foreach (Node node in GetTree().GetNodesInGroup(Hero.GroupName))
            if (node is Hero hero && hero.IsAlive)
                living.Add(hero);
        return living;
    }

    private Hero ByDistance(List<Hero> heroes, bool nearest)
    {
        Hero best = heroes[0];
        float bestDistance = best.ServerPosition.DistanceSquaredTo(GlobalPosition);

        foreach (Hero hero in heroes)
        {
            float distance = hero.ServerPosition.DistanceSquaredTo(GlobalPosition);
            if (nearest ? distance < bestDistance : distance > bestDistance)
            {
                best = hero;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void UpdateLabel()
    {
        if (_label is null) return;

        _label.Text = IsAlive
            ? $"{DisplayName}\n{Mathf.RoundToInt(Health)}/{Mathf.RoundToInt(MaxHealth)}"
            : $"{DisplayName}\nDEFEATED";
    }

    private static string Flat(Vector3 v) => $"({v.X:0.0}, {v.Z:0.0})";
}
