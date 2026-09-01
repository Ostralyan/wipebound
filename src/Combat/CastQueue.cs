using System;
using System.Collections.Generic;

using Godot;

namespace Wipebound.Combat;

/// <summary>
/// The casts currently in flight, and the iteration discipline that keeps them
/// safe to mutate.
///
/// Resolving a cast runs arbitrary effects, and effects reach back in here: an
/// interrupt cancels a cast from inside the resolution of another one, and a
/// summon could start a new one. Removing during that walk threw an
/// IndexOutOfRange the first time an interrupt ever landed.
///
/// So cancellation MARKS and never removes, the walk is bounded to the casts that
/// existed when it began, and a single sweep afterwards clears what was marked.
/// Lives outside the director as plain C# so this is reachable by tests.
/// </summary>
public sealed class CastQueue
{
    private static readonly List<CastInstance> None = new();

    private readonly List<CastInstance> _pending = new();
    private bool _walking;

    public int Count => _pending.Count;
    public IReadOnlyList<CastInstance> Pending => _pending;

    public void Add(CastInstance cast)
    {
        if (cast is not null) _pending.Add(cast);
    }

    public bool IsCasting(ICombatant caster)
    {
        foreach (CastInstance cast in _pending)
            if (!cast.Cancelled && ReferenceEquals(cast.Caster, caster)) return true;

        return false;
    }

    /// <summary>Stops everything this caster had in flight. Returns what it stopped.</summary>
    public List<CastInstance> CancelFor(ICombatant caster)
    {
        List<CastInstance> stopped = null;

        foreach (CastInstance cast in _pending)
        {
            if (cast.Cancelled || !ReferenceEquals(cast.Caster, caster)) continue;
            cast.Cancelled = true;
            (stopped ??= new List<CastInstance>()).Add(cast);
        }

        Sweep();
        return stopped ?? None;
    }

    public List<CastInstance> CancelAll()
    {
        List<CastInstance> stopped = null;

        foreach (CastInstance cast in _pending)
        {
            if (cast.Cancelled) continue;
            cast.Cancelled = true;
            (stopped ??= new List<CastInstance>()).Add(cast);
        }

        Sweep();
        return stopped ?? None;
    }

    /// <summary>
    /// Resolve everything that has come due. <paramref name="casterValid"/> drops
    /// casts whose caster died or was freed mid-cast -- without it a dead boss keeps
    /// hitting people.
    /// </summary>
    public void Process(double now, Func<CastInstance, bool> casterValid, Action<CastInstance> resolve)
    {
        if (_pending.Count == 0) return;

        // Only the casts that existed when this began. Anything an effect starts
        // waits for the next tick rather than cascading inside this one.
        int existing = _pending.Count;
        _walking = true;

        try
        {
            for (int i = 0; i < existing && i < _pending.Count; i++)
            {
                CastInstance cast = _pending[i];
                if (cast.Cancelled) continue;

                if (casterValid is not null && !casterValid(cast))
                {
                    cast.Cancelled = true;
                    continue;
                }

                if (now < cast.ResolveAt) continue;

                cast.Cancelled = true;
                resolve?.Invoke(cast);
            }
        }
        finally
        {
            _walking = false;
        }

        Sweep();
    }

    /// Removing while the walk is in progress is exactly the bug this class exists
    /// to prevent, so it simply does not happen until the walk is over.
    private void Sweep()
    {
        if (_walking) return;
        _pending.RemoveAll(cast => cast.Cancelled);
    }
}
