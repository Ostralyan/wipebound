using Godot;
using System.Collections.Generic;
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
        Attribution();
        PerSourceStatuses();
        Shields();
        Dispels();
        ExpiryEffects();
        Cooldowns();
        CastQueueing();
        Hazards();
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

        // Honest movement leaves no trail; a ladder needs that to be true or the
        // measurement is worthless.
        Untrusted.AdvanceValidatedPosition(Vector3.Zero, new Vector3(0.05f, 0f, 0f),
                                           7f, 1f / 60f, 44f, out float honest);
        Near(honest, 0f, "legitimate movement records no overreach");

        Untrusted.AdvanceValidatedPosition(Vector3.Zero, new Vector3(50f, 0f, 0f),
                                           7f, 1f / 60f, 44f, out float cheating);
        Check(cheating > 40f, $"a teleport records the distance it overreached ({cheating:0.0}m)");

        Untrusted.AdvanceValidatedPosition(Vector3.Zero, new Vector3(float.NaN, 0f, 0f),
                                           7f, 1f / 60f, 44f, out float garbage);
        Near(garbage, Untrusted.GarbageClaimPenalty, "a non-finite claim is charged heavily");

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

    /// <summary>A combatant with no scene behind it, so status logic is testable.</summary>
    private sealed class Dummy : ICombatant
    {
        public string CombatName { get; init; } = "dummy";
        public int CombatId { get; init; }
        public Team Team { get; init; } = Team.Players;
        public Vector3 CombatPosition => Vector3.Zero;
        public bool IsAlive => !HealthPool.IsEmpty;
        public ResourcePool HealthPool { get; } = new(1000f);
        public StatusTracker Status { get; } = new();
        public Contribution Contribution { get; } = new();
        public Node3D Node => null;

        public void ApplyDamage(float amount, ICombatant source, string label)
            => HealthPool.Drain(Combatants.ResolveIncoming(amount, source, this));

        public void Heal(float amount, ICombatant source, string label)
            => Combatants.ResolveHealing(amount, source, this);
        public void Displace(Vector3 destination, float travelSeconds) { }
        public void OnEncounterReset() { }
    }

    private static StatusEffect Custom(string id, System.Action<StatusEffect> configure)
    {
        var definition = new StatusEffect { Id = id, DisplayName = id, Duration = 10f };
        configure(definition);
        StatusLibrary.Register(definition);
        return definition;
    }

    // -- attribution -----------------------------------------------------

    private static void Attribution()
    {
        var attacker = new Dummy { CombatId = 1 };
        var victim = new Dummy { CombatId = 2, Team = Team.Enemies };

        victim.ApplyDamage(50f, attacker, "test");
        Near(attacker.Contribution.DamageDone, 50f, "damage is credited to whoever dealt it");
        Near(victim.Contribution.DamageTaken, 50f, "and counted against whoever took it");
        Near(victim.Contribution.DamageDone, 0f, "taking damage is not dealing it");

        // A shield means less damage landed, and the meter must say so rather than
        // crediting a hit that never reached anybody's health.
        var shielded = new Dummy { CombatId = 3, Team = Team.Enemies };
        shielded.Status.Apply(StatusLibrary.Get(StatusLibrary.Warded), null, 100.0);
        shielded.ApplyDamage(20f, attacker, "test");
        Near(attacker.Contribution.DamageDone, 50f, "damage absorbed by a shield is not credited as damage done");
        Near(shielded.Contribution.DamageAbsorbed, 20f, "absorbed damage is tracked separately");
        Near(shielded.Contribution.DamageTaken, 0f, "a fully absorbed hit is not damage taken");

        // Vulnerability multiplies what actually lands, so the meter should follow it.
        var vulnerable = new Dummy { CombatId = 4, Team = Team.Enemies };
        vulnerable.Status.Apply(StatusLibrary.Get(StatusLibrary.Sundered), null, 100.0);
        vulnerable.ApplyDamage(100f, attacker, "test");
        Near(attacker.Contribution.DamageDone, 170f, "credit follows the modified number, not the raw one");

        // Healing credits what landed, not what was asked for.
        var healer = new Dummy { CombatId = 5 };
        var hurt = new Dummy { CombatId = 6 };
        hurt.HealthPool.Current = hurt.HealthPool.Max - 30f;
        hurt.Heal(100f, healer, "test");
        Near(healer.Contribution.HealingDone, 30f, "overhealing is not a contribution");
        Near(hurt.HealthPool.Current, hurt.HealthPool.Max, "but the target is topped up");

        attacker.Contribution.Clear();
        Near(attacker.Contribution.DamageDone, 0f, "a new attempt starts from zero");
    }

    // -- per-source instances --------------------------------------------

    private static void PerSourceStatuses()
    {
        var alice = new Dummy { CombatId = 11 };
        var bob = new Dummy { CombatId = 22 };
        var target = new Dummy { CombatId = 99, Team = Team.Enemies };

        StatusEffect burning = StatusLibrary.Get(StatusLibrary.Burning);
        Check(burning.Scope == StatusScope.PerSource, "burning is per caster");

        target.Status.Apply(burning, alice, 100.0);
        target.Status.Apply(burning, bob, 100.0);
        Check(target.Status.Active.Count == 2, "two casters hold two instances");
        Check(target.Status.Active[0].SourceId != target.Status.Active[1].SourceId,
              "instances remember who applied them");

        // Each caster's damage-over-time ticks. Two burns hurt twice as much.
        float before = target.HealthPool.Current;
        target.Status.Tick(target, 101.5);
        Near(before - target.HealthPool.Current, 14f, "both instances tick independently");

        // A Shared status collapses to one instance no matter who applies it,
        // which is the narrower behaviour reachable from the wider model.
        var shared = new Dummy { CombatId = 77, Team = Team.Enemies };
        StatusEffect crippled = StatusLibrary.Get(StatusLibrary.Crippled);
        Check(crippled.Scope == StatusScope.Shared, "crippled is shared");
        shared.Status.Apply(crippled, alice, 100.0);
        shared.Status.Apply(crippled, bob, 100.0);
        Check(shared.Status.Active.Count == 1, "a shared status stays a single instance");

        // Modifiers must not multiply once per caster, or a raid stacking the same
        // debuff would scale it by however many people happened to press the button.
        StatusEffect vuln = Custom("test_vuln", d =>
        {
            d.Scope = StatusScope.PerSource;
            d.DamageTakenMultiplier = 1.5f;
        });
        var victim = new Dummy { CombatId = 55, Team = Team.Enemies };
        victim.Status.Apply(vuln, alice, 100.0);
        Near(victim.Status.DamageTakenMultiplier, 1.5f, "one caster's vulnerability");
        victim.Status.Apply(vuln, bob, 100.0);
        Near(victim.Status.DamageTakenMultiplier, 1.5f, "a second caster does not multiply it again");

        // Source and shield survive the wire.
        var mirror = new StatusTracker();
        mirror.Decode(target.Status.Encoded);
        Check(mirror.Active.Count == 2, "both instances survive encoding");
        Check(mirror.Active[0].SourceId == target.Status.Active[0].SourceId, "source id survives encoding");

        // Older, shorter entries must still decode, so the format can grow.
        var legacy = new StatusTracker();
        legacy.Decode($"{StatusLibrary.Crippled}:500:1");
        Check(legacy.Active.Count == 1, "a three-field entry still decodes");
        Near(legacy.MoveSpeedMultiplier, 0.55f, "a truncated entry still aggregates");
    }

    // -- shields ---------------------------------------------------------

    private static void Shields()
    {
        var hero = new Dummy();
        StatusEffect warded = StatusLibrary.Get(StatusLibrary.Warded);
        Check(warded.AbsorbAmount > 0f, "warded is a shield, not a multiplier");

        hero.Status.Apply(warded, hero, 100.0);
        Near(hero.Status.AbsorbRemaining, 45f, "shield starts at full strength");

        hero.ApplyDamage(20f, null, "test");
        Near(hero.HealthPool.Current, 1000f, "a shield takes the hit instead of health");
        Near(hero.Status.AbsorbRemaining, 25f, "the shield is spent down");

        hero.ApplyDamage(40f, null, "test");
        Near(hero.HealthPool.Current, 985f, "damage beyond the shield reaches health");
        Check(hero.Status.Active.Count == 0, "a spent shield disappears rather than lingering at zero");

        // Shields sum, because two shields really are more shield.
        var stacked = new Dummy();
        StatusEffect other = Custom("test_shield", d => { d.AbsorbAmount = 30f; d.Beneficial = true; });
        stacked.Status.Apply(warded, stacked, 100.0);
        stacked.Status.Apply(other, stacked, 100.0);
        Near(stacked.Status.AbsorbRemaining, 75f, "shields from different statuses add");

        // Reapplying restores, or refreshing would be worse than waiting.
        var refreshed = new Dummy();
        refreshed.Status.Apply(warded, refreshed, 100.0);
        refreshed.ApplyDamage(30f, null, "test");
        Near(refreshed.Status.AbsorbRemaining, 15f, "shield partially spent");
        refreshed.Status.Apply(warded, refreshed, 105.0);
        Near(refreshed.Status.AbsorbRemaining, 45f, "reapplying restores the shield");

        // Mitigation applies before absorption: a shield soaks what you would
        // actually have taken.
        var mitigated = new Dummy();
        StatusEffect half = Custom("test_half", d => { d.DamageTakenMultiplier = 0.5f; d.Beneficial = true; });
        mitigated.Status.Apply(half, mitigated, 100.0);
        mitigated.Status.Apply(other, mitigated, 100.0);
        mitigated.ApplyDamage(40f, null, "test");
        Near(mitigated.Status.AbsorbRemaining, 10f, "the shield soaks the mitigated amount, not the raw one");
    }

    // -- dispel ----------------------------------------------------------

    private static void Dispels()
    {
        var hero = new Dummy();
        hero.Status.Apply(StatusLibrary.Get(StatusLibrary.Crippled), null, 100.0);
        hero.Status.Apply(StatusLibrary.Get(StatusLibrary.Haste), null, 100.0);

        Check(hero.Status.Dispel(beneficial: false, count: 1) == 1, "a debuff is cleansed");
        Check(hero.Status.Has(StatusLibrary.Haste), "cleansing a debuff leaves buffs alone");
        Near(hero.Status.MoveSpeedMultiplier, 1.45f, "aggregates update after a cleanse");

        var stubborn = new Dummy();
        StatusEffect detonation = StatusLibrary.Get(StatusLibrary.Detonation);
        Check(!detonation.Dispellable, "a bomb cannot simply be cleansed away");
        stubborn.Status.Apply(detonation, null, 100.0);
        Check(stubborn.Status.Dispel(beneficial: false, count: 5) == 0, "undispellable statuses survive a cleanse");

        var many = new Dummy();
        many.Status.Apply(StatusLibrary.Get(StatusLibrary.Crippled), null, 100.0);
        many.Status.Apply(StatusLibrary.Get(StatusLibrary.Sundered), null, 100.0);
        Check(many.Status.Dispel(beneficial: false, count: 1) == 1, "dispel respects its count");
        Check(many.Status.Active.Count == 1, "only one was taken");
    }

    // -- expiry ----------------------------------------------------------

    private static void ExpiryEffects()
    {
        StatusEffect bomb = Custom("test_bomb", d =>
        {
            d.Duration = 5f;
            d.Beneficial = false;
            d.Dispellable = true;
            d.OnExpire = new Godot.Collections.Array<AbilityEffect> { new DamageEffect { Amount = 40f } };
        });

        var carrier = new Dummy();
        carrier.Status.Apply(bomb, null, 100.0);
        carrier.Status.Tick(carrier, 104.0);
        Near(carrier.HealthPool.Current, 1000f, "the bomb does nothing before its time");

        carrier.Status.Tick(carrier, 105.1);
        Near(carrier.HealthPool.Current, 960f, "the bomb detonates on expiry");
        Check(carrier.Status.Active.Count == 0, "the bomb is gone afterwards");

        // Removing it early is the entire point of removing it.
        var cleansed = new Dummy();
        cleansed.Status.Apply(bomb, null, 100.0);
        cleansed.Status.Dispel(beneficial: false, count: 1);
        cleansed.Status.Tick(cleansed, 200.0);
        Near(cleansed.HealthPool.Current, 1000f, "a dispelled bomb does not detonate");

        var died = new Dummy();
        died.Status.Apply(bomb, null, 100.0);
        died.Status.Clear();
        died.Status.Tick(died, 200.0);
        Near(died.HealthPool.Current, 1000f, "clearing on death does not detonate");

        // A client must never run effects; it only stops drawing them.
        var clientSide = new Dummy();
        clientSide.Status.Apply(bomb, null, 100.0);
        clientSide.Status.PruneForDisplay(200.0);
        Near(clientSide.HealthPool.Current, 1000f, "expiring for display runs nothing");
        Check(clientSide.Status.Active.Count == 0, "expiring for display still drops it");
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
        // Freed explicitly: a Node built with new and never parented leaks at exit,
        // and a test suite that leaks is a test suite nobody trusts about leaks.
        var probe = new Boss();
        float resetDelay = probe.ResetSeconds;
        probe.Free();

        Check(longest > resetDelay,
              $"longest cooldown ({longest}s) exceeds the reset delay, so clearing them matters");
    }

    // -- cast queue ------------------------------------------------------

    private static CastInstance Cast(ICombatant caster, long id, double resolveAt)
        => new() { Id = id, Caster = caster, Ability = new Ability(), ResolveAt = resolveAt };

    private static void CastQueueing()
    {
        var boss = new Dummy { CombatId = -1, Team = Team.Enemies };
        var other = new Dummy { CombatId = -2, Team = Team.Enemies };
        var queue = new CastQueue();

        queue.Add(Cast(boss, 1, 105.0));
        Check(queue.Count == 1, "a cast joins the queue");
        Check(queue.IsCasting(boss), "the caster reads as casting");
        Check(!queue.IsCasting(other), "an unrelated caster does not");

        var resolved = new List<long>();
        queue.Process(104.0, _ => true, c => resolved.Add(c.Id));
        Check(resolved.Count == 0, "nothing resolves before its time");
        Check(queue.Count == 1, "and it stays queued");

        queue.Process(105.0, _ => true, c => resolved.Add(c.Id));
        Check(resolved.Count == 1, "it resolves exactly on time");
        Check(queue.Count == 0, "and leaves the queue");

        // Cancelling stops it outright.
        queue.Add(Cast(boss, 2, 105.0));
        queue.CancelFor(boss);
        Check(!queue.IsCasting(boss), "a cancelled cast is no longer casting");
        resolved.Clear();
        queue.Process(200.0, _ => true, c => resolved.Add(c.Id));
        Check(resolved.Count == 0, "a cancelled cast never resolves");

        // A caster that died mid-cast takes its mechanic with it.
        queue.Add(Cast(boss, 3, 105.0));
        resolved.Clear();
        queue.Process(200.0, _ => false, c => resolved.Add(c.Id));
        Check(resolved.Count == 0, "an invalid caster's cast is dropped, not resolved");
        Check(queue.Count == 0, "and removed");

        // THE REENTRANCY CASE. An interrupt cancels a cast from inside the
        // resolution of another one. Removing during that walk threw
        // IndexOutOfRange the first time an interrupt ever landed in a real fight.
        var reentrant = new CastQueue();
        reentrant.Add(Cast(boss, 10, 100.0));
        reentrant.Add(Cast(other, 11, 100.0));
        reentrant.Add(Cast(other, 12, 100.0));

        resolved.Clear();
        bool threw = false;

        try
        {
            reentrant.Process(150.0, _ => true, c =>
            {
                resolved.Add(c.Id);
                if (c.Id == 10) reentrant.CancelFor(other);   // the interrupt
            });
        }
        catch (System.Exception)
        {
            threw = true;
        }

        Check(!threw, "cancelling from inside a resolution does not throw");
        Check(resolved.Count == 1 && resolved[0] == 10, "the interrupted casts never resolved");
        Check(reentrant.Count == 0, "and the queue is left clean");

        // An effect that starts a cast must not have it resolve inside the same walk.
        var cascading = new CastQueue();
        cascading.Add(Cast(boss, 20, 100.0));
        resolved.Clear();
        cascading.Process(150.0, _ => true, c =>
        {
            resolved.Add(c.Id);
            if (c.Id == 20) cascading.Add(Cast(boss, 21, 0.0));
        });
        Check(resolved.Count == 1, "a cast started during resolution waits for the next tick");
        Check(cascading.Count == 1, "and is still queued");

        var everything = new CastQueue();
        everything.Add(Cast(boss, 30, 100.0));
        everything.Add(Cast(other, 31, 100.0));
        Check(everything.CancelAll().Count == 2, "cancel-all reports what it stopped");
        Check(everything.Count == 0, "and empties the queue");
    }

    // -- hazards ---------------------------------------------------------

    private static void Hazards()
    {
        var boss = new Dummy { CombatId = -1, Team = Team.Enemies };
        var definition = new Hazard { Id = "test_fire", DisplayName = "Fire", Duration = 14f, TickInterval = 1f };

        HazardInstance Fire(double from) => new()
        {
            Id = 1, Definition = definition, Owner = boss,
            Area = new TelegraphArea(TelegraphShape.Circle, Vector3.Zero, 0f, 6f),
            ExpiresAt = from + definition.Duration, NextTickAt = from,
        };

        var field = new HazardField();
        field.Add(Fire(100.0));
        Check(field.Count == 1, "a hazard joins the field");

        Check(field.Advance(100.0).Count == 1, "it burns immediately");
        Check(field.Advance(100.5).Count == 0, "and not again before its interval");
        Check(field.Advance(101.0).Count == 1, "and again once the interval elapses");

        Check(field.Advance(120.0).Count == 0, "an expired hazard stops burning");
        Check(field.Count == 0, "and is dropped");

        // The bug this is here for: Cinders lasts fourteen seconds and an encounter
        // reset takes eight, so fire from the previous attempt was still burning
        // when the raid was revived into it.
        var lingering = new HazardField();
        lingering.Add(Fire(100.0));
        Check(definition.Duration > 8f, "the hazard genuinely outlives a reset, so clearing it matters");
        lingering.Clear();
        Check(lingering.Count == 0, "an encounter reset clears the ground");
        Check(lingering.Advance(101.0).Count == 0, "and nothing burns afterwards");

        // Geometry is what decides who is caught, and it is already pinned.
        TelegraphArea area = Fire(100.0).Area;
        Check(area.Contains(new Vector3(3f, 0f, 0f)), "a hazard catches what stands in it");
        Check(!area.Contains(new Vector3(9f, 0f, 0f)), "and not what stands clear");
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
