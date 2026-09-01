using System;

namespace Wipebound.Combat;

/// <summary>
/// When each ability slot next becomes usable, as absolute server-clock times.
///
/// Absolute rather than counting down, for the same reason casts are: a countdown
/// decremented by delta drifts and couples ability timing to frame rate, and a
/// client cannot be handed a remaining duration and be expected to agree about
/// when it ends.
///
/// Extracted from Hero so it is reachable by the self-test. Every slot access is
/// bounds-checked, because slot indices originate in client payloads.
/// </summary>
public sealed class CooldownSet
{
    private double[] _readyAt = Array.Empty<double>();

    public int Count => _readyAt.Length;

    public void Resize(int count) => _readyAt = new double[Math.Max(0, count)];

    private bool InRange(int slot) => slot >= 0 && slot < _readyAt.Length;

    public bool IsReady(int slot, double now) => InRange(slot) && now >= _readyAt[slot];

    public double ReadyAt(int slot) => InRange(slot) ? _readyAt[slot] : 0.0;

    public void SetReadyAt(int slot, double readyAt)
    {
        if (InRange(slot)) _readyAt[slot] = readyAt;
    }

    public void Start(int slot, double now, float duration)
    {
        if (InRange(slot)) _readyAt[slot] = now + duration;
    }

    /// <summary>How much of the cooldown remains, 1 to 0, for a progress bar.</summary>
    public float Fraction(int slot, double now, float duration)
    {
        if (!InRange(slot) || duration <= 0f) return 0f;
        double remaining = (_readyAt[slot] - now) / duration;
        return (float)Math.Clamp(remaining, 0.0, 1.0);
    }

    public void Clear() => Array.Clear(_readyAt);
}
