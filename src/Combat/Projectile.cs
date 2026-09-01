using Godot;

namespace Wipebound.Combat;

/// <summary>
/// A thing in flight, described rather than simulated.
///
/// Motion is a FUNCTION OF TIME, not an accumulated step: position is always
/// origin + direction * speed * (now - spawnedAt). That is the whole reason
/// projectiles are affordable here. The server broadcasts one spawn packet and
/// every client computes the same path from the same shared clock, so a stream
/// of forty projectiles costs forty packets rather than forty per frame, and a
/// client that joined late or dropped a frame is never out of step.
///
/// It also means there is nothing to cheat with. Clients render; the server is
/// the only thing that decides a projectile hit anybody.
/// </summary>
[GlobalClass]
public partial class Projectile : Resource
{
    [Export] public string Id { get; set; } = "projectile";
    [Export] public float Speed { get; set; } = 26f;

    /// Fat enough to be dodged by moving rather than by pixel-hunting.
    [Export] public float Radius { get; set; } = 0.9f;

    /// How far it flies before giving up. Kept under the arena so a stray one
    /// does not live forever.
    [Export] public float Range { get; set; } = 60f;

    [Export] public float Damage { get; set; } = 6f;
    [Export] public TargetFilter Affects { get; set; } = TargetFilter.Enemies;
    [Export] public Color Tint { get; set; } = new(1f, 0.55f, 0.2f);

    public double Lifetime => Speed > 0.01f ? Range / Speed : 0.0;
}

/// <summary>One projectile actually in the air, server-side.</summary>
public sealed class ProjectileInstance
{
    public long Id;
    public Projectile Definition;
    public ICombatant Owner;
    public Vector3 Origin;

    /// Flat and normalised. Set once at spawn; a projectile does not steer.
    public Vector3 Direction;

    public double SpawnedAt;
    public double ExpiresAt;

    /// Marked on hit, swept after the walk. Never removed mid-iteration.
    public bool Spent;

    public Vector3 PositionAt(double now)
        => Origin + Direction * (Definition.Speed * (float)(now - SpawnedAt));
}

public readonly struct ProjectileHit
{
    public readonly ProjectileInstance Projectile;
    public readonly ICombatant Target;

    public ProjectileHit(ProjectileInstance projectile, ICombatant target)
    {
        Projectile = projectile;
        Target = target;
    }
}
