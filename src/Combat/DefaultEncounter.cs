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

        var crater = new BossAbility
        {
            DisplayName = "Crater",
            Shape = TelegraphShape.Circle,
            Radius = 9f,
            TelegraphSeconds = 2.6f,
            Cooldown = 8f,
            Targeting = TargetingRule.RandomPlayer,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 26f } },
        };

        var sunder = new BossAbility
        {
            DisplayName = "Sunder",
            Shape = TelegraphShape.Cone,
            Radius = 20f,
            ConeAngleDegrees = 80f,
            TelegraphSeconds = 2.0f,
            Cooldown = 13f,
            Targeting = TargetingRule.BossPosition,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect>
            {
                new DamageEffect { Amount = 30f },
                new KnockbackEffect { Distance = 9f, TravelSeconds = 0.35f },
            },
        };

        // Inverted danger: the raid is punished for leaving this one EMPTY, which
        // turns a dodge into a decision somebody has to announce.
        var beacon = new BossAbility
        {
            DisplayName = "Beacon",
            Shape = TelegraphShape.Circle,
            Radius = 4.5f,
            TelegraphSeconds = 3.2f,
            Cooldown = 16f,
            Targeting = TargetingRule.FarthestPlayer,
            TelegraphColor = Soak,
            Effects = new Array<AbilityEffect>
            {
                new SoakEffect { RequiredSoakers = 1, DamagePerSoaker = 22f, UnsoakedDamage = 65f },
            },
        };

        // A safe hole in the middle instead of outside it -- the same dodge run
        // backwards, which is why donuts pair well with circles.
        var collapse = new BossAbility
        {
            DisplayName = "Collapse",
            Shape = TelegraphShape.Donut,
            Radius = 24f,
            InnerRadius = 7f,
            TelegraphSeconds = 2.8f,
            Cooldown = 15f,
            Targeting = TargetingRule.ArenaCenter,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 42f } },
        };

        var lance = new BossAbility
        {
            DisplayName = "Lance",
            Shape = TelegraphShape.Rectangle,
            Radius = 44f,
            RectHalfWidth = 3.5f,
            TelegraphSeconds = 2.2f,
            Cooldown = 11f,
            Targeting = TargetingRule.RandomPlayer,
            TelegraphColor = Danger,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 38f } },
        };

        // Enough damage to kill anyone taking it alone, so the only answer is to
        // stand on top of each other.
        var convergence = new BossAbility
        {
            DisplayName = "Convergence",
            Shape = TelegraphShape.Circle,
            Radius = 5.5f,
            TelegraphSeconds = 3.4f,
            Cooldown = 18f,
            Targeting = TargetingRule.RandomPlayer,
            TelegraphColor = Stack,
            Effects = new Array<AbilityEffect> { new StackEffect { TotalDamage = 220f } },
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
                Abilities = new Array<BossAbility> { crater, sunder, beacon },
            },
            new BossPhase
            {
                Name = "Fracture",
                EntersAtHealthPercent = 60f,
                RecoverySeconds = 1.8f,
                Abilities = new Array<BossAbility> { crater, sunder, beacon, collapse, lance },
            },
            new BossPhase
            {
                Name = "Wipebound",
                EntersAtHealthPercent = 25f,
                RecoverySeconds = 1.2f,
                Abilities = new Array<BossAbility> { crater, sunder, beacon, collapse, lance, convergence },
            },
        };
    }
}
