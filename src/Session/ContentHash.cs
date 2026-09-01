using System.Text;
using Wipebound.Combat;

namespace Wipebound.Session;

/// <summary>
/// A stable fingerprint of the balance data a run was played against.
///
/// A four minute clear on a boss with 4000 health is not the same achievement as
/// one on a boss with 400, so a ladder that mixes them is measuring nothing. The
/// hash goes in the run record and the backend refuses runs whose content it does
/// not recognise.
///
/// Deliberately covers the numbers that change difficulty and nothing else -- not
/// colours, not display names -- so a cosmetic edit does not invalidate a season.
/// </summary>
public static class ContentHash
{
    public static string Current { get; } = Compute();

    private static string Compute()
    {
        var builder = new StringBuilder();

        foreach (BossPhase phase in DefaultEncounter.Build())
        {
            if (phase is null) continue;
            builder.Append(phase.EntersAtHealthPercent).Append(';').Append(phase.RecoverySeconds).Append(';');

            foreach (Ability ability in phase.Abilities) Append(builder, ability);
        }

        foreach (Ability ability in PlayerKit.Build()) Append(builder, ability);

        return Fnv1a(builder.ToString());
    }

    private static void Append(StringBuilder builder, Ability ability)
    {
        if (ability is null) return;

        builder.Append(ability.Id).Append(':')
               .Append((int)ability.Shape).Append(':')
               .Append((int)ability.Origin).Append(':')
               .Append(ability.Radius).Append(':')
               .Append(ability.InnerRadius).Append(':')
               .Append(ability.ConeAngleDegrees).Append(':')
               .Append(ability.RectHalfWidth).Append(':')
               .Append(ability.CastSeconds).Append(':')
               .Append(ability.Cooldown).Append(':')
               .Append(ability.ManaCost).Append(':')
               .Append(ability.Range).Append(':')
               .Append(ability.Effects.Count).Append('|');
    }

    /// FNV-1a: short, stable across runs and platforms, and not a security boundary
    /// -- nobody gains anything by colliding it, the backend allow-lists known values.
    private static string Fnv1a(string text)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offset;
        foreach (char character in text)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash.ToString("x16");
    }
}
