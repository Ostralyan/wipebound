using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Put something in the air, along whatever direction the ability is aimed.
///
/// Deliberately does NOT read context.Targets. A projectile has no target: it
/// travels, and it hits whoever is standing where it goes. That is the whole
/// difference between this and every other effect in the game, and it is why a
/// sweeping channel is dodged by moving rather than by being someone else.
/// </summary>
[GlobalClass]
public partial class FireProjectileEffect : AbilityEffect
{
    [Export] public Projectile Definition { get; set; }

    /// More than one fans out around the aim, so a channel can be a spray.
    [Export] public int Count { get; set; } = 1;

    [Export] public float SpreadDegrees { get; set; }

    public override void Resolve(EffectContext context)
    {
        if (Definition is null || CombatDirector.Instance is null) return;

        Vector3 aim = context.AimDirection;
        if (aim.LengthSquared() < 0.0001f) return;

        int count = Mathf.Max(1, Count);
        float spread = Mathf.DegToRad(SpreadDegrees);

        for (int i = 0; i < count; i++)
        {
            // Centred fan: one projectile goes straight down the aim, and the
            // rest sit symmetrically either side of it.
            float offset = count == 1 ? 0f : spread * ((float)i / (count - 1) - 0.5f);
            Vector3 direction = aim.Rotated(Vector3.Up, offset);

            CombatDirector.Instance.FireProjectile(context.Caster, Definition, direction, context.Now);
        }
    }

    public override string Describe(EffectContext context)
        => $"Fire {Mathf.Max(1, Count)}x {Definition?.Id ?? "?"}";
}
