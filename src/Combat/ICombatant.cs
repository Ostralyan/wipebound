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

    /// Everyone on your side except you. The whole of a support role's dependency
    /// lives in this value: a healer who cannot reach themselves needs somebody.
    OtherAllies = 3,
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

    /// <summary>
    /// What this combatant spends to act, or null for anything that does not.
    ///
    /// Nullable rather than an empty pool: a boss with a zero-length mana bar
    /// and a boss with no mana bar look identical to a reader, and only one of
    /// them is true.
    /// </summary>
    ResourcePool ResourcePool => null;

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

    /// <summary>
    /// Whether this combatant's position can safely be read from its node.
    ///
    /// IsInstanceValid alone is not enough, and the difference is not theoretical.
    /// A replicated node stays valid for a while after the engine takes it out of
    /// the tree -- which is what a server-first disconnect does to every hero at
    /// once -- and reading GlobalPosition in that window is an error with our own
    /// name on the stack.
    ///
    /// Named so the check is one idea in one place. It was found in the camera,
    /// fixed there, and was in three other places doing exactly the same thing.
    /// </summary>
    public static bool Placed(ICombatant combatant)
        => combatant?.Node is not null
           && GodotObject.IsInstanceValid(combatant.Node)
           && combatant.Node.IsInsideTree();

    public static bool Matches(ICombatant candidate, ICombatant caster, TargetFilter filter) => filter switch
    {
        TargetFilter.Enemies => candidate.Team != caster.Team,
        TargetFilter.Allies => candidate.Team == caster.Team,
        TargetFilter.OtherAllies => candidate.Team == caster.Team && !ReferenceEquals(candidate, caster),
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
    public static float ResolveIncoming(float amount, ICombatant source, ICombatant target, string ability = "")
    {
        float scaled = ScaleDamage(amount, source, target);
        double when = Net.NetClock.Instance?.ServerTime ?? 0.0;
        float landed = target?.Status is null ? scaled : target.Status.AbsorbDamage(scaled, when);

        // Attribution belongs here and nowhere else. This is already the one place
        // that knows the final number, so recording it costs nothing and no future
        // effect can forget to.
        if (source?.Contribution is not null) source.Contribution.DamageDone += landed;

        if (target?.Contribution is not null)
        {
            target.Contribution.DamageTaken += landed;
            target.Contribution.DamageAbsorbed += scaled - landed;
        }

        // Logged here for the same reason attribution is: this is the one place
        // that knows the final number, so nothing else has to remember to report
        // and nothing can report a number that was never applied.
        //
        // Overkill is computed before the caller drains, which is the only moment
        // the remaining health is still the health this blow was aimed at.
        Session.RunRecorder.Instance?.Log.Damage(
            Net.NetClock.Instance?.ServerTime ?? 0.0, source, target, ability,
            landed, scaled - landed,
            Mathf.Max(0f, landed - (target?.HealthPool?.Current ?? 0f)));

        return landed;
    }

    /// <summary>
    /// The healing counterpart, so credit works the same way in both directions and
    /// there is somewhere obvious to put healing modifiers later.
    /// </summary>
    public static float ResolveHealing(float amount, ICombatant source, ICombatant target, string ability = "")
    {
        if (amount <= 0f || target?.HealthPool is null) return 0f;

        // Credit what actually landed, not what was requested. Overhealing a
        // full-health ally is not a contribution.
        float landed = target.HealthPool.Restore(amount);
        if (source?.Contribution is not null) source.Contribution.HealingDone += landed;

        // Overhealing is recorded rather than discarded. It is the difference
        // between healing done and healing that mattered, and without it an HPS
        // number rewards spamming a full-health ally.
        Session.RunRecorder.Instance?.Log.Heal(
            Net.NetClock.Instance?.ServerTime ?? 0.0, source, target, ability, landed, amount - landed);

        return landed;
    }

    /// <summary>Find a combatant by the identity that travels over the wire.</summary>
    public static ICombatant ById(Node context, int combatId)
    {
        foreach (Node node in context.GetTree().GetNodesInGroup(GroupName))
            if (node is ICombatant combatant && combatant.CombatId == combatId)
                return combatant;

        return null;
    }

    /// <summary>
    /// Whatever the cursor is over, for abilities aimed at a person rather than a
    /// place.
    ///
    /// USES THE VISIBLE POSITION, NOT CombatPosition, and the distinction is the
    /// whole correctness of this function. CombatPosition is the server's validated
    /// copy and it is only ever advanced on the server -- on a client it sits at
    /// the spawn point forever. Picking with it meant the ring appeared where
    /// people STARTED rather than where they are, and a Verdant could not target a
    /// moving ally at all.
    ///
    /// So: the client picks by what the player can see, and the server validates
    /// that choice against its own copy. Neither is a substitute for the other.
    ///
    /// Nearest-to-the-ground-point rather than a physics pick, deliberately: the
    /// boss has no collision body at all, and a pick radius degrades into "roughly
    /// where you meant" instead of failing outright.
    /// </summary>
    public static ICombatant UnderCursor(Node context, Vector3 groundPoint, ICombatant caster,
                                         TargetFilter filter, float pickRadius = 2.5f)
    {
        ICombatant best = null;
        float bestDistance = pickRadius * pickRadius;

        foreach (Node node in context.GetTree().GetNodesInGroup(GroupName))
        {
            if (node is not ICombatant candidate || !candidate.IsAlive) continue;
            if (caster is not null && !Matches(candidate, caster, filter)) continue;

            if (!Placed(candidate)) continue;

            Vector3 offset = candidate.Node.GlobalPosition - groundPoint;
            offset.Y = 0f;

            float distance = offset.LengthSquared();
            if (distance > bestDistance) continue;

            best = candidate;
            bestDistance = distance;
        }

        return best;
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
