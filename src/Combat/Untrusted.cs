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

    /// <summary>
    /// How much unspent allowance can be banked, in seconds of travel.
    ///
    /// This is the size of the gap between position updates that a player is
    /// allowed to have. A claim arriving after a quiet stretch carries all the
    /// movement of that stretch at once, and the bank is what pays for it.
    ///
    /// It is also, exactly, the free instant reposition available to a modified
    /// client: bank the allowance, spend it in one claim, wait, repeat. It buys
    /// no extra average speed -- the refill takes as long as the dodge saved --
    /// so what it costs is a dodge, not a race.
    ///
    /// 0.35s covered gaps up to 0.44s, which is fine on a LAN and not fine at
    /// 300ms with packet loss, where honest players were billed for the bunching
    /// their network did to them. 0.6s covers gaps to 0.75s. The price is that
    /// the free dodge grows from about 3m to about 5m, and that is the trade:
    /// refusing honest players from the ladder is worse than a cheat that still
    /// has to kill the boss.
    /// </summary>
    public const float BurstSeconds = 0.6f;

    /// Metres of overreach charged for a single non-finite claim. Garbage is not
    /// "slightly too fast", it is an attempt to poison this copy, and it should not
    /// be able to hide in the noise.
    public const float GarbageClaimPenalty = 100f;

    public float ArenaRadius { get; set; } = 46f;

    public Vector3 Validated { get; private set; }
    public float Allowance { get; private set; }

    /// The last claim that was actually judged, so that standing discrepancy is
    /// not re-judged every tick. See Move.
    private Vector3 _judged;
    private bool _everJudged;

    /// <summary>The server placing the body itself -- a spawn, a respawn, a knockback.</summary>
    public void Reset(Vector3 at)
    {
        Validated = new Vector3(at.X, 0f, at.Z);
        Allowance = 0f;
        _judged = Validated;
        _everJudged = false;
    }

    /// <summary>
    /// Take one claim. Returns how far it exceeded what was legal, which is zero
    /// for anything an honest client can produce.
    /// </summary>
    public float Accept(Vector3 claimed, float maxSpeed, float delta) => Move(claimed, maxSpeed, delta, bill: true);

    /// <summary>How far a claim sits from where the server put the body.</summary>
    public float DistanceFrom(Vector3 point)
        => new Vector3(point.X - Validated.X, 0f, point.Z - Validated.Z).Length();

    /// <summary>
    /// Hold position while accruing allowance, for windows where the SERVER moved
    /// the body and the client has not confirmed yet.
    ///
    /// Following the claim instead was wrong in a way that mattered: after a
    /// knockback the authoritative position is the destination, and the client is
    /// still reporting where it was before the push. Chasing that claim dragged the
    /// server's copy BACKWARDS onto stale ground, and combat resolved against it.
    ///
    /// Freezing without accruing was the other wrong answer -- that is what charged
    /// an honest player seven metres every death -- so the allowance keeps filling
    /// and there is no cliff when the window closes.
    /// </summary>
    public void Idle(float maxSpeed, float delta)
    {
        if (!Untrusted.IsFinite(delta) || delta <= 0f) return;

        float rate = Mathf.Max(0f, maxSpeed) * SpeedTolerance;
        Allowance = Mathf.Min(Allowance + rate * delta, rate * BurstSeconds);
    }

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

        // A claim is judged ONCE.
        //
        // This runs every physics tick against the latest claim held, not once
        // per packet, so a claim the server cannot reach yet stays in front of it
        // for many ticks. Billing the shortfall each time charged a player over
        // and over for a single stretch of walking: an honest walker whose
        // updates arrived once a second was billed 3394m for 420m travelled,
        // because the same unreached metres were re-judged sixty times a second.
        //
        // Catching up to a claim already judged is free. Only new information can
        // be an accusation.
        bool alreadyJudged = _everJudged && target == _judged;
        _judged = target;
        _everJudged = true;

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
        return bill && !alreadyJudged ? overreach : 0f;
    }
}
