using Godot;
using System.Collections.Generic;

namespace Wipebound.Combat;

/// <summary>
/// The patches of ground currently dangerous.
///
/// Expiry and tick pacing live here rather than in the director so they can be
/// tested without a scene: a hazard that outlives an encounter reset is a bug you
/// want caught by a test, not by a raid that got burned by fire from the attempt
/// before.
/// </summary>
public sealed class HazardField
{
    private static readonly List<HazardInstance> None = new();

    private readonly List<HazardInstance> _live = new();

    public int Count => _live.Count;
    public IReadOnlyList<HazardInstance> Live => _live;

    public void Add(HazardInstance hazard)
    {
        if (hazard is not null) _live.Add(hazard);
    }

    public void Clear() => _live.Clear();

    /// <summary>
    /// Drop what has burnt out, and return what is due to hurt somebody now,
    /// advancing those timers. Effects run on the RESULT, after this returns, so
    /// anything they spawn cannot disturb the walk.
    /// </summary>
    public List<HazardInstance> Advance(double now)
    {
        List<HazardInstance> due = null;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            HazardInstance hazard = _live[i];

            if (now >= hazard.ExpiresAt)
            {
                _live.RemoveAt(i);
                continue;
            }

            if (now < hazard.NextTickAt) continue;

            hazard.NextTickAt = now + Mathf.Max(0.05f, hazard.Definition.TickInterval);
            (due ??= new List<HazardInstance>()).Add(hazard);
        }

        return due ?? None;
    }
}
