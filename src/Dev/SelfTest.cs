using Godot;
using System.Collections.Generic;
using Wipebound.Combat;
using Wipebound.Combat.Commands;
using Wipebound.Session;

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
        Targeting();
        PerSourceStatuses();
        Shields();
        Dispels();
        ExpiryEffects();
        Cooldowns();
        CastQueueing();
        Hazards();
        Classes();
        DeadTargetsStayDead();
        Channelling();
        KitShape();
        Controls();
        Submission();
        Fingerprint();
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
        var poisoned = new MovementValidator();
        poisoned.Reset(new Vector3(5f, 0f, 5f));
        float charged = poisoned.Accept(new Vector3(float.NaN, 0f, 0f), 7f, 1f / 60f);
        Check(Untrusted.IsFinite(poisoned.Validated), "a NaN claim cannot poison the validated position");
        Near(poisoned.Validated.X, 5f, "a NaN claim moves nothing");
        Near(charged, MovementValidator.GarbageClaimPenalty, "a non-finite claim is charged heavily");

        var wanderer = new MovementValidator { ArenaRadius = 44f };
        wanderer.Reset(Vector3.Zero);
        for (int tick = 0; tick < 600; tick++) wanderer.Accept(new Vector3(1e30f, 0f, 0f), 7f, 1f / 60f);
        Check(wanderer.Validated.Length() <= 44.01f, "claims outside the arena are clamped to it");
    }

    private static void MovementBudget()
    {
        const float nominal = 7f;
        const float dt = 1f / 60f;

        // THE CASE THAT SHIPPED BROKEN. Claims arrive at 20Hz while validation runs
        // at 60Hz, so a claim carries about three frames of travel. Billing it
        // against one frame charged an honest walker a hundred metres in thirteen
        // seconds, and the ladder rejected any overreach at all.
        var honest = new MovementValidator { ArenaRadius = 1000f };
        honest.Reset(Vector3.Zero);

        var claim = Vector3.Zero;
        float charged = 0f;

        for (int tick = 0; tick < 600; tick++)
        {
            // The client walks every frame but only publishes every third, and a
            // claim reports where it HAS been rather than where it is about to be.
            if (tick % 3 == 0) claim = new Vector3(nominal * dt * tick, 0f, 0f);
            charged += honest.Accept(claim, nominal, dt);
        }

        Near(charged, 0f, "ten seconds of honest walking is charged nothing", 0.001f);
        Check(honest.Validated.X > nominal * 9f, "and the honest walker actually got where it was going");

        // Sustained speed is still impossible.
        var speeding = new MovementValidator { ArenaRadius = 10_000f };
        speeding.Reset(Vector3.Zero);
        for (int tick = 0; tick < 60; tick++) speeding.Accept(new Vector3(5000f, 0f, 0f), nominal, dt);
        float travelled = speeding.Validated.Length();
        Check(travelled <= nominal * MovementValidator.SpeedTolerance + 0.01f,
              $"one second of claiming to be far away travels at most the legal speed ({travelled:0.00}m)");

        // Standing still cannot bank a teleport.
        var patient = new MovementValidator { ArenaRadius = 10_000f };
        patient.Reset(Vector3.Zero);
        for (int tick = 0; tick < 600; tick++) patient.Accept(Vector3.Zero, nominal, dt);
        float blink = patient.Accept(new Vector3(200f, 0f, 0f), nominal, dt);
        float banked = patient.Validated.Length();
        Check(banked <= nominal * MovementValidator.SpeedTolerance * MovementValidator.BurstSeconds + 0.01f,
              $"ten idle seconds bank only a fraction of a second of travel ({banked:0.00}m)");
        Check(blink > 190f, "and the rest of a teleport is charged");

        // A server-commanded destination must not be dragged backwards by the
        // client's trailing claim. Idle holds position and accrues; it never chases.
        var pushed = new MovementValidator { ArenaRadius = 1000f };
        pushed.Reset(new Vector3(19f, 0f, 0f));
        for (int tick = 0; tick < 30; tick++) pushed.Idle(nominal, dt);
        Near(pushed.Validated.X, 19f, "waiting for acknowledgement holds the commanded destination");
        Check(pushed.Allowance > 0f, "and keeps accruing, so there is no cliff when the wait ends");

        // Which is exactly what a knockback looks like: destination 19, client
        // still reporting 10 while it slides.
        Near(pushed.DistanceFrom(new Vector3(10f, 0f, 0f)), 9f, "distance from a stale claim is measurable");
        Check(pushed.DistanceFrom(new Vector3(18.7f, 0f, 0f)) < 1f, "and an arrived claim reads as acknowledged");

        // A stationary claim never drifts.
        var still = new MovementValidator();
        still.Reset(new Vector3(3f, 0f, 3f));
        for (int tick = 0; tick < 60; tick++) still.Accept(new Vector3(3f, 0f, 3f), nominal, dt);
        Near(still.Validated.X, 3f, "a stationary claim does not drift");
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
        public Vector3 CombatPosition { get; set; } = Vector3.Zero;
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

    // -- who the adds come for -------------------------------------------

    private static void Targeting()
    {
        var near = new Dummy { CombatId = 1, CombatName = "near" };
        var far = new Dummy { CombatId = 2, CombatName = "far" };
        var hurt = new Dummy { CombatId = 3, CombatName = "hurt" };
        hurt.HealthPool.Current = hurt.HealthPool.Max * 0.2f;

        var everyone = new List<ICombatant> { near, far, hurt };

        // Dummy reports a fixed position, so distance is stubbed by ordering: all
        // three sit at the origin and Nearest resolves to the first.
        Check(TargetSelection.Choose(TargetRule.Nearest, everyone, Vector3.Zero) is not null,
              "nearest always picks somebody");

        Check(ReferenceEquals(TargetSelection.Choose(TargetRule.LowestHealth, everyone, Vector3.Zero), hurt),
              "lowest health goes for whoever is already hurt");

        // Health FRACTION, not absolute, so a bigger pool is not automatically safer.
        var big = new Dummy { CombatId = 4, CombatName = "big" };
        big.HealthPool.Max = 5000f;
        big.HealthPool.Current = 500f;   // 10%, worse off than hurt's 20%
        Check(ReferenceEquals(TargetSelection.Choose(TargetRule.LowestHealth,
                  new List<ICombatant> { hurt, big }, Vector3.Zero), big),
              "lowest health compares fractions, not raw numbers");

        // Attention: whoever hit hardest recently, and no opinion means proximity.
        var attention = new Dictionary<int, float> { [far.CombatId] = 120f, [near.CombatId] = 30f };
        Check(ReferenceEquals(TargetSelection.Choose(TargetRule.HighestRecentDamage, everyone,
                  Vector3.Zero, null, attention), far),
              "attention follows whoever hurt it most");

        Check(TargetSelection.Choose(TargetRule.HighestRecentDamage, everyone, Vector3.Zero, null,
                  new Dictionary<int, float>()) is not null,
              "an unhit minion still picks somebody rather than standing idle");

        // Attention is a MEMORY, measured in time since you last hit it -- not
        // merely a decaying number. Uniform decay never reorders anything, so a
        // lone attacker used to stay the target for about thirty seconds while the
        // comment promised four.
        // Half a half-life of ageing, staying inside the memory window.
        var table = new AttentionTable();
        table.Record(1, 100f, 100.0);
        table.Forget(102.0, TargetSelection.AttentionHalfLife * 0.5f);
        Near(table.Scores[1], 70.7f, "a hit loses weight as it ages", 1f);

        table.Forget(100.0 + TargetSelection.AttentionMemory + 0.1, 0.1f);
        Check(table.Count == 0, "and is forgotten entirely once the memory window passes");

        // Being hit again keeps you in mind.
        var refreshed = new AttentionTable();
        refreshed.Record(1, 100f, 100.0);
        for (int i = 0; i < 20; i++)
        {
            double at = 100.0 + i * 0.5;
            refreshed.Record(1, 1f, at);
            refreshed.Forget(at, 0.5f);
        }
        Check(refreshed.Count == 1, "a repeat attacker stays in mind");

        // A newer, smaller attacker can overtake an older, larger one -- which is
        // the entire point of decaying rather than accumulating.
        var contest = new AttentionTable();
        contest.Record(1, 100f, 100.0);

        for (int i = 0; i < 5; i++)
        {
            double at = 100.5 + i * 0.5;
            contest.Record(2, 25f, at);
            contest.Forget(at, 0.5f);
        }

        Check(contest.Scores[2] > contest.Scores[1],
              $"steady pressure overtakes one big old hit ({contest.Scores[2]:0} vs {contest.Scores[1]:0})");

        // Fixate keeps its victim while they are available...
        ICombatant kept = TargetSelection.Choose(TargetRule.Fixate, everyone, Vector3.Zero, keeping: far);
        Check(ReferenceEquals(kept, far), "fixate stays on its victim");

        // ...and lets go when they die, rather than chasing a corpse.
        far.HealthPool.Current = 0f;
        var living = new List<ICombatant> { near, hurt };
        ICombatant replaced = TargetSelection.Choose(TargetRule.Fixate, living, Vector3.Zero, keeping: far);
        Check(!ReferenceEquals(replaced, far), "fixate releases a dead victim");

        // Adds spawning together split up: the Hunted marker is how they avoid
        // each other without knowing about each other.
        near.Status.Apply(StatusLibrary.Get(StatusLibrary.Hunted), null, 100.0);
        ICombatant second = TargetSelection.Choose(TargetRule.Fixate,
            new List<ICombatant> { near, hurt }, Vector3.Zero);
        Check(ReferenceEquals(second, hurt), "a second add prefers somebody not already hunted");

        // But it still commits to somebody when everyone is spoken for.
        hurt.Status.Apply(StatusLibrary.Get(StatusLibrary.Hunted), null, 100.0);
        Check(TargetSelection.Choose(TargetRule.Fixate,
                  new List<ICombatant> { near, hurt }, Vector3.Zero) is not null,
              "a third add still picks somebody when everyone is already hunted");

        Check(TargetSelection.Choose(TargetRule.Fixate, new List<ICombatant>(), Vector3.Zero) is null,
              "an empty arena yields nobody rather than throwing");
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

        // A truncated entry is corruption, not an older sender: a client and its
        // server are always the same build, so there is no shorter format to be
        // compatible with.
        var truncated = new StatusTracker();
        truncated.Decode($"{StatusLibrary.Crippled}:500:1");
        Check(truncated.Active.Count == 0, "a truncated entry is rejected rather than half-read");
        Near(truncated.MoveSpeedMultiplier, 1f, "and contributes nothing");

        var whole = new StatusTracker();
        whole.Decode($"{StatusLibrary.Crippled}:500:1:7:0");
        Check(whole.Active.Count == 1, "a complete entry decodes");
        Near(whole.MoveSpeedMultiplier, 0.55f, "and aggregates");
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

        // A damage-over-time that KILLS its host clears the status list from inside
        // the walk that is ticking it. Throwing there aborted the whole physics
        // frame for that hero, and it only became reachable once the boss started
        // setting people on fire often enough to finish one off.
        StatusEffect lethal = Custom("test_lethal", d =>
        {
            d.Duration = 30f;
            d.Beneficial = false;
            d.TickInterval = 0.1f;
            d.OnTick = new Godot.Collections.Array<AbilityEffect> { new DamageEffect { Amount = 10_000f } };
        });

        var doomed = new Dummy();
        doomed.Status.Apply(lethal, null, 100.0);
        doomed.Status.Apply(StatusLibrary.Get(StatusLibrary.Crippled), null, 100.0);

        bool threw = false;
        try { doomed.Status.Tick(doomed, 101.0); }
        catch (System.Exception) { threw = true; }

        Check(!threw, "a lethal damage-over-time does not throw while ticking");
        Check(!doomed.IsAlive, "and it does kill");

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
        foreach (Ability ability in PlayerKit.For(HeroClass.Ember))
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

    // -- classes and aiming ----------------------------------------------

    private static void Classes()
    {
        var alice = new Dummy { CombatId = 1, CombatName = "alice" };
        var bob = new Dummy { CombatId = 2, CombatName = "bob" };
        var boss = new Dummy { CombatId = -1, CombatName = "boss", Team = Team.Enemies };

        Check(Combatants.Matches(bob, alice, TargetFilter.OtherAllies), "an ally is a legal other-ally");
        Check(!Combatants.Matches(alice, alice, TargetFilter.OtherAllies), "you are not your own other-ally");
        Check(!Combatants.Matches(boss, alice, TargetFilter.OtherAllies), "an enemy is never an other-ally");
        Check(Combatants.Matches(alice, alice, TargetFilter.Allies), "plain Allies still includes yourself");

        // Only one origin needs a person; the rest are places.
        foreach (AbilityOrigin origin in System.Enum.GetValues<AbilityOrigin>())
        {
            var probe = new Ability { Origin = origin };
            Check(probe.RequiresTarget == (origin == AbilityOrigin.AtTargetUnit),
                  $"{origin} requires a target only when it is AtTargetUnit");
        }

        // A targeted footprint lands on the person, not on the caster.
        var targeted = new Ability { Origin = AbilityOrigin.AtTargetUnit, Shape = TelegraphShape.Circle, Radius = 0.6f };
        TelegraphArea onThem = targeted.BuildArea(new Vector3(5f, 0f, 5f), new Vector3(-9f, 0f, 3f));
        Near(onThem.Center.X, -9f, "a targeted ability centres on its target");
        Check(onThem.Contains(new Vector3(-9f, 0f, 3f)), "and catches them");
        Check(!onThem.Contains(new Vector3(-6f, 0f, 3f)), "and nobody standing three metres away");

        // Every class is distinct, and every ability in every kit is well formed.
        var seen = new List<string>();
        foreach (HeroClass hero in System.Enum.GetValues<HeroClass>())
        {
            var kit = PlayerKit.For(hero);
            Check(kit.Count >= 4, $"{hero} has a kit");

            foreach (Ability ability in kit)
            {
                Check(!string.IsNullOrEmpty(ability.Id), $"{hero} ability has an id");
                Check(ability.Effects.Count > 0, $"{hero} {ability.DisplayName} does something");
                Check(!ability.RequiresTarget || ability.Range > 0f,
                      $"{hero} {ability.DisplayName} bounds its reach if it targets a person");
                seen.Add(ability.Id);
            }
        }

        Check(seen.Count == new HashSet<string>(seen).Count, "no two abilities share an id");

        // THE DESIGN, ASSERTED. The Verdant depends on somebody else, and that is
        // the reason for having classes at all -- so it is a test, not a comment
        // somebody can quietly contradict.
        bool foundHeal = false;
        foreach (Ability ability in PlayerKit.For(HeroClass.Verdant))
        {
            bool heals = false;
            foreach (AbilityEffect effect in ability.Effects) heals |= effect is HealEffect;
            if (!heals) continue;

            foundHeal = true;
            Check(ability.Affects == TargetFilter.OtherAllies,
                  $"Verdant's {ability.DisplayName} cannot be turned on itself");
        }

        Check(foundHeal, "the Verdant actually heals somebody");
    }

    /// <summary>
    /// The target list is built once and every effect in an ability shares it, so
    /// an effect that kills changes the world the effects after it act on. Damage
    /// and healing already refuse the dead; this proves statuses do too.
    /// </summary>
    private static void DeadTargetsStayDead()
    {
        var victim = new Dummy { CombatId = 9, CombatName = "victim" };
        victim.HealthPool.Current = 10f;

        var targets = new List<ICombatant> { victim };
        var context = new EffectContext
        {
            AbilityName = "two-part",
            Caster = new Dummy { CombatId = -9, CombatName = "boss", Team = Team.Enemies },
            Targets = targets,
            Candidates = targets,
            Now = 100.0,
        };

        new DamageEffect { Amount = 40f }.Resolve(context);
        Check(!victim.IsAlive, "the first effect in the list kills the target");

        new ApplyStatusEffect { StatusId = StatusLibrary.Burning }.Resolve(context);
        Check(victim.Status.Active.Count == 0, "and a later effect does not hang a status on the body");

        // The living are unaffected by the guard.
        var survivor = new Dummy { CombatId = 10, CombatName = "survivor" };
        var live = new List<ICombatant> { survivor };
        new ApplyStatusEffect { StatusId = StatusLibrary.Burning }.Resolve(new EffectContext
        {
            AbilityName = "two-part",
            Caster = context.Caster,
            Targets = live,
            Candidates = live,
            Now = 100.0,
        });

        Check(survivor.Status.Active.Count == 1, "somebody still standing still gets it");
    }

    /// <summary>
    /// Channels and the things they fire.
    ///
    /// The property worth protecting is that a projectile's position is a
    /// FUNCTION of the clock rather than an accumulated step. That is what lets
    /// the server send one packet per projectile and have every client agree
    /// about where it is, and it is what makes a dropped frame invisible instead
    /// of a stutter.
    /// </summary>
    private static void Channelling()
    {
        var shot = new Projectile { Speed = 10f, Radius = 1f, Range = 50f, Damage = 5f };
        Near((float)shot.Lifetime, 5f, "a projectile lives for range over speed");

        var flying = new ProjectileInstance
        {
            Id = 1,
            Definition = shot,
            Origin = Vector3.Zero,
            Direction = Vector3.Right,
            SpawnedAt = 100.0,
            ExpiresAt = 100.0 + shot.Lifetime,
        };

        Near(flying.PositionAt(102.0).X, 20f, "it is wherever the clock says it is");
        Near(flying.PositionAt(102.0).X - flying.PositionAt(101.0).X, 10f, "moving at its own speed");
        Near(flying.PositionAt(102.0).X, flying.PositionAt(102.0).X,
             "and asking twice gives the same answer");

        // Spent on contact rather than sweeping through a line of people, which is
        // what makes standing in front of somebody a real defence.
        var field = new ProjectileField();
        var standing = new Dummy { CombatId = 3, CombatName = "standing", CombatPosition = new Vector3(20f, 0f, 0f) };
        var behind = new Dummy { CombatId = 4, CombatName = "behind", CombatPosition = new Vector3(20.5f, 0f, 0f) };
        var candidates = new List<ICombatant> { standing, behind };

        field.Add(flying);
        List<ProjectileHit> hits = field.Advance(102.0, _ => candidates);
        Check(hits.Count == 1, "a projectile hits one thing, not everything it overlaps");
        Check(ReferenceEquals(hits[0].Target, standing), "and it is the one it actually reached");

        Check(field.Advance(102.05, _ => candidates).Count == 0, "a spent projectile hits nothing more");
        Check(field.Count == 0, "and is swept away afterwards, not during");

        // Out of range with nobody in the way.
        var missing = new ProjectileField();
        missing.Add(new ProjectileInstance
        {
            Id = 2, Definition = shot, Origin = Vector3.Zero, Direction = Vector3.Right,
            SpawnedAt = 0.0, ExpiresAt = shot.Lifetime,
        });
        Check(missing.Advance(99.0, _ => candidates).Count == 0, "a projectile past its range hits nobody");
        Check(missing.Count == 0, "and stops existing");

        // -- the sweep itself --------------------------------------------
        var sweep = new Ability
        {
            Id = "sweep", DisplayName = "Sweep",
            Shape = TelegraphShape.Cone, Origin = AbilityOrigin.FromCasterTowardAim,
            Radius = 20f, ConeAngleDegrees = 20f,
            ChannelSeconds = 4f, ChannelTickInterval = 0.5f, ChannelRotationDegrees = 90f,
        };

        Check(sweep.IsChannelled, "a positive channel length makes it a channel");
        Check(!new Ability().IsChannelled, "and an ordinary ability is not one");

        var channel = new ChannelInstance
        {
            Id = 1, Ability = sweep,
            StartDirection = Vector3.Forward,
            RotationRate = Mathf.DegToRad(sweep.ChannelRotationDegrees),
            StartAt = 0.0, EndsAt = 4.0, NextTickAt = 0.0,
        };

        // Computed from elapsed time, so it cannot drift with the frame rate.
        Near(Mathf.RadToDeg(Vector3.Forward.AngleTo(channel.DirectionAt(1.0))), 90f,
             "a sweep turns at the rate it says", 0.01f);
        Near(Mathf.RadToDeg(Vector3.Forward.AngleTo(channel.DirectionAt(2.0))), 180f,
             "and keeps turning", 0.01f);

        var channels = new ChannelField();
        var finished = new List<ChannelInstance>();
        channels.Add(channel);

        Check(channels.Advance(0.0, finished).Count == 1, "a channel fires on its first tick");
        Check(channels.Advance(0.1, finished).Count == 0, "and not again before its interval elapses");
        Check(channels.Advance(0.6, finished).Count == 1, "and again once it has");
        Check(channels.Advance(5.0, finished).Count == 0, "and nothing after it ends");
        Check(finished.Count == 1 && channels.Count == 0, "reporting it finished and leaving nothing behind");

        // Interruptible, which is what makes holding a Rebuke worth it.
        var victim = new Dummy { CombatId = 7, CombatName = "caster" };
        var live = new ChannelField();
        live.Add(new ChannelInstance
        {
            Id = 2, Ability = sweep, Owner = victim,
            StartDirection = Vector3.Forward, StartAt = 0.0, EndsAt = 10.0, NextTickAt = 0.0,
        });

        Check(live.IsChannelling(victim), "a channelling caster reports as busy");
        Check(live.CancelFor(victim).Count == 1, "and can be interrupted");
        Check(!live.IsChannelling(victim), "after which it is not channelling");
        live.Advance(0.1, finished);
        Check(live.Count == 0, "and the cancelled channel is gone");
    }

    // -- the shape of a kit ----------------------------------------------

    /// <summary>
    /// Twelve buttons that were all six-second nukes would be twelve buttons and
    /// one decision. The counts AND the cooldown bands are asserted, because a
    /// kit does not lose its shape in one commit -- it loses it by somebody
    /// shaving a defensive from 40 seconds to 12 and nobody noticing that it is
    /// now part of the rotation.
    /// </summary>
    private static void KitShape()
    {
        static (float Low, float High) Band(AbilityRole role) => role switch
        {
            AbilityRole.Rotational => (0f, 12f),
            AbilityRole.Situational => (12f, 45f),
            AbilityRole.Defensive => (30f, 120f),
            _ => (90f, 240f),
        };

        foreach (HeroClass hero in System.Enum.GetValues<HeroClass>())
        {
            var kit = PlayerKit.For(hero);
            var counts = new Dictionary<AbilityRole, int>();
            foreach (Ability ability in kit)
                counts[ability.Role] = counts.GetValueOrDefault(ability.Role) + 1;

            Check(kit.Count >= 10 && kit.Count <= 13, $"{hero} has 10-13 abilities (has {kit.Count})");
            Check(kit.Count <= Player.Bindings.AbilitySlots, $"{hero}'s kit fits on the keyboard");

            int rotational = counts.GetValueOrDefault(AbilityRole.Rotational);
            Check(rotational is >= 5 and <= 6, $"{hero} has 5-6 rotational abilities (has {rotational})");
            Check(counts.GetValueOrDefault(AbilityRole.Situational) == 3, $"{hero} has 3 situational tools");
            Check(counts.GetValueOrDefault(AbilityRole.Defensive) == 2, $"{hero} has 2 defensive cooldowns");
            Check(counts.GetValueOrDefault(AbilityRole.Ultimate) == 1, $"{hero} has exactly one ultimate");

            foreach (Ability ability in kit)
            {
                (float low, float high) = Band(ability.Role);
                Check(ability.Cooldown >= low && ability.Cooldown <= high,
                      $"{hero} {ability.DisplayName} is {ability.Role} on a {ability.Cooldown}s cooldown, outside {low}-{high}s");
            }
        }
    }

    // -- controls --------------------------------------------------------

    private static void Controls()
    {
        Player.Bindings.SaveEnabled = false;

        try
        {
            // Defaults, deterministically -- not whatever this machine's player
            // has remapped, which would make the collision check flap.
            Player.Bindings.ResetToDefaults();

            foreach (string action in Player.Bindings.All)
                if (!InputMap.HasAction(action)) Check(false, $"{action} is registered");

            // THE BUG THIS PREVENTS: W used to both walk you forward and cast
            // slot 2, because the two claims on that key had nowhere to meet.
            // Compared as InputMap events rather than printed keycaps, since a
            // headless DisplayServer has no keyboard layout to name them with.
            int clashes = 0;
            string firstClash = "none";
            foreach (string action in Player.Bindings.All)
            {
                foreach (InputEvent bound in InputMap.ActionGetEvents(action))
                {
                    foreach (string other in Player.Bindings.All)
                    {
                        if (other == action || !InputMap.ActionHasEvent(other, bound)) continue;
                        if (clashes++ == 0) firstClash = $"{action} and {other}";
                    }
                }
            }

            Check(clashes == 0, $"no default key is bound to two actions (first clash: {firstClash})");

            // Rebinding takes the key away from whoever held it, and says so.
            var q = new InputEventKey { PhysicalKeycode = Key.Q };
            bool rebound = Player.Bindings.Rebind(Player.Bindings.MoveUp, q, out string displaced);
            Check(rebound, "a key can be rebound");
            Check(displaced == Player.Bindings.Ability(2), $"and reports who lost it (got '{displaced ?? "nobody"}')");
            Check(InputMap.ActionHasEvent(Player.Bindings.MoveUp, q), "the new owner holds the key");
            Check(!InputMap.ActionHasEvent(Player.Bindings.Ability(2), q), "and the old owner does not");

            // THE POINT. Keybinds are per-player; the fingerprint gates ranked
            // submission. If remapping moved the hash, everyone who touched the
            // options screen would be refused from the leaderboard -- the same
            // failure as hashing numbers in the ambient locale.
            string before = Session.ContentHash.Compute();
            Check(before.Length > 0, "the fingerprint is actually computed, so this comparison means something");
            Player.Bindings.Rebind(Player.Bindings.Ability(0), new InputEventKey { PhysicalKeycode = Key.Z }, out _);
            string after = Session.ContentHash.Compute();
            Check(before == after, "remapping a key leaves the ranked content fingerprint alone");
        }
        finally
        {
            Player.Bindings.ResetToDefaults();
            Player.Bindings.SaveEnabled = true;
        }
    }

    // -- submitting runs -------------------------------------------------

    private static void Submission()
    {
        // A judgement about the payload never changes, so retrying one forever
        // would block every run behind it.
        Check(!SubmissionPolicy.ShouldRetry(400), "a malformed run is not retried");
        Check(!SubmissionPolicy.ShouldRetry(409), "a conflicting run is not retried");
        Check(!SubmissionPolicy.ShouldRetry(422), "a rejected run is not retried");
        Check(!SubmissionPolicy.ShouldRetry(201), "an accepted run is not retried");

        // These all leave hope.
        Check(SubmissionPolicy.ShouldRetry(500), "a server fault is retried");
        Check(SubmissionPolicy.ShouldRetry(503), "an unavailable backend is retried");
        Check(SubmissionPolicy.ShouldRetry(408), "a timeout is retried");
        Check(SubmissionPolicy.ShouldRetry(429), "rate limiting is retried");

        // A wrong token is somebody's configuration mistake. Discarding every run
        // played before they noticed would be the wrong way to report it.
        Check(SubmissionPolicy.ShouldRetry(401), "a bad credential is retried, not thrown away");
        Check(SubmissionPolicy.ShouldRetry(403), "a forbidden credential is retried too");

        // Backoff has to actually grow. It did not: the attempt counter was reset
        // on every dequeue, so "exponential" was a flat two seconds forever.
        Near((float)SubmissionPolicy.BackoffFor(1), 2f, "the first retry is prompt");
        Near((float)SubmissionPolicy.BackoffFor(2), 4f, "the second waits longer");
        Near((float)SubmissionPolicy.BackoffFor(3), 8f, "and it keeps doubling");

        for (int attempt = 1; attempt < 8; attempt++)
            Check(SubmissionPolicy.BackoffFor(attempt + 1) >= SubmissionPolicy.BackoffFor(attempt),
                  $"backoff never shrinks between attempts {attempt} and {attempt + 1}");

        Near((float)SubmissionPolicy.BackoffFor(99), (float)SubmissionPolicy.MaxBackoffSeconds,
             "and is capped so it stays plausible");
    }

    // -- the content fingerprint -----------------------------------------

    private static void Fingerprint()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            var english = new System.Globalization.CultureInfo("en-US");
            var german = new System.Globalization.CultureInfo("de-DE");

            System.Globalization.CultureInfo.CurrentCulture = german;
            string decimalHere = 0.15.ToString();

            // If ICU is unavailable the cultures collapse and this test would pass
            // without proving anything, so say so rather than report a false green.
            Check(decimalHere == "0,15",
                  $"the test locale really does format differently (got '{decimalHere}')");

            string underGerman = ContentHash.Compute();

            System.Globalization.CultureInfo.CurrentCulture = english;
            string underEnglish = ContentHash.Compute();

            // Appending a float used the ambient culture, so identical builds
            // produced different fingerprints and a ladder rejected runs based on
            // where the server happened to be running.
            Check(underGerman == underEnglish,
                  $"the fingerprint is the same in every locale ({underGerman} vs {underEnglish})");

            Check(underEnglish == ContentHash.Current, "and matches the one computed at startup");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
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
