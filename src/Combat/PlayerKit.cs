using Godot;
using Godot.Collections;

namespace Wipebound.Combat;

public enum HeroClass
{
    /// Frontline control: shields the group, interrupts, and shoves things away.
    Warden = 0,

    /// Damage, at range, on cooldowns worth spending well.
    Ember = 1,

    /// Keeps everyone else alive, and CANNOT keep itself alive.
    Verdant = 2,
}

/// <summary>
/// What each class can do.
///
/// Every kit is aimed the same way -- with the cursor -- and what differs is only
/// what the cursor resolves to: a point, a direction, or the person under it.
/// There is no selected target anywhere in this game, so a heal is aimed exactly
/// like a fireball and missing one is possible.
///
/// The Verdant's dependency is the point of having classes at all. Its heals are
/// OtherAllies, so it cannot touch itself, and the only thing keeping it standing
/// is somebody else's shield. Five identical players are one player with five
/// times the health; five players who need each other are a group.
/// </summary>
public static class PlayerKit
{
    public static Array<Ability> For(HeroClass hero) => hero switch
    {
        HeroClass.Ember => Ember(),
        HeroClass.Verdant => Verdant(),
        _ => Warden(),
    };

    public static string NameOf(HeroClass hero) => hero.ToString();

    // -- Warden ----------------------------------------------------------

    private static Array<Ability> Warden() => new()
    {
        Melee("strike", "Strike", 14f),

        // A shield on everyone standing with you, so the safe play and the
        // sociable play are the same one.
        new Ability
        {
            Id = "bulwark", DisplayName = "Bulwark",
            Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtCaster, Radius = 7f,
            CastSeconds = 0.2f, Cooldown = 14f, ManaCost = 25f,
            Affects = TargetFilter.Allies, TelegraphColor = new Color("38bdf8"),
            Effects = new Array<AbilityEffect>
            {
                new ApplyStatusEffect { StatusId = StatusLibrary.Warded },
                new DispelEffect { Count = 1, StripBeneficial = false },
            },
        },

        new Ability
        {
            Id = "rebuke", DisplayName = "Rebuke",
            Shape = TelegraphShape.Cone, Origin = AbilityOrigin.FromCasterTowardAim,
            Radius = 12f, ConeAngleDegrees = 70f,
            CastSeconds = 0f, Cooldown = 15f, ManaCost = 20f,
            Affects = TargetFilter.Enemies, ShowTelegraph = false,
            Effects = new Array<AbilityEffect> { new InterruptEffect() },
        },

        new Ability
        {
            Id = "shockwave", DisplayName = "Shockwave",
            Shape = TelegraphShape.Cone, Origin = AbilityOrigin.FromCasterTowardAim,
            Radius = 15f, ConeAngleDegrees = 90f,
            CastSeconds = 0.5f, Cooldown = 18f, ManaCost = 35f,
            Affects = TargetFilter.Enemies, TelegraphColor = new Color("f0abfc"),
            Effects = new Array<AbilityEffect>
            {
                new DamageEffect { Amount = 30f },
                new KnockbackEffect { Distance = 8f, TravelSeconds = 0.3f },
            },
        },
    };

    // -- Ember -----------------------------------------------------------

    private static Array<Ability> Ember() => new()
    {
        Melee("scorch", "Scorch", 16f),

        new Ability
        {
            Id = "lance", DisplayName = "Lance",
            Shape = TelegraphShape.Rectangle, Origin = AbilityOrigin.FromCasterTowardAim,
            Radius = 26f, RectHalfWidth = 2.2f,
            CastSeconds = 0.35f, Cooldown = 4f, ManaCost = 18f,
            Affects = TargetFilter.Enemies, TelegraphColor = new Color("60a5fa"),
            Effects = new Array<AbilityEffect> { new DamageEffect { Amount = 32f } },
        },

        new Ability
        {
            Id = "rupture", DisplayName = "Rupture",
            Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtAimPoint,
            Radius = 7f, Range = 22f,
            CastSeconds = 0.8f, Cooldown = 20f, ManaCost = 45f,
            Affects = TargetFilter.Enemies, TelegraphColor = new Color("fbbf24"),
            Effects = new Array<AbilityEffect>
            {
                new DamageEffect { Amount = 40f },
                new ApplyStatusEffect { StatusId = StatusLibrary.Sundered },
                new ApplyStatusEffect { StatusId = StatusLibrary.Burning },
            },
        },

        // Ground you leave behind, so an Ember shapes where the fight happens.
        new Ability
        {
            Id = "immolate", DisplayName = "Immolate",
            Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtCaster, Radius = 9f,
            CastSeconds = 0.3f, Cooldown = 16f, ManaCost = 30f,
            Affects = TargetFilter.Enemies, TelegraphColor = new Color("fb7185"),
            Effects = new Array<AbilityEffect>
            {
                new SpawnHazardEffect
                {
                    Definition = new Hazard
                    {
                        Id = "embers", DisplayName = "Embers",
                        Duration = 8f, TickInterval = 0.75f,
                        Affects = TargetFilter.Enemies, Tint = new Color("f97316"),
                        OnTick = new Array<AbilityEffect> { new DamageEffect { Amount = 8f } },
                    },
                },
            },
        },
    };

    // -- Verdant ---------------------------------------------------------

    private static Array<Ability> Verdant() => new()
    {
        // Hover an enemy and press. Aimed exactly like everything else.
        Single("wither", "Wither", TargetFilter.Enemies, 1.0f, 0f,
            new DamageEffect { Amount = 12f }),

        // OtherAllies: the Verdant cannot be the target of its own heal, which is
        // the entire reason the class needs a group.
        Single("mend", "Mend", TargetFilter.OtherAllies, 2.5f, 22f,
            new HealEffect { Amount = 45f }),

        new Ability
        {
            Id = "bloom", DisplayName = "Bloom",
            Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtAimPoint,
            Radius = 8f, Range = 20f,
            CastSeconds = 0.4f, Cooldown = 14f, ManaCost = 40f,
            Affects = TargetFilter.OtherAllies, TelegraphColor = new Color("4ade80"),
            Effects = new Array<AbilityEffect>
            {
                new HealEffect { Amount = 30f },
                new ApplyStatusEffect { StatusId = StatusLibrary.Warded },
            },
        },

        new Ability
        {
            Id = "entangle", DisplayName = "Entangle",
            Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtAimPoint,
            Radius = 6f, Range = 20f,
            CastSeconds = 0.7f, Cooldown = 18f, ManaCost = 35f,
            Affects = TargetFilter.Enemies, TelegraphColor = new Color("a3e635"),
            Effects = new Array<AbilityEffect>
            {
                new DamageEffect { Amount = 20f },
                new ApplyStatusEffect { StatusId = StatusLibrary.Crippled },
            },
        },
    };

    // -- shapes shared between kits --------------------------------------

    /// The thing you hold down: free, short, and a cone rather than a magic
    /// hitscan, so facing matters and it uses the same footprint code as a boss.
    private static Ability Melee(string id, string name, float damage) => new()
    {
        Id = id, DisplayName = name,
        Shape = TelegraphShape.Cone, Origin = AbilityOrigin.FromCasterTowardAim,
        Radius = 6f, ConeAngleDegrees = 100f,
        CastSeconds = 0f, Cooldown = 0.9f, ManaCost = 0f,
        Affects = TargetFilter.Enemies, ShowTelegraph = false,
        Effects = new Array<AbilityEffect> { new DamageEffect { Amount = damage } },
    };

    /// Single target: the radius is small enough that only the designated
    /// combatant is inside it, so splash is a number rather than a new mechanism.
    private static Ability Single(string id, string name, TargetFilter affects,
                                  float cooldown, float mana, AbilityEffect effect) => new()
    {
        Id = id, DisplayName = name,
        Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtTargetUnit,
        Radius = 0.6f, Range = 24f,
        CastSeconds = 0f, Cooldown = cooldown, ManaCost = mana,
        Affects = affects, ShowTelegraph = false,
        Effects = new Array<AbilityEffect> { effect },
    };
}
