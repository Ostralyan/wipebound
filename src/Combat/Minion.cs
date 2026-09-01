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
        ICombatant prey = ChooseTarget();
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

    /// <summary>Nearest living player. Becomes a threat lookup once threat exists.</summary>
    private ICombatant ChooseTarget()
    {
        List<ICombatant> prey = Combatants.Living(this, this, TargetFilter.Enemies);
        return prey.Count == 0 ? null : Combatants.ByDistance(prey, GlobalPosition, nearest: true);
    }

    public void ApplyDamage(float amount, ICombatant source, string label)
    {
        if (!IsServer || !IsAlive || amount <= 0f) return;

        _health.Drain(Combatants.ResolveIncoming(amount, source, this));
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
