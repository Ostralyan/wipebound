using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Validation for input that arrives from a client.
///
/// There are exactly TWO untrusted channels into this server, and it is worth
/// naming them together because for a long time only one of them was treated as
/// untrusted:
///
///   1. CommandRouter.Submit -- explicit requests. Always looked like a door.
///   2. Hero.NetPosition via MoveSync -- the owning client writes its own
///      position every tick. This is just as much an input channel, and it did
///      not have equivalent validation. A client publishing NaN once poisoned
///      ServerPosition permanently, after which every telegraph comparison
///      returned false and the hero could not be hit by anything, ever.
///
/// Everything either channel accepts passes through here first.
/// </summary>
public static class Untrusted
{
    /// <summary>
    /// How much faster than nominal a client is allowed to appear to move, to
    /// absorb the window where a status change has not replicated yet.
    ///
    /// Note there is no additive epsilon. There used to be a "+0.05f" here that
    /// looked harmless and was applied EVERY PHYSICS TICK -- three extra metres
    /// per second at 60Hz, which let a nominal 7 m/s hero sustain 13.5 m/s.
    /// Tolerances on a per-tick budget have to be multiplicative or they quietly
    /// become a speed allowance.
    /// </summary>
    public const float SpeedTolerance = 1.25f;

    /// Metres of overreach charged for a single non-finite claim.
    public const float GarbageClaimPenalty = 100f;

    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static bool IsFinite(Vector3 value)
        => IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

    /// <summary>
    /// Move the server's validated position toward what the client claims, at no
    /// more than a legal pace. Garbage claims are ignored outright rather than
    /// clamped, because there is no sensible direction toward NaN.
    /// </summary>
    public static Vector3 AdvanceValidatedPosition(Vector3 validated, Vector3 claimed,
                                                   float maxSpeed, float delta, float arenaRadius)
        => AdvanceValidatedPosition(validated, claimed, maxSpeed, delta, arenaRadius, out _);

    /// <summary>
    /// As above, and reports how far the claim exceeded what was legal.
    ///
    /// Clamping a claim silently is the right thing for gameplay and the wrong
    /// thing for a leaderboard. A run is only worth ranking if you can say whether
    /// it was played, and overreach is the measurement: a normal client accumulates
    /// essentially none of it, while anything moving faster than it should leaves a
    /// trail proportional to how much faster.
    ///
    /// This measures rather than punishes on purpose. The decision of what amount
    /// disqualifies a run belongs with whoever runs the ladder, not in here.
    /// </summary>
    public static Vector3 AdvanceValidatedPosition(Vector3 validated, Vector3 claimed,
                                                   float maxSpeed, float delta, float arenaRadius,
                                                   out float overreach)
    {
        overreach = 0f;

        if (!IsFinite(claimed) || !IsFinite(delta) || delta <= 0f)
        {
            // Garbage is not "slightly too fast", it is a deliberate attempt to
            // poison the server's copy. Weight it so it cannot hide in the noise.
            if (!IsFinite(claimed)) overreach = GarbageClaimPenalty;
            return validated;
        }

        // Nothing legitimate is ever outside the arena, and allowing it would let a
        // client sit at 1e30 and drag its own validated position outward forever.
        Vector3 target = new(claimed.X, 0f, claimed.Z);
        if (arenaRadius > 0f && target.Length() > arenaRadius)
            target = target.Normalized() * arenaRadius;

        float budget = Mathf.Max(0f, maxSpeed) * SpeedTolerance * delta;
        Vector3 offset = target - validated;
        float distance = offset.Length();

        if (distance <= budget || distance <= 0f) return target;

        overreach = distance - budget;
        return validated + offset / distance * budget;
    }
}
