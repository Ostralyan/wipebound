using Godot;
using System.Collections.Generic;
using Wipebound.Net;

namespace Wipebound.Combat;

/// <summary>
/// A summoned enemy.
///
/// AUTHORITY IS THE OPPOSITE WAY ROUND FROM A HERO, on purpose. A hero owns its
/// own position because the player has to dodge instantly and prediction is not
/// worth writing for that. Nobody is dodging on a minion's behalf, so the server
/// owns it completely: it decides where the minion is and clients interpolate
/// toward what they are told. That is both simpler and impossible to cheat.
///
/// Everything else is shared with the rest of combat -- it is an ICombatant, so
/// every telegraph, effect, status and hazard already applies to it without a
/// line of new code.
/// </summary>
public partial class Minion : CharacterBody3D, ICombatant
{
    public const string ContainerGroup = "minion_root";

    [Export] public string Title { get; set; } = "Shade";
    [Export] public float MoveSpeed { get; set; } = 5.5f;

    /// How close it wants to be before it starts swinging.
    [Export] public float PreferredRange { get; set; } = 2.2f;

    [Export] public float CorpseSeconds { get; set; } = 1.5f;

    /// How it decides who it is coming for. Set per summon, so different adds can
    /// behave differently in the same fight.
    [Export] public TargetRule Targeting { get; set; } = TargetRule.Nearest;

    /// Fixate only: how long before it loses interest and picks again. Long enough
    /// to be a problem, short enough that nobody becomes the permanent victim.
    [Export] public float FixateSeconds { get; set; } = 9f;

    // --- Replicated. Authority: the server, for all of it. ---
    [Export] public int CombatId { get; set; } = -100;
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public float NetFacing { get; set; }
    [Export] public float Health { get => _health.Current; set => _health.Current = value; }
    [Export] public float HealthMax { get => _health.Max; set => _health.Max = value; }
    [Export] public string StatusPayload { get => _status.Encoded; set => _status.Decode(value); }

    private readonly ResourcePool _health = new(90f);
    private readonly StatusTracker _status = new();
    private readonly Contribution _contribution = new();

    public string CombatName => $"{Title} {-CombatId}";
    public Team Team => Team.Enemies;
    public Vector3 CombatPosition => GlobalPosition;
    public bool IsAlive => !_health.IsEmpty;
    public ResourcePool HealthPool => _health;
    public StatusTracker Status => _status;
    public Contribution Contribution => _contribution;
    public Node3D Node => this;

    /// Minions are shoved around like anything else, and because the server owns
    /// their position it can simply move them.
    public void Displace(Vector3 destination, float travelSeconds) => GlobalPosition = destination;

    public void OnEncounterReset() { }

    /// What it does when it reaches you. Built in code for the same reason the
    /// encounter is; assign in the inspector to override.
    public Ability Attack { get; set; }

    private static bool IsServer => NetworkManager.Instance.IsServer;
    private static double Now => NetClock.Instance.ServerTime;

    private Label3D _label;
    private double _attackReadyAt;
    private double _diesAt;

    // Fixate state.
    private ICombatant _fixation;
    private double _fixateUntil;
    private double _markAt;

    /// Who has been hurting it lately, for HighestRecentDamage.
    private readonly AttentionTable _attention = new();

    public override void _Ready()
    {
        AddToGroup(Combatants.GroupName);
        _label = GetNode<Label3D>("NameLabel");
        Attack ??= MinionKit.Claw();

        GlobalPosition = NetPosition;
        if (IsServer) _health.Fill();
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        UpdateLabel();

        if (!IsServer)
        {
            // Told, not guessed. Smooth toward it so a 20Hz stream does not stutter.
            GlobalPosition = GlobalPosition.Lerp(NetPosition, 1f - Mathf.Exp(-18f * dt));
            Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, NetFacing, 1f - Mathf.Exp(-18f * dt)), 0f);
            _status.PruneForDisplay(Now);
            return;
        }

        double now = Now;
        _status.Tick(this, now);
        if (Targeting == TargetRule.HighestRecentDamage) _attention.Forget(now, dt);

        if (!IsAlive)
        {
            if (_diesAt <= 0.0) _diesAt = now + CorpseSeconds;
            else if (now >= _diesAt) QueueFree();
            return;
        }

        Hunt(dt, now);

        NetPosition = GlobalPosition;
        NetFacing = Rotation.Y;
    }

    private void Hunt(float dt, double now)
    {
        ICombatant prey = ChooseTarget(now);
        if (prey is null)
        {
            Velocity = Vector3.Zero;
            MoveAndSlide();
            return;
        }

        Vector3 toPrey = prey.CombatPosition - GlobalPosition;
        toPrey.Y = 0f;
        float distance = toPrey.Length();

        if (distance > PreferredRange && distance > 0.01f)
        {
            Vector3 direction = toPrey / distance;
            float speed = _status.Rooted ? 0f : MoveSpeed * _status.MoveSpeedMultiplier;
            Velocity = new Vector3(direction.X * speed, 0f, direction.Z * speed);
            Rotation = new Vector3(0f, Mathf.Atan2(-direction.X, -direction.Z), 0f);
        }
        else
        {
            Velocity = Vector3.Zero;
        }

        MoveAndSlide();

        if (distance > PreferredRange + 1f) return;
        if (_status.Silenced || now < _attackReadyAt) return;

        _attackReadyAt = now + Attack.Cooldown;
        CombatDirector.Instance.Begin(this, Attack, prey.CombatPosition);
    }

    private ICombatant ChooseTarget(double now)
    {
        List<ICombatant> prey = Combatants.Living(this, this, TargetFilter.Enemies);
        if (prey.Count == 0)
        {
            _fixation?.Status.Remove(StatusLibrary.Hunted, CombatId);
            _fixation = null;
            return null;
        }

        if (Targeting != TargetRule.Fixate)
            return TargetSelection.Choose(Targeting, prey, GlobalPosition, null, _attention.Scores);

        // Lose interest on a timer, so being hunted is a moment rather than a role.
        ICombatant keeping = now >= _fixateUntil ? null : _fixation;
        ICombatant chosen = TargetSelection.Choose(TargetRule.Fixate, prey, GlobalPosition, keeping);

        if (!ReferenceEquals(chosen, _fixation))
        {
            // Release the previous victim explicitly. The marker used to linger for
            // its whole duration after a rotation, which left other minions avoiding
            // somebody nothing was chasing any more.
            _fixation?.Status.Remove(StatusLibrary.Hunted, CombatId);

            _fixation = chosen;
            _fixateUntil = now + FixateSeconds;
            _markAt = 0.0;
            GD.Print($"[minion] {CombatName} fixates on {chosen?.CombatName}");
        }

        // Keep the marker alive, refreshed rather than reapplied every frame. It is
        // what makes being chased visible -- to the target and to everyone who
        // could help -- and it is how two adds avoid picking the same person.
        if (chosen is not null && now >= _markAt)
        {
            _markAt = now + 2.0;
            chosen.Status.Apply(StatusLibrary.Get(StatusLibrary.Hunted), this, now);
        }

        return chosen;
    }

    public void ApplyDamage(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;

        float landed = Combatants.ResolveIncoming(amount, source, this);
        _health.Drain(landed);

        // Remembered only by this minion, and only for a few seconds.
        if (source is not null) _attention.Record(source.CombatId, landed, Now);

        if (!IsAlive) GD.Print($"[combat] {CombatName} destroyed by {label}");
    }

    public void Heal(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive) return;
        Combatants.ResolveHealing(amount, source, this);
    }

    private void UpdateLabel()
    {
        if (_label is null) return;
        _label.Text = IsAlive ? $"{Title}\n{Mathf.RoundToInt(Health)}" : "";
    }
}
