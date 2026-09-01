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
///      position every tick. Just as much an input channel, and it did not have
///      equivalent validation. A client publishing NaN once poisoned the server's
///      copy permanently, after which every telegraph comparison returned false.
/// </summary>
public static class Untrusted
{
    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static bool IsFinite(Vector3 value)
        => IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
}

/// <summary>
/// The server's own copy of where a client-authoritative body is, and the budget
/// that copy is allowed to move on.
///
/// THE ALLOWANCE ACCRUES, and that is the whole design. Position claims do not
/// arrive every physics frame -- MoveSync sends at 20Hz while physics runs at 60 --
/// so a claim represents about three frames of travel. Charging it against ONE
/// frame's worth of movement made every honest player look like a cheat: a client
/// simply walking was billed a hundred metres of overreach in thirteen seconds,
/// which with a ladder that rejects any overreach at all would have disqualified
/// every real run.
///
/// So movement is billed against time rather than against ticks. The allowance
/// fills at the legal speed and is spent by actual travel, which makes bursty
/// replication free and sustained speed still impossible.
/// </summary>
public sealed class MovementValidator
{
    /// How much faster than nominal a client may appear to move, to absorb the
    /// window where a status change has not replicated yet. Multiplicative, never
    /// additive: an additive per-tick tolerance quietly becomes a speed allowance.
    public const float SpeedTolerance = 1.25f;

    /// How much unspent allowance can be banked, in seconds of travel. Enough to
    /// ride out several dropped updates, far too little to bank a teleport.
    public const float BurstSeconds = 0.35f;

    /// Metres of overreach charged for a single non-finite claim. Garbage is not
    /// "slightly too fast", it is an attempt to poison this copy, and it should not
    /// be able to hide in the noise.
    public const float GarbageClaimPenalty = 100f;

    public float ArenaRadius { get; set; } = 46f;

    public Vector3 Validated { get; private set; }
    public float Allowance { get; private set; }

    /// <summary>The server placing the body itself -- a spawn, a respawn, a knockback.</summary>
    public void Reset(Vector3 at)
    {
        Validated = new Vector3(at.X, 0f, at.Z);
        Allowance = 0f;
    }

    /// <summary>
    /// Take one claim. Returns how far it exceeded what was legal, which is zero
    /// for anything an honest client can produce.
    /// </summary>
    public float Accept(Vector3 claimed, float maxSpeed, float delta) => Move(claimed, maxSpeed, delta, bill: true);

    /// <summary>
    /// Track a claim without billing for it, for windows where the SERVER moved
    /// the body -- a respawn, a knockback -- and the client is reconciling.
    ///
    /// Freezing instead of following was subtly wrong and expensive: the validator
    /// stood still while the client legitimately walked, and the entire gap was
    /// then billed the instant the window closed. A one second window at seven
    /// metres a second charged nearly seven metres, every death.
    ///
    /// It still only moves at a legal pace, so a knockback cannot be spent as a
    /// free teleport.
    /// </summary>
    public void Follow(Vector3 claimed, float maxSpeed, float delta) => Move(claimed, maxSpeed, delta, bill: false);

    private float Move(Vector3 claimed, float maxSpeed, float delta, bool bill)
    {
        if (!Untrusted.IsFinite(delta) || delta <= 0f) return 0f;

        if (!Untrusted.IsFinite(claimed)) return bill ? GarbageClaimPenalty : 0f;

        float rate = Mathf.Max(0f, maxSpeed) * SpeedTolerance;
        Allowance = Mathf.Min(Allowance + rate * delta, rate * BurstSeconds);

        // Nothing legitimate is ever outside the arena, and allowing it would let a
        // client sit at 1e30 and drag this copy outward forever.
        Vector3 target = new(claimed.X, 0f, claimed.Z);
        if (ArenaRadius > 0f && target.Length() > ArenaRadius)
            target = target.Normalized() * ArenaRadius;

        Vector3 offset = target - Validated;
        float distance = offset.Length();
        if (distance <= 0f) return 0f;

        if (distance <= Allowance)
        {
            Allowance -= distance;
            Validated = target;
            return 0f;
        }

        Validated += offset / distance * Allowance;
        float overreach = distance - Allowance;
        Allowance = 0f;
        return bill ? overreach : 0f;
    }
}
