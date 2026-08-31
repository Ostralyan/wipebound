using Godot;
using Godot.Collections;

namespace Wipebound.Combat;

/// <summary>
/// What a hero can do. Built in code for the same reason the encounter is: a kit
/// reads better as a short list of named abilities than as a directory of .tres.
/// Assign Hero.Kit in the inspector to override.
/// </summary>
public static class PlayerKit
{
    public static Array<Ability> Build() => new()
    {
        // Slot 0 (Q). Instant, no telegraph, no cost -- the thing you hold down.
        // A cone rather than a magic hitscan, so facing matters and so it goes
        // through exactly the same footprint machinery as a boss mechanic.
        new Ability
        {
            Id = "strike",
            DisplayName = "Strike",
            Shape = TelegraphShape.Cone,
            Origin = AbilityOrigin.FromCasterTowardAim,
            Radius = 6f,
            ConeAngleDegrees = 100f,
            CastSeconds = 0f,
            Cooldown = 0.9f,
            ManaCost = 0f,
            Affects = TargetFilter.Enemies,
            ShowTelegraph = false,
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 14f } },
        },

        // Slot 1 (W). A skillshot: a lane down the arena that has to be aimed, and
        // the first player ability that draws a telegraph -- reusing the boss's
        // machinery wholesale, because a warning is a warning.
        new Ability
        {
            Id = "lance",
            DisplayName = "Lance",
            Shape = TelegraphShape.Rectangle,
            Origin = AbilityOrigin.FromCasterTowardAim,
            Radius = 26f,
            RectHalfWidth = 2.2f,
            CastSeconds = 0.35f,
            Cooldown = 4f,
            ManaCost = 18f,
            Affects = TargetFilter.Enemies,
            TelegraphColor = new Color("60a5fa"),
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 32f } },
        },

        // Slot 2 (E). Defensive, and pointedly NOT self-only: it catches allies
        // standing with you, so the safe play and the sociable play are the same one.
        new Ability
        {
            Id = "aegis",
            DisplayName = "Aegis",
            Shape = TelegraphShape.Circle,
            Origin = AbilityOrigin.AtCaster,
            Radius = 7f,
            CastSeconds = 0.2f,
            Cooldown = 12f,
            ManaCost = 25f,
            Affects = TargetFilter.Allies,
            TelegraphColor = new Color("38bdf8"),
            Effects = new Array<AbilityEffect>
            {
                new ApplyStatusEffect { StatusId = StatusLibrary.Warded },
                new ApplyStatusEffect { StatusId = StatusLibrary.Haste },
            },
        },

        // Slot 3 (R). The big one: ground-targeted, range-limited, and it leaves the
        // boss more vulnerable afterwards, so the raid wants to spend them together.
        new Ability
        {
            Id = "rupture",
            DisplayName = "Rupture",
            Shape = TelegraphShape.Circle,
            Origin = AbilityOrigin.AtAimPoint,
            Radius = 7f,
            Range = 22f,
            CastSeconds = 0.8f,
            Cooldown = 20f,
            ManaCost = 45f,
            Affects = TargetFilter.Enemies,
            TelegraphColor = new Color("fbbf24"),
            Effects = new Array<AbilityEffect>
            {
                new DamageEffect { Amount = 40f },
                new ApplyStatusEffect { StatusId = StatusLibrary.Sundered },
                new ApplyStatusEffect { StatusId = StatusLibrary.Burning },
            },
        },
    };
}
