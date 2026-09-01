using System.Collections.Generic;
using Godot;

namespace Wipebound.Combat;

/// <summary>
/// Every status the game knows, by id.
///
/// A registry exists because the wire only carries ids -- a client receiving
/// "crippled" has to turn that back into the numbers. Definitions live here in
/// code for the same reason the encounter does: a short list of named modifiers
/// is easier to read and diff than a directory of .tres files. Registering
/// inspector-authored ones later is a call to Register.
///
/// COLOUR IS A CONTRACT here too, and it is the inverse of the telegraph palette
/// because the question is different: on the ground red means "leave", on your
/// own buff bar red means "this is happening to you".
/// </summary>
public static class StatusLibrary
{
    private static readonly Dictionary<string, StatusEffect> ById = new();

    public const string Crippled = "crippled";
    public const string Haste = "haste";
    public const string Warded = "warded";
    public const string Sundered = "sundered";
    public const string Burning = "burning";
    public const string Silenced = "silenced";
    public const string Detonation = "detonation";
    public const string Hunted = "hunted";
    public const string Bastion = "bastion";
    public const string Empowered = "empowered";
    public const string Rejuvenating = "rejuvenating";

    static StatusLibrary()
    {
        // The defensive-slot shield. Warded is what a rotational button hands
        // out; this is what a 60-second cooldown buys, and the gap between the
        // two numbers is the reason both slots exist.
        Register(new StatusEffect
        {
            Id = Bastion, DisplayName = "Bastion", Duration = 8f,
            Beneficial = true, Tint = new Color("818cf8"),
            AbsorbAmount = 220f,
        });

        Register(new StatusEffect
        {
            Id = Empowered, DisplayName = "Empowered", Duration = 10f,
            Beneficial = true, Tint = new Color("fbbf24"),
            DamageDealtMultiplier = 1.5f,
        });

        // Healing over time rather than a lump: it rewards pre-casting before the
        // damage lands instead of reacting after it.
        Register(new StatusEffect
        {
            Id = Rejuvenating, DisplayName = "Rejuvenating", Duration = 9f,
            Beneficial = true, Tint = new Color("4ade80"),
            TickInterval = 1.5f,
            OnTick = new Godot.Collections.Array<AbilityEffect> { new HealEffect { Amount = 14f } },
        });

        Register(new StatusEffect
        {
            Id = Crippled, DisplayName = "Crippled", Duration = 4f,
            Beneficial = false, Tint = new Color("f87171"),
            MoveSpeedMultiplier = 0.55f,
        });

        Register(new StatusEffect
        {
            Id = Haste, DisplayName = "Haste", Duration = 4f,
            Beneficial = true, Tint = new Color("4ade80"),
            MoveSpeedMultiplier = 1.45f,
        });

        // A shield rather than a mitigation multiplier: it spends itself and
        // disappears, which is a decision about WHEN to use it rather than a
        // passive discount on every hit for four seconds.
        Register(new StatusEffect
        {
            Id = Warded, DisplayName = "Warded", Duration = 6f,
            Beneficial = true, Tint = new Color("38bdf8"),
            AbsorbAmount = 45f,
        });

        // Stacks, so the raid is rewarded for keeping it up rather than reapplying
        // it once. Three stacks is 1.2^3 = 1.73x incoming damage.
        Register(new StatusEffect
        {
            Id = Sundered, DisplayName = "Sundered", Duration = 10f,
            Beneficial = false, Tint = new Color("fb923c"),
            Stacking = StackRule.Stack, MaxStacks = 3,
            DamageTakenMultiplier = 1.2f,
        });

        // Behaviour over time reuses the ability effect hierarchy rather than
        // inventing a second one.
        Register(new StatusEffect
        {
            Id = Burning, DisplayName = "Burning", Duration = 6f,
            Beneficial = false, Tint = new Color("f97316"),
            TickInterval = 1f,
            // Per caster, so each player's burn is their own and credits them
            // rather than one player's reapplication stealing another's.
            Scope = StatusScope.PerSource,
            OnTick = new Godot.Collections.Array<AbilityEffect> { new DamageEffect { Amount = 7f } },
        });

        Register(new StatusEffect
        {
            Id = Silenced, DisplayName = "Silenced", Duration = 2.5f,
            Beneficial = false, Tint = new Color("c084fc"),
            Silenced = true,
        });

        RegisterDetonation();

        // Purely a marker: no modifiers at all. It exists so a fixating add is
        // LEGIBLE -- to the person being chased, and to everyone who could help.
        // An add that silently picks someone is confusing rather than tense.
        //
        // Not dispellable: cleansing it would make the mechanic vanish rather than
        // be answered, and being chased is the thing you are meant to answer.
        Register(new StatusEffect
        {
            Id = Hunted, DisplayName = "Hunted", Duration = 5f,
            Beneficial = false, Tint = new Color("fbbf24"),
            Dispellable = false,

            // Per hunter, so two adds chasing one player hold separate marks and
            // one losing interest does not clear the other's.
            Scope = StatusScope.PerSource,
        });
    }

    private static void RegisterDetonation()
    {
        // The mechanic that only exists because statuses can act on expiry: you are
        // carrying a bomb, and the answer is to be somewhere else when it lands.
        // Not dispellable -- removing it would delete the decision.
        Register(new StatusEffect
        {
            Id = Detonation, DisplayName = "Detonation", Duration = 6f,
            Beneficial = false, Tint = new Color("f43f5e"),
            Dispellable = false,
            ExpireRadius = 9f,
            AreaAffects = TargetFilter.Enemies,
            OnExpire = new Godot.Collections.Array<AbilityEffect> { new DamageEffect { Amount = 55f } },
        });
    }

    public static void Register(StatusEffect definition) => ById[definition.Id] = definition;

    /// Every registered status, so the content fingerprint can cover them.
    public static System.Collections.Generic.IEnumerable<StatusEffect> All => ById.Values;

    public static StatusEffect Get(string id)
        => id is not null && ById.TryGetValue(id, out StatusEffect definition) ? definition : null;
}
