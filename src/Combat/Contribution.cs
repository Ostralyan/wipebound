namespace Wipebound.Combat;

/// <summary>
/// What one combatant has actually done this attempt.
///
/// Recorded at the single chokepoint every damage and heal already passes
/// through, which is the only reason this is cheap: nothing has to remember to
/// report, because nothing computes a final number anywhere else.
///
/// It answers "why did we wipe" with something better than opinion -- and threat,
/// when it arrives, needs exactly these numbers.
/// </summary>
public sealed class Contribution
{
    public float DamageDone { get; set; }
    public float HealingDone { get; set; }
    public float DamageTaken { get; set; }

    /// Damage a shield ate. Kept apart from DamageTaken so a tank's mitigation is
    /// visible rather than looking like damage that never happened.
    public float DamageAbsorbed { get; set; }

    public void Clear()
    {
        DamageDone = 0f;
        HealingDone = 0f;
        DamageTaken = 0f;
        DamageAbsorbed = 0f;
    }
}
