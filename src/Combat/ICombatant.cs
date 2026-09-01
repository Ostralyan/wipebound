using Godot;
using System.Collections.Generic;

namespace Wipebound.Combat;

public enum Team
{
    Players = 0,
    Enemies = 1,
}

/// <summary>Who an ability is allowed to touch, relative to whoever cast it.</summary>
public enum TargetFilter
{
    Enemies = 0,
    Allies = 1,
    All = 2,
}

/// <summary>
/// Anything that can be hit.
///
/// This interface is the seam that lets one ability system serve heroes, bosses
/// and whatever gets added later. Before it existed, every effect named Hero
/// directly, which meant a boss mechanic could hurt players and a player ability
/// could hurt nothing -- the two halves of combat shared no code at all.
///
/// Note CombatPosition rather than a plain transform: for a hero that is the
/// server's speed-clamped copy, never the position the client claims. Routing
/// every area test through this property is what stops a modified client from
/// simply asserting it dodged.
/// </summary>
public interface ICombatant
{
    string CombatName { get; }

    /// <summary>
    /// Stable identity across peers, used to tell one caster's statuses from
    /// another's over the wire. Heroes use their peer id. NPCs currently share one,
    /// which is fine while nothing NPC-applied is PerSource.
    /// </summary>
    int CombatId { get; }

    Team Team { get; }

    /// The position the SERVER believes this combatant occupies.
    Vector3 CombatPosition { get; }

    bool IsAlive { get; }
    ResourcePool HealthPool { get; }

    /// Running tally of what this combatant has done this attempt.
    Contribution Contribution { get; }

    /// Timed modifiers currently applied. Server-authoritative, replicated for the
    /// HUD and -- because hero movement is client-authoritative -- so a slowed
    /// client knows it is slowed.
    StatusTracker Status { get; }

    /// For lifetime checks and rendering. Combatants are always nodes.
    Node3D Node { get; }

    void ApplyDamage(float amount, ICombatant source, string label);
    void Heal(float amount, ICombatant source, string label);

    /// <summary>
    /// Server-driven movement (knockbacks). Implementations that cannot be moved --
    /// bosses, anchored hazards -- simply do nothing.
    /// </summary>
    void Displace(Vector3 destination, float travelSeconds);

    /// <summary>
    /// The encounter has restarted. Heroes come back to life here; the boss has
    /// already reset itself by the time this is broadcast.
    /// </summary>
    void OnEncounterReset();
}

public static class Combatants
{
    /// Every combatant joins this group in _Ready, so nothing has to know about
    /// concrete types to find them.
    public const string GroupName = "combatant";

    public static bool Matches(ICombatant candidate, ICombatant caster, TargetFilter filter) => filter switch
    {
        TargetFilter.Enemies => candidate.Team != caster.Team,
        TargetFilter.Allies => candidate.Team == caster.Team,
        _ => true,
    };

    /// <summary>Living combatants the given caster is allowed to affect.</summary>
    public static List<ICombatant> Living(Node context, ICombatant caster, TargetFilter filter)
    {
        var found = new List<ICombatant>();

        foreach (Node node in context.GetTree().GetNodesInGroup(GroupName))
        {
            if (node is not ICombatant combatant) continue;
            if (!combatant.IsAlive) continue;
            if (caster is not null && !Matches(combatant, caster, filter)) continue;
            found.Add(combatant);
        }

        return found;
    }

    /// <summary>
    /// The one place outgoing and incoming modifiers are combined. Both ApplyDamage
    /// implementations call it, so an effect can never forget to apply a
    /// vulnerability or a shield -- it never sees the final number at all.
    /// </summary>
    public static float ScaleDamage(float amount, ICombatant source, ICombatant target)
    {
        float outgoing = source?.Status?.DamageDealtMultiplier ?? 1f;
        float incoming = target?.Status?.DamageTakenMultiplier ?? 1f;
        return amount * outgoing * incoming;
    }

    /// <summary>
    /// The whole incoming-damage pipeline, in the order it has to happen: scale by
    /// both sides' modifiers, then spend shields against what is left. Absorption
    /// after mitigation, because a shield soaks the damage you were actually going
    /// to take. Both ApplyDamage implementations call this, so neither can drift.
    /// </summary>
    public static float ResolveIncoming(float amount, ICombatant source, ICombatant target)
    {
        float scaled = ScaleDamage(amount, source, target);
        float landed = target?.Status is null ? scaled : target.Status.AbsorbDamage(scaled);

        // Attribution belongs here and nowhere else. This is already the one place
        // that knows the final number, so recording it costs nothing and no future
        // effect can forget to.
        if (source?.Contribution is not null) source.Contribution.DamageDone += landed;

        if (target?.Contribution is not null)
        {
            target.Contribution.DamageTaken += landed;
            target.Contribution.DamageAbsorbed += scaled - landed;
        }

        return landed;
    }

    /// <summary>
    /// The healing counterpart, so credit works the same way in both directions and
    /// there is somewhere obvious to put healing modifiers later.
    /// </summary>
    public static float ResolveHealing(float amount, ICombatant source, ICombatant target)
    {
        if (amount <= 0f || target?.HealthPool is null) return 0f;

        // Credit what actually landed, not what was requested. Overhealing a
        // full-health ally is not a contribution.
        float landed = target.HealthPool.Restore(amount);
        if (source?.Contribution is not null) source.Contribution.HealingDone += landed;
        return landed;
    }

    public static ICombatant ByDistance(IReadOnlyList<ICombatant> from, Vector3 origin, bool nearest)
    {
        ICombatant best = null;
        float bestDistance = 0f;

        foreach (ICombatant candidate in from)
        {
            float distance = candidate.CombatPosition.DistanceSquaredTo(origin);
            if (best is null || (nearest ? distance < bestDistance : distance > bestDistance))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }
}
