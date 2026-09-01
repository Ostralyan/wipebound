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

    /// How long after a wipe or a kill before the encounter restarts, so you can
    /// iterate without relaunching. Both paths genuinely use it now -- for a long
    /// time only the kill did, and a wipe simply left the boss standing with its
    /// health, phase and cooldowns intact for the respawning raid to run back into.
    [Export] public float ResetSeconds { get; set; } = 8f;

    /// Left empty, DefaultEncounter fills this in. Assign .tres phases here to
    /// override without touching code.
    [Export] public Godot.Collections.Array<BossPhase> Phases { get; set; } = new();

    // --- Replicated by StatsSync. Authority: the server. ---
    private readonly ResourcePool _health = new(4000f);
    private readonly StatusTracker _status = new();

    [Export] public float Health { get => _health.Current; set => _health.Current = value; }
    [Export] public float HealthMax { get => _health.Max; set => _health.Max = value; }
    [Export] public int PhaseIndex { get; set; }
    [Export] public string StatusPayload { get => _status.Encoded; set => _status.Decode(value); }

    // --- ICombatant ---
    public string CombatName => DisplayName;

    /// NPC sources share one identity. Fine while nothing NPC-applied is PerSource;
    /// adds will need real ids agreed across peers.
    public int CombatId => -1;
    public Team Team => Team.Enemies;
    public Vector3 CombatPosition => GlobalPosition;
    public bool IsAlive => !_health.IsEmpty;
    public ResourcePool HealthPool => _health;
    public StatusTracker Status => _status;
    public Node3D Node => this;

    /// Bosses are anchored. Knockback effects are safe to point at one; they simply
    /// do nothing, rather than every ability needing to ask what it hit.
    public void Displace(Vector3 destination, float travelSeconds) { }

    /// The boss resets itself through RestartEncounter; this exists so the reset
    /// broadcast can be sent to every combatant without special-casing anything.
    public void OnEncounterReset() { }

    private static bool IsServer => NetworkManager.Instance.IsServer;
    private static double Now => NetClock.Instance.ServerTime;

    private Label3D _label;

    // --- Server-only encounter state. None of it is replicated. Note there is no
    // "currently casting" field any more: casts live in CombatDirector, which is
    // what lets more than one be in flight at a time. ---
    private double _nextCastAt;
    private double _resetAt;
    private double _wipeAt;

    /// True once the fight has actually started. Distinguishes a wipe from the
    /// perfectly ordinary state of nobody having joined the server yet.
    private bool _engaged;
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

        if (!IsServer)
        {
            _status.PruneForDisplay(Now);
            return;
        }

        double now = Now;
        _status.Tick(this, now);

        if (!IsAlive)
        {
            if (now >= _resetAt) RestartEncounter();
            return;
        }

        UpdatePhase();

        // A wipe resets the encounter. Without this, players respawned after six
        // seconds and ran back into a boss that had kept its health, its phase, its
        // statuses and its cooldowns -- so a wipe cost nothing and meant nothing.
        if (Combatants.Living(this, this, TargetFilter.Enemies).Count == 0)
        {
            if (!_engaged) return;

            if (_wipeAt <= 0.0)
            {
                _wipeAt = now + ResetSeconds;
                CombatDirector.Instance.CancelFor(this);
                GD.Print($"[boss] raid wiped. Resetting in {ResetSeconds}s.");
            }
            else if (now >= _wipeAt)
            {
                RestartEncounter();
            }

            return;
        }

        _wipeAt = 0.0;

        // The director owns casts in flight. Asking it, rather than tracking a flag
        // here, is what makes overlapping mechanics a data change instead of a rewrite.
        if (CombatDirector.Instance.IsCasting(this)) return;
        if (now < _nextCastAt) return;

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
    // Warn -- handed to the director, which telegraphs and resolves it
    // ---------------------------------------------------------------------

    private void BeginCast(Ability ability, double now)
    {
        _engaged = true;
        _readyAt[ability] = now + ability.Cooldown;

        // Recovery is booked from the cast's end, not its start, so a long wind-up
        // does not eat the breathing room after it.
        _nextCastAt = now + ability.CastSeconds + (CurrentPhase?.RecoverySeconds ?? 2.0);

        CombatDirector.Instance.Begin(this, ability, AimPointFor(ability));

        GD.Print($"[boss] cast {ability.DisplayName} ({ability.Shape}) " +
                 $"telegraph={ability.CastSeconds:0.00}s");
    }

    // ---------------------------------------------------------------------
    // Health and lifecycle
    // ---------------------------------------------------------------------

    public void ApplyDamage(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;

        _health.Drain(Combatants.ResolveIncoming(amount, source, this));

        if (IsAlive) return;

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
        _status.Clear();
        PhaseIndex = 0;
        _readyAt.Clear();
        _nextCastAt = Now + 2.0;
        _resetAt = 0.0;
        _wipeAt = 0.0;
        _engaged = false;
        CombatDirector.Instance.CancelFor(this);
        ReviveRaid();
        GD.Print($"[boss] {DisplayName} reset.");
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// Wipe every trace of the previous session. Leaving and re-hosting used to
    /// leave the boss on whatever health and phase it had when you left.
    /// </summary>
    public void ResetForNewSession()
    {
        _health.Fill();
        _status.Clear();
        PhaseIndex = 0;
        _readyAt.Clear();
        _nextCastAt = 0.0;
        _resetAt = 0.0;
        _wipeAt = 0.0;
        _engaged = false;
    }

    /// <summary>Bring back everyone who died, wherever they died.</summary>
    private void ReviveRaid()
    {
        foreach (Node node in GetTree().GetNodesInGroup(Combatants.GroupName))
            if (node is ICombatant combatant && combatant.Team == Team.Players)
                combatant.OnEncounterReset();
    }

    private void UpdateLabel()
    {
        if (_label is null) return;

        _label.Text = IsAlive
            ? $"{DisplayName}\n{Mathf.RoundToInt(Health)}/{Mathf.RoundToInt(HealthMax)}"
            : $"{DisplayName}\nDEFEATED";
    }

    private static string Flat(Vector3 v) => $"({v.X:0.0}, {v.Z:0.0})";
}
