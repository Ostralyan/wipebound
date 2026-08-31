using Godot;
using Wipebound.Net;

namespace Wipebound.World;

/// <summary>
/// Placeholder for the boss slot. It exists to make the security model visible:
/// its health only ever changes inside <see cref="ApplyDamage"/>, which only runs
/// on the server, and it reaches clients through a synchronizer they cannot write.
/// </summary>
public partial class TrainingDummy : Node3D
{
    public const string GroupName = "training_dummy";

    [Export] public float MaxHealth = 400f;
    [Export] public float RespawnDelay = 4f;

    // Replicated by StatsSync. Authority: the server.
    [Export] public float Health { get; set; } = 400f;

    public bool IsAlive => Health > 0f;

    private Label3D _label;
    private MeshInstance3D _body;
    private double _respawnAt;

    public override void _Ready()
    {
        AddToGroup(GroupName);
        _label = GetNode<Label3D>("HealthLabel");
        _body = GetNode<MeshInstance3D>("Body");

        if (NetworkManager.Instance.IsServer)
            Health = MaxHealth;
    }

    public override void _Process(double delta)
    {
        _label.Text = IsAlive ? $"Dummy  {Mathf.RoundToInt(Health)}/{Mathf.RoundToInt(MaxHealth)}" : "Dummy  down";
        _body.Visible = IsAlive;

        if (!NetworkManager.Instance.IsServer || IsAlive) return;

        double now = Time.GetTicksMsec() / 1000.0;
        if (now >= _respawnAt) Health = MaxHealth;
    }

    /// <summary>
    /// Server only, by construction. Nothing on a client can reach this, and no
    /// client-supplied number reaches it either -- callers compute damage from
    /// server-side stats.
    /// </summary>
    public void ApplyDamage(float amount)
    {
        if (!NetworkManager.Instance.IsServer) return;
        if (!IsAlive) return;

        Health = Mathf.Max(0f, Health - amount);
        if (!IsAlive) _respawnAt = Time.GetTicksMsec() / 1000.0 + RespawnDelay;
    }
}
