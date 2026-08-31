using Godot;

namespace Wipebound.Combat;

/// <summary>
/// A bounded, optionally regenerating quantity: health, mana, energy, rage.
///
/// One class rather than three, because the difference between "mana" and
/// "energy" and "rage" is entirely a matter of maximum, regeneration rate and
/// what refills it -- all data. Health is simply a pool that does not regenerate.
///
/// Plain C#, deliberately. It is not a Node, so it cannot be replicated directly;
/// the owning node exposes [Export] properties that proxy into it, and those are
/// what the synchronizer carries.
/// </summary>
public sealed class ResourcePool
{
    public float Max { get; set; }
    public float Current { get; set; }
    public float RegenPerSecond { get; set; }

    public ResourcePool(float max, float regenPerSecond = 0f)
    {
        Max = max;
        Current = max;
        RegenPerSecond = regenPerSecond;
    }

    public float Fraction => Max > 0f ? Mathf.Clamp(Current / Max, 0f, 1f) : 0f;
    public bool IsEmpty => Current <= 0f;

    public void Fill() => Current = Max;

    public void Tick(float delta)
    {
        if (RegenPerSecond == 0f) return;
        Current = Mathf.Clamp(Current + RegenPerSecond * delta, 0f, Max);
    }

    public bool CanAfford(float amount) => Current >= amount;

    public bool TrySpend(float amount)
    {
        if (!CanAfford(amount)) return false;
        Current -= amount;
        return true;
    }

    /// <summary>Removes up to <paramref name="amount"/>; returns how much actually went.</summary>
    public float Drain(float amount)
    {
        float before = Current;
        Current = Mathf.Max(0f, Current - amount);
        return before - Current;
    }

    /// <summary>Adds up to <paramref name="amount"/>; returns how much actually landed.</summary>
    public float Restore(float amount)
    {
        float before = Current;
        Current = Mathf.Min(Max, Current + amount);
        return Current - before;
    }
}
