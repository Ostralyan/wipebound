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
///
/// SHAPE. Every kit is twelve buttons in the same four groups, and the groups are
/// the design rather than a filing convention:
///
///   6 rotational   what you press constantly; short cooldowns are the texture
///   3 situational  answers to a named mechanic -- interrupt, dispel, shove
///   2 defensive    the panic buttons, long enough that spending one is a choice
///   1 ultimate     once or twice a fight
///
/// Twelve abilities that were all six-second nukes would be twelve buttons and
/// one decision. SelfTest enforces the counts AND the cooldown band each role
/// implies, so a kit cannot drift back into that by increments.
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
    //
    // Holds ground. Everything it does is either aimed at a place it wants
    // people to stand, or at moving something out of one.

    private static Array<Ability> Warden() => new()
    {
        Melee("strike", "Strike", 14f),
        Cone("cleave", "Cleave", 8f, 120f, 0f, 5f, 10f, TargetFilter.Enemies, "f0abfc",
             AbilityRole.Rotational, new DamageEffect { Amount = 26f }),
        Line("skewer", "Skewer", 18f, 1.8f, 0.25f, 7f, 14f, TargetFilter.Enemies, "94a3b8",
             AbilityRole.Rotational, new DamageEffect { Amount = 34f }),
        Single("sunder", "Sunder", TargetFilter.Enemies, 8f, 12f, AbilityRole.Rotational,
               new DamageEffect { Amount = 10f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Sundered }),
        Cone("bash", "Bash", 6f, 90f, 0f, 6f, 10f, TargetFilter.Enemies, "fca5a5",
             AbilityRole.Rotational, new DamageEffect { Amount = 18f },
             new ApplyStatusEffect { StatusId = StatusLibrary.Crippled }),

        // The safe play and the sociable play are the same one: it only reaches
        // people standing with you.
        Circle("rally", "Rally", AbilityOrigin.AtCaster, 7f, 0f, 0.2f, 12f, 20f,
               TargetFilter.Allies, "38bdf8", AbilityRole.Rotational,
               new ApplyStatusEffect { StatusId = StatusLibrary.Warded }),

        Cone("rebuke", "Rebuke", 12f, 70f, 0f, 15f, 20f, TargetFilter.Enemies, "fde047",
             AbilityRole.Situational, new InterruptEffect()),
        Cone("shockwave", "Shockwave", 15f, 90f, 0.5f, 18f, 35f, TargetFilter.Enemies, "f0abfc",
             AbilityRole.Situational, new DamageEffect { Amount = 30f },
             new KnockbackEffect { Distance = 8f, TravelSeconds = 0.3f }),

        // Positional threat, not a stat race: Hunted makes adds walk at whoever
        // pulled them, so the answer is where you stand.
        Circle("roar", "Roar", AbilityOrigin.AtCaster, 12f, 0f, 0.3f, 25f, 30f,
               TargetFilter.Enemies, "fb923c", AbilityRole.Situational,
               new ApplyStatusEffect { StatusId = StatusLibrary.Hunted }),

        Circle("bulwark", "Bulwark", AbilityOrigin.AtCaster, 9f, 0f, 0.2f, 40f, 35f,
               TargetFilter.Allies, "818cf8", AbilityRole.Defensive,
               new ApplyStatusEffect { StatusId = StatusLibrary.Bastion },
               new DispelEffect { Count = 1, StripBeneficial = false }),

        // The Warden's escape is forward, which is the class in one button.
        Circle("charge", "Charge", AbilityOrigin.AtAimPoint, 4f, 20f, 0f, 35f, 25f,
               TargetFilter.Enemies, "e2e8f0", AbilityRole.Defensive,
               new DashEffect { Distance = 20f },
               new DamageEffect { Amount = 20f },
               new KnockbackEffect { Distance = 5f, TravelSeconds = 0.25f }),

        Circle("laststand", "Last Stand", AbilityOrigin.AtCaster, 14f, 0f, 0.5f, 120f, 60f,
               TargetFilter.Allies, "c4b5fd", AbilityRole.Ultimate,
               new ApplyStatusEffect { StatusId = StatusLibrary.Bastion },
               new ApplyStatusEffect { StatusId = StatusLibrary.Empowered },
               new DispelEffect { Count = 2, StripBeneficial = false }),
    };

    // -- Ember -----------------------------------------------------------
    //
    // Shapes where the fight happens. Its cooldowns leave ground behind.

    private static Array<Ability> Ember() => new()
    {
        Melee("scorch", "Scorch", 16f),
        Line("lance", "Lance", 26f, 2.2f, 0.35f, 4f, 18f, TargetFilter.Enemies, "60a5fa",
             AbilityRole.Rotational, new DamageEffect { Amount = 32f }),
        Single("bolt", "Ember Bolt", TargetFilter.Enemies, 2f, 10f, AbilityRole.Rotational,
               new DamageEffect { Amount = 22f }),
        Circle("cinder", "Cinder", AbilityOrigin.AtAimPoint, 5f, 24f, 0.3f, 6f, 16f,
               TargetFilter.Enemies, "f97316", AbilityRole.Rotational,
               new DamageEffect { Amount = 24f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Burning }),
        Circle("backdraft", "Backdraft", AbilityOrigin.AtCaster, 8f, 0f, 0.2f, 10f, 22f,
               TargetFilter.Enemies, "fb7185", AbilityRole.Rotational,
               new DamageEffect { Amount = 26f },
               new KnockbackEffect { Distance = 6f, TravelSeconds = 0.25f }),
        Single("kindle", "Kindle", TargetFilter.Enemies, 8f, 14f, AbilityRole.Rotational,
               new DamageEffect { Amount = 8f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Burning },
               new ApplyStatusEffect { StatusId = StatusLibrary.Sundered }),

        Circle("rupture", "Rupture", AbilityOrigin.AtAimPoint, 7f, 22f, 0.8f, 20f, 45f,
               TargetFilter.Enemies, "fbbf24", AbilityRole.Situational,
               new DamageEffect { Amount = 40f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Sundered },
               new ApplyStatusEffect { StatusId = StatusLibrary.Burning }),

        Circle("immolate", "Immolate", AbilityOrigin.AtCaster, 9f, 0f, 0.3f, 16f, 30f,
               TargetFilter.Enemies, "fb7185", AbilityRole.Situational,
               Ground("embers", "Embers", 8f, 0.75f, "f97316", 8f)),

        Cone("flashfire", "Flashfire", 14f, 80f, 0.4f, 30f, 35f, TargetFilter.Enemies, "fda4af",
             AbilityRole.Situational, new DamageEffect { Amount = 30f },
             new ApplyStatusEffect { StatusId = StatusLibrary.Silenced }),

        // No damage, no shield: distance is the whole ability.
        Circle("blink", "Blink", AbilityOrigin.AtAimPoint, 0.5f, 18f, 0f, 30f, 20f,
               TargetFilter.Enemies, "a5b4fc", AbilityRole.Defensive,
               new DashEffect { Distance = 18f, TravelSeconds = 0.12f }),

        Circle("cauterize", "Cauterize", AbilityOrigin.AtCaster, 0.6f, 0f, 0.3f, 45f, 30f,
               TargetFilter.Allies, "818cf8", AbilityRole.Defensive,
               new DispelEffect { Count = 2, StripBeneficial = false },
               new ApplyStatusEffect { StatusId = StatusLibrary.Bastion }),

        Circle("firestorm", "Firestorm", AbilityOrigin.AtAimPoint, 14f, 26f, 1.2f, 120f, 70f,
               TargetFilter.Enemies, "f43f5e", AbilityRole.Ultimate,
               new DamageEffect { Amount = 60f },
               Ground("firestorm_ground", "Firestorm", 12f, 0.5f, "f43f5e", 14f)),
    };

    // -- Verdant ---------------------------------------------------------
    //
    // Every heal here is OtherAllies. The class is deliberately unable to
    // solve its own problem.

    private static Array<Ability> Verdant() => new()
    {
        Single("wither", "Wither", TargetFilter.Enemies, 1.0f, 0f, AbilityRole.Rotational,
               new DamageEffect { Amount = 12f }),
        Single("mend", "Mend", TargetFilter.OtherAllies, 2.5f, 22f, AbilityRole.Rotational,
               new HealEffect { Amount = 45f }),
        Line("thorns", "Thorns", 20f, 2f, 0.3f, 5f, 16f, TargetFilter.Enemies, "a3e635",
             AbilityRole.Rotational, new DamageEffect { Amount = 26f }),
        Single("regrowth", "Regrowth", TargetFilter.OtherAllies, 8f, 20f, AbilityRole.Rotational,
               new HealEffect { Amount = 15f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Rejuvenating }),
        Single("sap", "Sap", TargetFilter.Enemies, 7f, 14f, AbilityRole.Rotational,
               new DamageEffect { Amount = 16f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Sundered }),
        Circle("bloom", "Bloom", AbilityOrigin.AtAimPoint, 8f, 20f, 0.4f, 12f, 40f,
               TargetFilter.OtherAllies, "4ade80", AbilityRole.Rotational,
               new HealEffect { Amount = 30f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Warded }),

        Circle("entangle", "Entangle", AbilityOrigin.AtAimPoint, 6f, 20f, 0.7f, 18f, 35f,
               TargetFilter.Enemies, "a3e635", AbilityRole.Situational,
               new DamageEffect { Amount = 20f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Crippled }),
        Circle("cleanse", "Cleanse", AbilityOrigin.AtAimPoint, 8f, 20f, 0.3f, 25f, 30f,
               TargetFilter.OtherAllies, "67e8f9", AbilityRole.Situational,
               new DispelEffect { Count = 2, StripBeneficial = false }),
        Circle("bramble", "Bramble", AbilityOrigin.AtAimPoint, 7f, 22f, 0.5f, 30f, 35f,
               TargetFilter.Enemies, "65a30d", AbilityRole.Situational,
               Ground("brambles", "Brambles", 9f, 1f, "65a30d", 7f)),

        Circle("shelter", "Shelter", AbilityOrigin.AtAimPoint, 9f, 20f, 0.4f, 40f, 45f,
               TargetFilter.OtherAllies, "818cf8", AbilityRole.Defensive,
               new ApplyStatusEffect { StatusId = StatusLibrary.Bastion }),

        // The Verdant's own panic button moves the problem instead of tanking it.
        Circle("uproot", "Uproot", AbilityOrigin.AtCaster, 10f, 0f, 0.2f, 35f, 30f,
               TargetFilter.Enemies, "d9f99d", AbilityRole.Defensive,
               new KnockbackEffect { Distance = 10f, TravelSeconds = 0.3f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Crippled }),

        Circle("wellspring", "Wellspring", AbilityOrigin.AtAimPoint, 14f, 20f, 1.0f, 120f, 70f,
               TargetFilter.OtherAllies, "34d399", AbilityRole.Ultimate,
               new HealEffect { Amount = 90f },
               new ApplyStatusEffect { StatusId = StatusLibrary.Rejuvenating },
               new DispelEffect { Count = 2, StripBeneficial = false }),
    };

    // -- shapes shared between kits --------------------------------------

    /// The thing you hold down: free, short, and a cone rather than a magic
    /// hitscan, so facing matters and it uses the same footprint code as a boss.
    private static Ability Melee(string id, string name, float damage) => new()
    {
        Id = id, DisplayName = name, Role = AbilityRole.Rotational,
        Shape = TelegraphShape.Cone, Origin = AbilityOrigin.FromCasterTowardAim,
        Radius = 6f, ConeAngleDegrees = 100f,
        CastSeconds = 0f, Cooldown = 0.9f, ManaCost = 0f,
        Affects = TargetFilter.Enemies, ShowTelegraph = false,
        Effects = new Array<AbilityEffect> { new DamageEffect { Amount = damage } },
    };

    /// Single target: the radius is small enough that only the designated
    /// combatant is inside it, so splash is a number rather than a new mechanism.
    private static Ability Single(string id, string name, TargetFilter affects, float cooldown,
                                  float mana, AbilityRole role, params AbilityEffect[] effects) => new()
    {
        Id = id, DisplayName = name, Role = role,
        Shape = TelegraphShape.Circle, Origin = AbilityOrigin.AtTargetUnit,
        Radius = 0.6f, Range = 24f,
        CastSeconds = 0f, Cooldown = cooldown, ManaCost = mana,
        Affects = affects, ShowTelegraph = false,
        Effects = Pack(effects),
    };

    private static Ability Circle(string id, string name, AbilityOrigin origin, float radius,
                                  float range, float cast, float cooldown, float mana,
                                  TargetFilter affects, string tint, AbilityRole role,
                                  params AbilityEffect[] effects) => new()
    {
        Id = id, DisplayName = name, Role = role,
        Shape = TelegraphShape.Circle, Origin = origin, Radius = radius, Range = range,
        CastSeconds = cast, Cooldown = cooldown, ManaCost = mana,
        Affects = affects, TelegraphColor = new Color(tint),
        Effects = Pack(effects),
    };

    private static Ability Cone(string id, string name, float radius, float degrees, float cast,
                                float cooldown, float mana, TargetFilter affects, string tint,
                                AbilityRole role, params AbilityEffect[] effects) => new()
    {
        Id = id, DisplayName = name, Role = role,
        Shape = TelegraphShape.Cone, Origin = AbilityOrigin.FromCasterTowardAim,
        Radius = radius, ConeAngleDegrees = degrees,
        CastSeconds = cast, Cooldown = cooldown, ManaCost = mana,
        Affects = affects, TelegraphColor = new Color(tint),
        Effects = Pack(effects),
    };

    private static Ability Line(string id, string name, float length, float halfWidth, float cast,
                                float cooldown, float mana, TargetFilter affects, string tint,
                                AbilityRole role, params AbilityEffect[] effects) => new()
    {
        Id = id, DisplayName = name, Role = role,
        Shape = TelegraphShape.Rectangle, Origin = AbilityOrigin.FromCasterTowardAim,
        Radius = length, RectHalfWidth = halfWidth,
        CastSeconds = cast, Cooldown = cooldown, ManaCost = mana,
        Affects = affects, TelegraphColor = new Color(tint),
        Effects = Pack(effects),
    };

    /// Ground you leave behind, so a cooldown shapes where the fight happens next.
    private static SpawnHazardEffect Ground(string id, string name, float duration,
                                            float tickInterval, string tint, float damage)
        => new()
        {
            Definition = new Hazard
            {
                Id = id, DisplayName = name,
                Duration = duration, TickInterval = tickInterval,
                Affects = TargetFilter.Enemies, Tint = new Color(tint),
                OnTick = new Array<AbilityEffect> { new DamageEffect { Amount = damage } },
            },
        };

    private static Array<AbilityEffect> Pack(AbilityEffect[] effects)
    {
        var packed = new Array<AbilityEffect>();
        foreach (AbilityEffect effect in effects) packed.Add(effect);
        return packed;
    }
}
