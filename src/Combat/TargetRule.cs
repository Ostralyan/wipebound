using Godot;
using System.Collections.Generic;

namespace Wipebound.Combat;

/// <summary>How one NPC decides who it is coming for.</summary>
public enum TargetRule
{
    /// Whoever is closest. Adds become a spatial problem and proximity costs you.
    /// Simple and readable, but it visibly flip-flops when players cross paths.
    Nearest = 0,

    /// Picks one player and commits. It is YOUR add now -- run, or kill it, or ask
    /// for help. This is what a threat table was ever standing in for, without the
    /// designated victim: it rotates on a timer, and nobody can volunteer to hold it.
    Fixate = 1,

    /// Attention with a short memory: whoever is hurting it most right now.
    ///
    /// This is a threat table, and worth calling one. What makes it not a tank
    /// mechanic is that it lives on a single NPC and decays fast, so it cannot be
    /// held -- it tracks the current biggest problem rather than accumulating into
    /// a job. It buys a real burst-versus-greed tension and nothing else.
    HighestRecentDamage = 2,

    /// Goes for whoever is already hurt. Vicious, and it makes the group protect
    /// somebody rather than each dodge alone.
    LowestHealth = 3,
}

/// <summary>
/// Target selection, extracted from Minion so the rules can be tested without a
/// scene. Every branch here is a decision about how a fight feels.
/// </summary>
public static class TargetSelection
{
    /// How quickly a recent hit loses weight against a newer one.
    public const float AttentionHalfLife = 4f;

    /// <summary>
    /// How long since your last hit before a minion forgets you entirely.
    ///
    /// Decay alone was not a memory. Scaling every score by the same factor never
    /// changes their order, so a lone attacker stayed the target until their score
    /// happened to fall under a cull threshold -- about thirty seconds, not the
    /// four this claimed. Forgetting is now a function of TIME SINCE YOU LAST HIT
    /// IT, which is what the word meant.
    /// </summary>
    public const float AttentionMemory = 4f;

    public static ICombatant Choose(
        TargetRule rule,
        IReadOnlyList<ICombatant> candidates,
        Vector3 from,
        ICombatant keeping = null,
        IReadOnlyDictionary<int, float> attention = null)
    {
        if (candidates.Count == 0) return null;

        return rule switch
        {
            TargetRule.Fixate => Fixated(candidates, keeping, from),
            TargetRule.HighestRecentDamage => MostRecentlyHurtBy(candidates, attention, from),
            TargetRule.LowestHealth => MostHurt(candidates),
            _ => Nearest(candidates, from),
        };
    }

    private static ICombatant Nearest(IReadOnlyList<ICombatant> candidates, Vector3 from)
        => Combatants.ByDistance(candidates, from, nearest: true);

    /// <summary>
    /// Keep the current victim while it is still available. Otherwise pick someone
    /// nobody else is already hunting, so three adds spawning together split up
    /// instead of dogpiling one player.
    ///
    /// The Hunted status is the coordination channel: it already replicates, it
    /// already shows on nameplates, and it means the minions never need to know
    /// about each other.
    /// </summary>
    private static ICombatant Fixated(IReadOnlyList<ICombatant> candidates, ICombatant keeping, Vector3 from)
    {
        if (keeping is not null && keeping.IsAlive)
        {
            foreach (ICombatant candidate in candidates)
                if (ReferenceEquals(candidate, keeping)) return keeping;
        }

        var unhunted = new List<ICombatant>();
        foreach (ICombatant candidate in candidates)
            if (!candidate.Status.Has(StatusLibrary.Hunted))
                unhunted.Add(candidate);

        return Nearest(unhunted.Count > 0 ? unhunted : candidates, from);
    }

    private static ICombatant MostRecentlyHurtBy(
        IReadOnlyList<ICombatant> candidates,
        IReadOnlyDictionary<int, float> attention,
        Vector3 from)
    {
        if (attention is null || attention.Count == 0) return Nearest(candidates, from);

        ICombatant best = null;
        float bestScore = 0f;

        foreach (ICombatant candidate in candidates)
        {
            if (!attention.TryGetValue(candidate.CombatId, out float score)) continue;
            if (best is not null && score <= bestScore) continue;

            best = candidate;
            bestScore = score;
        }

        // Nobody has hit it yet, so it has no opinion and falls back to proximity.
        return best ?? Nearest(candidates, from);
    }

    private static ICombatant MostHurt(IReadOnlyList<ICombatant> candidates)
    {
        ICombatant best = null;
        float lowest = float.MaxValue;

        foreach (ICombatant candidate in candidates)
        {
            float fraction = candidate.HealthPool.Fraction;
            if (best is not null && fraction >= lowest) continue;

            best = candidate;
            lowest = fraction;
        }

        return best;
    }

}

/// <summary>
/// Who has been hurting one NPC lately.
///
/// Local to a single minion and genuinely short-lived, which is the whole
/// difference between attention and a threat table somebody can hold: there is no
/// way to accumulate a claim on it, because not hitting it for four seconds
/// removes you from it completely.
/// </summary>
public sealed class AttentionTable
{
    private readonly Dictionary<int, float> _score = new();
    private readonly Dictionary<int, double> _lastHit = new();

    public IReadOnlyDictionary<int, float> Scores => _score;
    public int Count => _score.Count;

    public void Record(int combatId, float amount, double now)
    {
        if (amount <= 0f) return;

        _score[combatId] = _score.GetValueOrDefault(combatId) + amount;
        _lastHit[combatId] = now;
    }

    public void Clear()
    {
        _score.Clear();
        _lastHit.Clear();
    }

    /// <summary>
    /// Age the table. Scores decay so a newer, smaller hit can overtake an older,
    /// larger one; entries nobody has refreshed inside the memory window are
    /// dropped outright.
    /// </summary>
    public void Forget(double now, float delta)
    {
        if (_score.Count == 0) return;

        float factor = Mathf.Exp(-Mathf.Log(2f) * delta / TargetSelection.AttentionHalfLife);
        var stale = new List<int>();

        foreach (int id in _score.Keys)
        {
            if (now - _lastHit.GetValueOrDefault(id) >= TargetSelection.AttentionMemory) stale.Add(id);
            else _score[id] *= factor;
        }

        foreach (int id in stale)
        {
            _score.Remove(id);
            _lastHit.Remove(id);
        }
    }
}
