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
    Team Team { get; }

    /// The position the SERVER believes this combatant occupies.
    Vector3 CombatPosition { get; }

    bool IsAlive { get; }
    ResourcePool HealthPool { get; }

    /// For lifetime checks and rendering. Combatants are always nodes.
    Node3D Node { get; }

    void ApplyDamage(float amount, ICombatant source, string label);
    void Heal(float amount, ICombatant source, string label);

    /// <summary>
    /// Server-driven movement (knockbacks). Implementations that cannot be moved --
    /// bosses, anchored hazards -- simply do nothing.
    /// </summary>
    void Displace(Vector3 destination, float travelSeconds);
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
