using Godot;

namespace Wipebound.Combat.Commands;

/// <summary>
/// "I pressed the button in slot N, aiming here."
///
/// Note what does NOT cross the wire: no damage, no cooldown, no mana figure, no
/// hero id. The client states an intent and the server derives every consequence
/// from its own data.
/// </summary>
public sealed class CastAbilityCommand : ClientCommand
{
    private int _slot;
    private Vector3 _aimPoint;
    private int _targetId;
    private Ability _ability;
    private ICombatant _target;

    /// Sanity bound so a hostile slot index cannot be used to probe anything.
    private const int MaxSlot = 15;

    public override bool Read(Godot.Collections.Dictionary payload)
    {
        if (!payload.TryGetValue("slot", out Variant rawSlot)) return false;
        if (!payload.TryGetValue("aim", out Variant rawAim)) return false;

        // Check the Variant TYPE before converting. Godot's conversions are lenient:
        // casting a String to Vector3 does not throw, it quietly yields zero, which
        // would turn a malformed payload into a perfectly valid cast at the origin.
        if (rawSlot.VariantType != Variant.Type.Int) return false;
        if (rawAim.VariantType != Variant.Type.Vector3) return false;

        _slot = (int)rawSlot;
        _aimPoint = (Vector3)rawAim;

        // Optional: only abilities aimed at a person carry one, and the server
        // decides whether the answer is allowed regardless of what arrives.
        _targetId = payload.TryGetValue("target", out Variant rawTarget) && rawTarget.VariantType == Variant.Type.Int
            ? (int)rawTarget
            : 0;

        if (_slot < 0 || _slot > MaxSlot) return false;

        // Every component, not just the two the footprint ends up using. Y was
        // unchecked, and because DistanceTo consumes all three, a NaN there made the
        // range comparison false while TelegraphArea later discarded Y -- so a
        // modified client could land a ground-targeted ability anywhere on the map.
        return Untrusted.IsFinite(_aimPoint);
    }

    public override bool Validate(CommandContext context, out string reason)
    {
        _ability = context.Hero.AbilityAt(_slot);

        if (!context.Hero.IsAlive) { reason = "dead"; return false; }
        if (_ability is null) { reason = $"no ability in slot {_slot}"; return false; }
        if (context.Hero.Status.Silenced) { reason = "silenced"; return false; }
        if (!context.Hero.IsAbilityReady(_slot, context.Now)) { reason = "on cooldown"; return false; }
        if (!context.Hero.ManaPool.CanAfford(_ability.ManaCost)) { reason = "not enough mana"; return false; }

        // A designated target is untrusted input like everything else: it must
        // exist, be alive, be somebody this ability is allowed to touch, and be
        // close enough. "Heal the enemy boss" and "reach across the map" are both
        // expressible in the payload and neither survives here.
        if (_ability.RequiresTarget)
        {
            _target = Combatants.ById(context.Hero, _targetId);

            if (_target is null || !_target.IsAlive) { reason = "no such target"; return false; }
            if (!Combatants.Matches(_target, context.Hero, _ability.Affects)) { reason = "not a legal target"; return false; }

            float reach = context.Hero.CombatPosition.DistanceTo(_target.CombatPosition);
            if (_ability.Range > 0f && reach > _ability.Range + 1.0f)
            {
                reason = $"target out of range ({reach:0.0}m)";
                return false;
            }
        }

        // Range is measured from the VALIDATED position, never the claimed one.
        // The small tolerance absorbs the sub-tick difference between where the
        // client aimed and where the server thinks it was standing.
        if (_ability.Range > 0f)
        {
            float distance = context.Hero.CombatPosition.DistanceTo(_aimPoint);
            if (distance > _ability.Range + 1.0f) { reason = $"out of range ({distance:0.0}m)"; return false; }
        }

        reason = null;
        return true;
    }

    public override void Execute(CommandContext context)
    {
        context.Hero.ManaPool.TrySpend(_ability.ManaCost);
        context.Hero.StartCooldown(_slot, context.Now);

        CombatDirector.Instance.Begin(context.Hero, _ability, _aimPoint, _targetId);

        // Tell the owner what its real cooldown is, so its button stops guessing.
        context.Hero.AcknowledgeCast(_slot, context.Hero.AbilityReadyAt(_slot));
    }

    /// Called on rejection so the client can clear an optimistic cooldown.
    public void Reject(CommandContext context) => context.Hero.AcknowledgeCast(_slot, 0.0);

    public override string Describe() => $"Cast(slot {_slot} -> {_ability?.DisplayName ?? "?"})";
}
