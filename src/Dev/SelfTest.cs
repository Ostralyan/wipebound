using Godot;
using Wipebound.Combat;
using Wipebound.Combat.Commands;

namespace Wipebound.Dev;

/// <summary>
/// Assertions over the pure logic that the rest of the game trusts.
///
/// Run headless, exits non-zero on failure, so CI can gate on it:
///
///     godot --headless -- --selftest
///
/// Deliberately covers the things that are silent when they break. Geometry
/// parity matters because TelegraphArea.Field is duplicated in GLSL and nothing
/// enforces agreement; this at least pins the C# side so drift is detected.
/// Movement validation matters because it is the check on an untrusted channel
/// that does not look like one.
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        _passed = 0;
        _failed = 0;

        GeometryParity();
        UntrustedInput();
        MovementBudget();
        StatusEncoding();
        Resources();
        Cooldowns();
        CommandPayloads();
        SpawnAllocation();

        GD.Print($"[selftest] {_passed} passed, {_failed} failed");
        return _failed;
    }

    // -- helpers ---------------------------------------------------------

    private static void Check(bool condition, string what)
    {
        if (condition) { _passed++; return; }
        _failed++;
        GD.PrintErr($"[selftest] FAIL: {what}");
    }

    private static void Near(float actual, float expected, string what, float epsilon = 0.001f)
        => Check(Mathf.Abs(actual - expected) <= epsilon, $"{what} (expected {expected}, got {actual})");

    // -- geometry --------------------------------------------------------

    private static void GeometryParity()
    {
        var circle = new TelegraphArea(TelegraphShape.Circle, Vector3.Zero, 0f, 10f);
        Near(circle.Field(Vector3.Zero), -10f, "circle centre");
        Near(circle.Field(new Vector3(10f, 0f, 0f)), 0f, "circle exactly on the rim");
        Near(circle.Field(new Vector3(12f, 0f, 0f)), 2f, "circle outside");
        Check(circle.Contains(new Vector3(9.99f, 0f, 0f)), "circle just inside contains");
        Check(!circle.Contains(new Vector3(10.01f, 0f, 0f)), "circle just outside excluded");

        // Y is ignored everywhere: combatants and footprints live on the ground plane.
        Near(circle.Field(new Vector3(12f, 99f, 0f)), 2f, "circle ignores height");

        var donut = new TelegraphArea(TelegraphShape.Donut, Vector3.Zero, 0f, 10f, innerRadius: 5f);
        Near(donut.Field(Vector3.Zero), 5f, "donut hole is safe");
        Near(donut.Field(new Vector3(7f, 0f, 0f)), -2f, "donut band is deadly");
        Near(donut.Field(new Vector3(12f, 0f, 0f)), 2f, "donut outside");

        // Facing 0 means forward is -Z, matching Godot's convention.
        var cone = new TelegraphArea(TelegraphShape.Cone, Vector3.Zero, 0f, 10f,
                                     halfAngle: Mathf.DegToRad(45f));
        Check(cone.Contains(new Vector3(0f, 0f, -5f)), "cone covers straight ahead");
        Check(!cone.Contains(new Vector3(0f, 0f, 5f)), "cone excludes behind");
        Check(!cone.Contains(new Vector3(0f, 0f, -15f)), "cone excludes beyond its range");
        Check(cone.Contains(Vector3.Zero), "cone apex counts as inside");

        var lane = new TelegraphArea(TelegraphShape.Rectangle, Vector3.Zero, 0f, 20f, halfWidth: 3f);
        Near(lane.Field(new Vector3(0f, 0f, -10f)), -3f, "lane centre line");
        Near(lane.Field(new Vector3(5f, 0f, -10f)), 2f, "lane beside the edge");
        Check(!lane.Contains(new Vector3(0f, 0f, 5f)), "lane excludes behind the caster");
        Check(!lane.Contains(new Vector3(0f, 0f, -25f)), "lane excludes beyond its length");

        // An ability with an origin at the caster must not silently move.
        var ability = new Ability
        {
            Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtCaster, Radius = 6f,
        };
        TelegraphArea built = ability.BuildArea(new Vector3(4f, 0f, 4f), new Vector3(30f, 0f, 30f));
        Near(built.Center.X, 4f, "AtCaster ignores the aim point");
    }

    // -- untrusted input -------------------------------------------------

    private static void UntrustedInput()
    {
        Check(!Untrusted.IsFinite(float.NaN), "NaN is not finite");
        Check(!Untrusted.IsFinite(float.PositiveInfinity), "infinity is not finite");
        Check(!Untrusted.IsFinite(new Vector3(1f, float.NaN, 1f)), "NaN in Y is caught");
        Check(!Untrusted.IsFinite(new Vector3(float.NegativeInfinity, 0f, 0f)), "-infinity in X is caught");
        Check(Untrusted.IsFinite(new Vector3(1f, 2f, 3f)), "ordinary vectors are finite");

        // The exploit this exists to stop: one NaN claim used to poison the
        // server's validated position permanently.
        Vector3 validated = new(5f, 0f, 5f);
        Vector3 poisoned = Untrusted.AdvanceValidatedPosition(
            validated, new Vector3(float.NaN, 0f, 0f), 7f, 1f / 60f, 44f);
        Check(Untrusted.IsFinite(poisoned), "a NaN claim cannot poison the validated position");
        Near(poisoned.X, 5f, "a NaN claim is ignored outright");

        Vector3 farAway = Untrusted.AdvanceValidatedPosition(
            Vector3.Zero, new Vector3(1e30f, 0f, 0f), 7f, 1f, 44f);
        Check(farAway.Length() <= 44.01f, "claims outside the arena are clamped to it");
    }

    private static void MovementBudget()
    {
        // Simulate one second of a client claiming it is very far away, and measure
        // how far the server actually let it travel.
        const float nominal = 7f;
        const float dt = 1f / 60f;

        Vector3 validated = Vector3.Zero;
        var claim = new Vector3(500f, 0f, 0f);

        for (int tick = 0; tick < 60; tick++)
            validated = Untrusted.AdvanceValidatedPosition(validated, claim, nominal, dt, 1000f);

        float travelled = validated.Length();
        Check(travelled <= nominal * Untrusted.SpeedTolerance + 0.01f,
              $"sustained speed stays within tolerance (travelled {travelled:0.00}m in 1s at a nominal {nominal})");
        Check(travelled > nominal, "legitimate movement is not over-restricted");

        // A hero standing still must not drift.
        Vector3 still = new(3f, 0f, 3f);
        for (int tick = 0; tick < 60; tick++)
            still = Untrusted.AdvanceValidatedPosition(still, new Vector3(3f, 0f, 3f), nominal, dt, 44f);
        Near(still.X, 3f, "a stationary claim does not drift");
    }

    // -- statuses --------------------------------------------------------

    private static void StatusEncoding()
    {
        var tracker = new StatusTracker();
        StatusEffect crippled = StatusLibrary.Get(StatusLibrary.Crippled);
        Check(crippled is not null, "status library resolves crippled");

        tracker.Apply(crippled, null, 100.0);
        Near(tracker.MoveSpeedMultiplier, 0.55f, "crippled halves move speed");

        // Round trip through the wire form must preserve the aggregate exactly,
        // because the client drives its own movement from the decoded copy.
        var mirror = new StatusTracker();
        mirror.Decode(tracker.Encoded);
        Near(mirror.MoveSpeedMultiplier, tracker.MoveSpeedMultiplier, "decode preserves move speed");
        Check(mirror.Active.Count == 1, "decode preserves the status count");
        Near((float)mirror.Active[0].ExpiresAt, (float)tracker.Active[0].ExpiresAt, "decode preserves expiry", 0.01f);

        // Stacking is multiplicative per stack.
        var stacked = new StatusTracker();
        StatusEffect sundered = StatusLibrary.Get(StatusLibrary.Sundered);
        stacked.Apply(sundered, null, 100.0);
        Near(stacked.DamageTakenMultiplier, 1.2f, "one stack of sundered");
        stacked.Apply(sundered, null, 100.0);
        Near(stacked.DamageTakenMultiplier, 1.44f, "two stacks compound");
        stacked.Apply(sundered, null, 100.0);
        Near(stacked.DamageTakenMultiplier, 1.728f, "three stacks compound");
        stacked.Apply(sundered, null, 100.0);
        Near(stacked.DamageTakenMultiplier, 1.728f, "stacks stop at MaxStacks");

        // Expiry is absolute server time, so a decoded copy expires with the original.
        var expiring = new StatusTracker();
        expiring.Apply(crippled, null, 100.0);
        expiring.PruneForDisplay(100.0 + crippled.Duration + 0.01);
        Check(expiring.Active.Count == 0, "expired statuses are dropped");
        Near(expiring.MoveSpeedMultiplier, 1f, "aggregates reset when a status expires");

        var empty = new StatusTracker();
        empty.Decode("");
        Near(empty.MoveSpeedMultiplier, 1f, "empty payload decodes cleanly");
        empty.Decode("nonsense|also:garbage|missing:1");
        Near(empty.MoveSpeedMultiplier, 1f, "malformed payload decodes cleanly");

        var silenced = new StatusTracker();
        silenced.Apply(StatusLibrary.Get(StatusLibrary.Silenced), null, 100.0);
        Check(silenced.Silenced, "silence flag aggregates");
    }

    // -- resources -------------------------------------------------------

    private static void Resources()
    {
        var pool = new ResourcePool(100f);
        Check(pool.Current == 100f, "pool starts full");
        Check(pool.TrySpend(40f), "affordable spend succeeds");
        Near(pool.Current, 60f, "spend deducts");
        Check(!pool.TrySpend(80f), "unaffordable spend is refused");
        Near(pool.Current, 60f, "a refused spend does not deduct");

        pool.Drain(1000f);
        Near(pool.Current, 0f, "drain floors at zero");
        Check(pool.IsEmpty, "empty pool reports empty");

        pool.Restore(1000f);
        Near(pool.Current, 100f, "restore ceilings at max");

        var regen = new ResourcePool(100f, 10f) { Current = 0f };
        regen.Tick(1f);
        Near(regen.Current, 10f, "regen adds per second");
        regen.Tick(100f);
        Near(regen.Current, 100f, "regen ceilings at max");
    }

    // -- cooldowns -------------------------------------------------------

    private static void Cooldowns()
    {
        var set = new CooldownSet();
        set.Resize(4);
        Check(set.Count == 4, "cooldown set sizes to the kit");
        Check(set.IsReady(0, 100.0), "a fresh slot is ready");

        set.Start(0, 100.0, 5f);
        Check(!set.IsReady(0, 100.0), "starting a cooldown blocks the slot");
        Check(!set.IsReady(0, 104.9), "the slot stays blocked until it elapses");
        Check(set.IsReady(0, 105.0), "the slot frees exactly on time");
        Check(set.IsReady(1, 100.0), "other slots are unaffected");

        Near(set.Fraction(0, 100.0, 5f), 1f, "fraction is full at the start");
        Near(set.Fraction(0, 102.5, 5f), 0.5f, "fraction is half way through");
        Near(set.Fraction(0, 105.0, 5f), 0f, "fraction empties on expiry");
        Near(set.Fraction(0, 200.0, 5f), 0f, "fraction never goes negative");

        // Slot indices originate in client payloads, so out-of-range must be inert
        // rather than an exception inside an RPC handler.
        Check(!set.IsReady(-1, 100.0), "negative slot is never ready");
        Check(!set.IsReady(99, 100.0), "out-of-range slot is never ready");
        set.Start(-1, 100.0, 5f);
        set.Start(99, 100.0, 5f);
        set.SetReadyAt(-5, 999.0);
        Near(set.Fraction(-1, 100.0, 5f), 0f, "out-of-range access is inert");

        // A wipe must hand every attempt the same starting state.
        set.Start(0, 100.0, 20f);
        set.Start(3, 100.0, 20f);
        set.Clear();
        Check(set.IsReady(0, 100.0), "clearing frees a long cooldown");
        Check(set.IsReady(3, 100.0), "clearing frees every slot");

        // The reset delay is shorter than the longest ability cooldown, which is
        // exactly why cooldowns cannot be allowed to survive a wipe.
        float longest = 0f;
        foreach (Ability ability in PlayerKit.Build())
            longest = Mathf.Max(longest, ability.Cooldown);
        Check(longest > new Boss().ResetSeconds,
              $"longest cooldown ({longest}s) exceeds the reset delay, so clearing them matters");
    }

    // -- command payloads ------------------------------------------------

    private static void CommandPayloads()
    {
        Check(!TryRead(new Godot.Collections.Dictionary()), "empty payload rejected");
        Check(!TryRead(new Godot.Collections.Dictionary { ["slot"] = 0 }), "missing aim rejected");
        Check(!TryRead(new Godot.Collections.Dictionary { ["aim"] = Vector3.Zero }), "missing slot rejected");

        Check(!TryRead(new Godot.Collections.Dictionary
        {
            ["slot"] = 0,
            ["aim"] = new Vector3(float.NaN, 0f, 0f),
        }), "NaN in aim X rejected");

        // The bypass: DistanceTo uses all three components, so a NaN in Y made every
        // range comparison false while TelegraphArea later discarded Y entirely.
        Check(!TryRead(new Godot.Collections.Dictionary
        {
            ["slot"] = 0,
            ["aim"] = new Vector3(0f, float.NaN, 0f),
        }), "NaN in aim Y rejected");

        Check(!TryRead(new Godot.Collections.Dictionary
        {
            ["slot"] = 0,
            ["aim"] = new Vector3(0f, 0f, float.PositiveInfinity),
        }), "infinite aim rejected");

        // Wrong Variant types must return false, never throw: an exception inside an
        // RPC handler is a denial of service any client can trigger.
        Check(!TryRead(new Godot.Collections.Dictionary { ["slot"] = "zero", ["aim"] = Vector3.Zero }),
              "non-integer slot rejected without throwing");
        Check(!TryRead(new Godot.Collections.Dictionary { ["slot"] = 0, ["aim"] = "over there" }),
              "non-vector aim rejected without throwing");
        Check(!TryRead(new Godot.Collections.Dictionary { ["slot"] = new Godot.Collections.Array(), ["aim"] = 7 }),
              "wholly wrong types rejected without throwing");
        Check(!TryRead(new Godot.Collections.Dictionary { ["slot"] = -1, ["aim"] = Vector3.Zero }),
              "negative slot rejected");
        Check(!TryRead(new Godot.Collections.Dictionary { ["slot"] = 9999, ["aim"] = Vector3.Zero }),
              "absurd slot rejected");

        Check(TryRead(new Godot.Collections.Dictionary { ["slot"] = 1, ["aim"] = new Vector3(3f, 0f, 4f) }),
              "a well-formed payload is accepted");
    }

    private static bool TryRead(Godot.Collections.Dictionary payload)
    {
        try
        {
            return new CastAbilityCommand().Read(payload);
        }
        catch (System.Exception error)
        {
            GD.PrintErr($"[selftest] Read threw instead of returning false: {error.GetType().Name}");
            return true;   // treat a throw as a failure of the "never throws" contract
        }
    }

    // -- spawn allocation ------------------------------------------------

    private static void SpawnAllocation()
    {
        var occupied = new System.Collections.Generic.HashSet<int> { 0, 1, 2 };
        Check(Net.SpawnRing.NextFreeIndex(occupied) == 3, "fresh players take the next slot");

        occupied.Remove(1);
        Check(Net.SpawnRing.NextFreeIndex(occupied) == 1, "a vacated slot is reused, not doubled up");

        var none = new System.Collections.Generic.HashSet<int>();
        Check(Net.SpawnRing.NextFreeIndex(none) == 0, "first player takes slot zero");

        Check(!Net.SpawnRing.PointFor(0).IsEqualApprox(Net.SpawnRing.PointFor(1)),
              "distinct slots are distinct places");
    }
}
