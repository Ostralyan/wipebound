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
    private readonly Contribution _contribution = new();

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
    public Contribution Contribution => _contribution;
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
    private int _loggedPhase = -1;
    private double _wipeAt;

    /// True once the fight has actually started. Distinguishes a wipe from the
    /// perfectly ordinary state of nobody having joined the server yet.
    private bool _engaged;
    private readonly Dictionary<Ability, double> _readyAt = new();
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        // The tracker reports its own transitions, and needs to know whose
        // and where.
        _status.Owner = this;
        _status.Journal = Session.RunRecorder.Instance?.Log;

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
                Session.RunRecorder.Instance?.CompleteAttempt(victory: false);
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

        // An interrupt is supposed to buy a window, not merely delay one cast by an
        // instant. Without this the lockout it applies did nothing at all and the
        // boss simply started its next mechanic on the following frame.
        if (_status.Silenced) return;

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

        // Also the FIRST one. Returning early whenever the wanted phase matched
        // the current one meant the opening was never recorded at all: a replay
        // showed nothing until the boss dropped through a threshold, so the phase
        // that most of a fight happens in was the one phase with no name.
        if (wanted == PhaseIndex && _loggedPhase == wanted) return;

        _loggedPhase = wanted;
        PhaseIndex = wanted;
        Session.RunRecorder.Instance?.Log.Phase(Now, this, CurrentPhase?.Name ?? "?", PhaseIndex);
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
    /// <summary>
    /// Where the mechanic is aimed, and at whom.
    ///
    /// The target id matters for AtTargetUnit abilities, which follow a person
    /// rather than landing on a place. Without it an NPC could not use that origin
    /// at all, which would be a strange asymmetry in a system whose whole point is
    /// that a boss mechanic and a player spell are the same object.
    ///
    /// Candidates come from the ability's own filter, so a boss ability aimed at
    /// Allies picks its own minions -- or itself.
    /// </summary>
    private Vector3 AimPointFor(Ability ability, out int targetId)
    {
        targetId = 0;

        List<ICombatant> candidates = Combatants.Living(this, this, ability.Affects);
        if (candidates.Count == 0) return GlobalPosition;

        ICombatant chosen = ability.AiTargeting switch
        {
            AiTargeting.ArenaCentre => null,
            AiTargeting.Self => this,
            AiTargeting.NearestEnemy => Combatants.ByDistance(candidates, GlobalPosition, nearest: true),
            AiTargeting.FarthestEnemy => Combatants.ByDistance(candidates, GlobalPosition, nearest: false),
            _ => candidates[(int)(_rng.Randi() % (uint)candidates.Count)],
        };

        if (chosen is null) return Vector3.Zero;

        targetId = chosen.CombatId;
        return chosen.CombatPosition;
    }

    // ---------------------------------------------------------------------
    // Warn -- handed to the director, which telegraphs and resolves it
    // ---------------------------------------------------------------------

    /// <summary>
    /// How long one ability keeps the boss busy: the wind-up, the channel if it
    /// has one, and the breathing room owed afterwards.
    ///
    /// Recovery is booked from the moment the mechanic actually ENDS, not from
    /// its start, so a long wind-up does not eat the pause after it. Channels
    /// made that distinction matter: a sweep resolves when its telegraph fills
    /// and then keeps going for seven more seconds, so a schedule that stopped
    /// at CastSeconds expired four to six seconds before the mechanic finished.
    /// Nothing overlapped -- the director refuses a second cast while one is in
    /// flight -- but the pause was already spent, so the boss opened its next
    /// mechanic on the frame the sweep ended, with no recovery at all after the
    /// longest and most movement-hungry thing in the fight.
    ///
    /// Pure and static so an encounter's pacing can be checked without standing
    /// a boss up.
    /// </summary>
    public static double OccupiedFor(Ability ability, double recoverySeconds)
    {
        if (ability is null) return recoverySeconds;

        return ability.CastSeconds + ability.ChannelSeconds + recoverySeconds;
    }

    private void BeginCast(Ability ability, double now)
    {
        if (!_engaged)
        {
            Session.RunRecorder.Instance?.BeginAttempt(DisplayName);

            // Announce the phase again on the next tick. UpdatePhase runs BEFORE
            // this in the same frame, so its first announcement was written to a
            // log that had not started yet and was cleared by the one that did.
            _loggedPhase = -1;
        }

        _engaged = true;
        _readyAt[ability] = now + ability.Cooldown;

        _nextCastAt = now + OccupiedFor(ability, CurrentPhase?.RecoverySeconds ?? 2.0);

        Vector3 aim = AimPointFor(ability, out int targetId);
        CombatDirector.Instance.Begin(this, ability, aim, targetId);

        GD.Print($"[boss] cast {ability.DisplayName} ({ability.Shape}) " +
                 $"telegraph={ability.CastSeconds:0.00}s");
    }

    // ---------------------------------------------------------------------
    // Health and lifecycle
    // ---------------------------------------------------------------------

    public void ApplyDamage(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;

        _health.Drain(Combatants.ResolveIncoming(amount, source, this, label));

        if (IsAlive) return;

        Session.RunRecorder.Instance?.Log.Death(Now, this, source, label);
        Session.RunRecorder.Instance?.CompleteAttempt(victory: true);
        _resetAt = Now + ResetSeconds;
        GD.Print($"[boss] {DisplayName} defeated. Resetting in {ResetSeconds}s.");
    }

    public void Heal(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive) return;
        Combatants.ResolveHealing(amount, source, this, label);
    }

    private void RestartEncounter()
    {
        // A new attempt announces its phase again: the log for it starts empty.
        _loggedPhase = -1;
        _health.Fill();
        _status.Clear(Now);
        _contribution.Clear();
        PhaseIndex = 0;
        _readyAt.Clear();
        _nextCastAt = Now + 2.0;
        _resetAt = 0.0;
        _wipeAt = 0.0;
        _engaged = false;

        // Not just this boss's casts: ground left burning from the previous attempt
        // would still be there when the raid comes back to life in it.
        CombatDirector.Instance.ResetEncounter();
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
        _status.Clear(Now);
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
