using System;
using System.Collections.Generic;
using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Everything currently in the air, and the same iteration discipline as
/// hazards and casts.
///
/// A projectile hitting somebody runs damage, damage can kill, and death reaches
/// back into the world. So Advance() only MARKS what it hit and hands the list
/// back; the caller applies consequences after the walk is over. This class has
/// no idea what damage is, which is what keeps that guarantee cheap to hold.
/// </summary>
public sealed class ProjectileField
{
    private static readonly List<ProjectileHit> None = new();

    private readonly List<ProjectileInstance> _live = new();

    public int Count => _live.Count;
    public IReadOnlyList<ProjectileInstance> Live => _live;

    public void Add(ProjectileInstance projectile)
    {
        if (projectile is not null) _live.Add(projectile);
    }

    public void Clear() => _live.Clear();

    /// <summary>
    /// Fly everything forward, drop what has expired, and report the first thing
    /// each projectile touched.
    ///
    /// One hit per projectile: it is spent on contact rather than sweeping through
    /// a line of people, which is what makes a wall of bodies a real defence.
    /// </summary>
    public List<ProjectileHit> Advance(double now, Func<ProjectileInstance, List<ICombatant>> candidatesFor)
    {
        List<ProjectileHit> hits = null;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            ProjectileInstance projectile = _live[i];

            if (projectile.Spent || now >= projectile.ExpiresAt)
            {
                _live.RemoveAt(i);
                continue;
            }

            Vector3 at = projectile.PositionAt(now);
            float reach = projectile.Definition.Radius;

            foreach (ICombatant candidate in candidatesFor(projectile))
            {
                if (candidate is null || !candidate.IsAlive) continue;

                // Flat distance. Heroes, minions and bosses sit at different
                // heights and a projectile that missed because of a hitbox nobody
                // can see is indistinguishable from a bug.
                Vector3 gap = candidate.CombatPosition - at;
                gap.Y = 0f;
                if (gap.LengthSquared() > reach * reach) continue;

                projectile.Spent = true;
                (hits ??= new List<ProjectileHit>()).Add(new ProjectileHit(projectile, candidate));
                break;
            }
        }

        return hits ?? None;
    }

    /// <summary>Stop everything one caster has in the air, for interrupts and resets.</summary>
    public void CancelFor(ICombatant owner)
    {
        foreach (ProjectileInstance projectile in _live)
            if (ReferenceEquals(projectile.Owner, owner))
                projectile.Spent = true;
    }
}
