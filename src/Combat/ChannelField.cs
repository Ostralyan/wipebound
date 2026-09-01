using System.Collections.Generic;
using Godot;

namespace Wipebound.Combat;

/// <summary>
/// A cast that keeps happening, and slowly turns while it does.
///
/// A normal cast is a promise about one moment: it telegraphs, then it resolves
/// once. A channel is a promise about an interval -- it resolves over and over
/// on a tick, and the direction it points is a function of how long it has been
/// running. The dodge changes shape accordingly: you do not step out of a circle
/// once, you keep moving ahead of a sweep, or you get behind it.
///
/// Facing is COMPUTED from elapsed time rather than accumulated per tick. An
/// accumulator would drift apart from what a client drew, and would speed up or
/// slow down with the server's frame rate.
/// </summary>
public sealed class ChannelInstance
{
    public long Id;
    public Ability Ability;
    public ICombatant Owner;

    /// Flat and normalised, where the sweep began.
    public Vector3 StartDirection;

    /// Radians per second, signed. The sign is which way the sweep goes.
    public float RotationRate;

    public double StartAt;
    public double EndsAt;
    public double NextTickAt;

    public bool Cancelled;

    public Vector3 DirectionAt(double now)
        => StartDirection.Rotated(Vector3.Up, RotationRate * (float)(now - StartAt));
}

/// <summary>
/// Live channels, with the same mark-and-sweep discipline as casts and hazards:
/// a tick runs effects, effects can kill, and death reaches back into the world.
/// </summary>
public sealed class ChannelField
{
    private static readonly List<ChannelInstance> None = new();

    private readonly List<ChannelInstance> _live = new();

    public int Count => _live.Count;

    public void Add(ChannelInstance channel)
    {
        if (channel is not null) _live.Add(channel);
    }

    public void Clear() => _live.Clear();

    public bool IsChannelling(ICombatant owner)
    {
        foreach (ChannelInstance channel in _live)
            if (!channel.Cancelled && ReferenceEquals(channel.Owner, owner)) return true;

        return false;
    }

    /// <summary>Stops what this caster was channelling. Returns what it stopped, for the view.</summary>
    public List<ChannelInstance> CancelFor(ICombatant owner)
    {
        List<ChannelInstance> stopped = null;

        foreach (ChannelInstance channel in _live)
        {
            if (channel.Cancelled || !ReferenceEquals(channel.Owner, owner)) continue;
            channel.Cancelled = true;
            (stopped ??= new List<ChannelInstance>()).Add(channel);
        }

        return stopped ?? None;
    }

    /// <summary>
    /// Drop what has finished or been interrupted, and return what is due to fire
    /// now. Effects run on the RESULT, after this returns.
    /// </summary>
    public List<ChannelInstance> Advance(double now, List<ChannelInstance> finished)
    {
        List<ChannelInstance> due = null;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            ChannelInstance channel = _live[i];

            if (channel.Cancelled || now >= channel.EndsAt)
            {
                _live.RemoveAt(i);
                finished?.Add(channel);
                continue;
            }

            if (now < channel.NextTickAt) continue;

            // Interval floored so a zero cannot spin the tick loop forever.
            channel.NextTickAt = now + Mathf.Max(0.02f, channel.Ability.ChannelTickInterval);
            (due ??= new List<ChannelInstance>()).Add(channel);
        }

        return due ?? None;
    }
}
