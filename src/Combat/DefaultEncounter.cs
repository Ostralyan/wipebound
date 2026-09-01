using Godot;
using Godot.Collections;

namespace Wipebound.Combat;

/// <summary>
/// The starting fight, built in code.
///
/// Everything here is ordinary Resource objects, so the same encounter can be
/// authored as .tres files in the inspector and dropped onto Boss.Phases instead
/// -- these classes are [GlobalClass] precisely so that path is open. It lives in
/// code for now because reading a fight as a list of named mechanics teaches the
/// data model better than a serialised blob does.
///
/// COLOUR IS A CONTRACT. Players learn it in the first thirty seconds and then
/// trust it for the rest of the game, so it has to hold everywhere:
///
///     red    -- damage. Get out.
///     blue   -- soak. Somebody get IN, or everyone pays.
///     amber  -- stack. Everybody get in, together.
///
/// A red circle that turns out to be a soak is worse than no telegraph at all.
/// </summary>
public static class DefaultEncounter
{
    private static readonly Color Danger = new("f04a34");
    private static readonly Color Soak = new("38bdf8");
    private static readonly Color Stack = new("fbbf24");

    public static Array<BossPhase> Build()
    {
        // Built once and SHARED between phases on purpose: cooldowns are tracked
        // per ability instance, so a mechanic that survives a phase change keeps
        // its timer instead of being free to fire again immediately.

        var crater = new Ability
        {
            Id = "crater",
            DisplayName = "Crater",
            Shape = TelegraphShape.Circle,
            Radius = 9f,
            CastSeconds = 2.6f,
            Cooldown = 8f,
            AiTargeting = AiTargeting.RandomEnemy,
            Origin = AbilityOrigin.AtAimPoint,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 26f } },
        };

        var sunder = new Ability
        {
            Id = "sunder",
            DisplayName = "Sunder",
            Shape = TelegraphShape.Cone,
            Radius = 20f,
            ConeAngleDegrees = 80f,
            CastSeconds = 2.0f,
            Cooldown = 13f,
            AiTargeting = AiTargeting.NearestEnemy,
            Origin = AbilityOrigin.FromCasterTowardAim,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect>
            {
                new DamageEffect { Amount = 30f },
                new KnockbackEffect { Distance = 9f, TravelSeconds = 0.35f },
                new ApplyStatusEffect { StatusId = StatusLibrary.Crippled },
            },
        };

        // Inverted danger: the raid is punished for leaving this one EMPTY, which
        // turns a dodge into a decision somebody has to announce.
        var beacon = new Ability
        {
            Id = "beacon",
            DisplayName = "Beacon",
            Shape = TelegraphShape.Circle,
            Radius = 4.5f,
            CastSeconds = 3.2f,
            Cooldown = 16f,
            AiTargeting = AiTargeting.FarthestEnemy,
            Origin = AbilityOrigin.AtAimPoint,
            TelegraphColor = Soak,
            Effects = new Array<AbilityEffect>
            {
                new SoakEffect { RequiredSoakers = 1, DamagePerSoaker = 22f, UnsoakedDamage = 65f },
            },
        };

        // A safe hole in the middle instead of outside it -- the same dodge run
        // backwards, which is why donuts pair well with circles.
        var collapse = new Ability
        {
            Id = "collapse",
            DisplayName = "Collapse",
            Shape = TelegraphShape.Donut,
            Radius = 24f,
            InnerRadius = 7f,
            CastSeconds = 2.8f,
            Cooldown = 15f,
            AiTargeting = AiTargeting.ArenaCentre,
            Origin = AbilityOrigin.AtAimPoint,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 42f } },
        };

        var lance = new Ability
        {
            Id = "lance",
            DisplayName = "Lance",
            Shape = TelegraphShape.Rectangle,
            Radius = 44f,
            RectHalfWidth = 3.5f,
            CastSeconds = 2.2f,
            Cooldown = 11f,
            AiTargeting = AiTargeting.RandomEnemy,
            Origin = AbilityOrigin.FromCasterTowardAim,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 38f } },
        };

        // Enough damage to kill anyone taking it alone, so the only answer is to
        // stand on top of each other.
        var convergence = new Ability
        {
            Id = "convergence",
            DisplayName = "Convergence",
            Shape = TelegraphShape.Circle,
            Radius = 5.5f,
            CastSeconds = 3.4f,
            Cooldown = 18f,
            AiTargeting = AiTargeting.RandomEnemy,
            Origin = AbilityOrigin.AtAimPoint,
            TelegraphColor = Stack,
            Effects = new Array<AbilityEffect> { new StackEffect { TotalDamage = 220f } },
        };

        // Only expressible because statuses can act when they expire: the bomb goes
        // on somebody, and the raid has nine metres to spread before it lands.
        var blight = new Ability
        {
            Id = "blight",
            DisplayName = "Blight",
            Shape = TelegraphShape.Circle,
            Radius = 5f,
            CastSeconds = 1.8f,
            Cooldown = 17f,
            AiTargeting = AiTargeting.RandomEnemy,
            Origin = AbilityOrigin.AtAimPoint,
            TelegraphColor = new Color("f43f5e"),
            Effects = new Array<AbilityEffect>
            {
                new ApplyStatusEffect { StatusId = StatusLibrary.Detonation },
            },
        };

        // Ground that stays dangerous, so the arena shrinks as the fight goes on
        // rather than resetting to empty after every dodge.
        var cinders = new Ability
        {
            Id = "cinders",
            DisplayName = "Cinders",
            Shape = TelegraphShape.Circle,
            Radius = 7.5f,
            CastSeconds = 2.0f,
            Cooldown = 14f,
            AiTargeting = AiTargeting.RandomEnemy,
            Origin = AbilityOrigin.AtAimPoint,
            TelegraphColor = new Color("fb7185"),
            Effects = new Array<AbilityEffect>
            {
                new DamageEffect { Amount = 18f },
                new SpawnHazardEffect
                {
                    Definition = new Hazard
                    {
                        Id = "cinders", DisplayName = "Cinders",
                        Duration = 14f, TickInterval = 0.75f,
                        Affects = TargetFilter.Enemies,
                        Tint = new Color("f97316"),
                        OnTick = new Array<AbilityEffect> { new DamageEffect { Amount = 9f } },
                    },
                },
            },
        };

        // Adds. The arena stops being about the boss alone, and the raid has to
        // decide what to kill and what to survive.
        var summon = new Ability
        {
            Id = "summon",
            DisplayName = "Rend the Veil",
            Shape = TelegraphShape.Circle,
            Radius = 6f,
            CastSeconds = 2.4f,
            Cooldown = 26f,
            AiTargeting = AiTargeting.ArenaCentre,
            Origin = AbilityOrigin.AtAimPoint,
            TelegraphColor = new Color("a78bfa"),
            Effects = new Array<AbilityEffect> { new SummonEffect { Count = 3, Spread = 5f, Health = 90f } },
        };

        // Phases are read highest-threshold first. Later phases add mechanics and
        // shorten the gaps -- raising pressure by subtraction of rest, not by
        // inflating numbers.
        return new Array<BossPhase>
        {
            new BossPhase
            {
                Name = "Opening",
                EntersAtHealthPercent = 100f,
                RecoverySeconds = 2.4f,
                Abilities = new Array<Ability> { crater, sunder, beacon },
            },
            new BossPhase
            {
                Name = "Fracture",
                EntersAtHealthPercent = 60f,
                RecoverySeconds = 1.8f,
                Abilities = new Array<Ability> { crater, sunder, beacon, collapse, lance, blight, cinders, summon },
            },
            new BossPhase
            {
                Name = "Wipebound",
                EntersAtHealthPercent = 25f,
                RecoverySeconds = 1.2f,
                Abilities = new Array<Ability> { crater, sunder, beacon, collapse, lance, blight, cinders, summon, convergence },
            },
        };
    }
}
