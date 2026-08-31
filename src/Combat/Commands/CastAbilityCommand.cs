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
    private Ability _ability;

    public override bool Read(Godot.Collections.Dictionary payload)
    {
        if (!payload.ContainsKey("slot") || !payload.ContainsKey("aim")) return false;

        _slot = (int)payload["slot"];
        _aimPoint = (Vector3)payload["aim"];

        // A hostile client can put anything in here, including NaN, which would
        // poison every distance test downstream.
        return !float.IsNaN(_aimPoint.X) && !float.IsNaN(_aimPoint.Z)
               && !float.IsInfinity(_aimPoint.X) && !float.IsInfinity(_aimPoint.Z);
    }

    public override bool Validate(CommandContext context, out string reason)
    {
        _ability = context.Hero.AbilityAt(_slot);

        if (!context.Hero.IsAlive) { reason = "dead"; return false; }
        if (_ability is null) { reason = $"no ability in slot {_slot}"; return false; }
        if (context.Hero.Status.Silenced) { reason = "silenced"; return false; }
        if (!context.Hero.IsAbilityReady(_slot, context.Now)) { reason = "on cooldown"; return false; }
        if (!context.Hero.ManaPool.CanAfford(_ability.ManaCost)) { reason = "not enough mana"; return false; }

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

        CombatDirector.Instance.Begin(context.Hero, _ability, _aimPoint);

        // Tell the owner what its real cooldown is, so its button stops guessing.
        context.Hero.AcknowledgeCast(_slot, context.Hero.AbilityReadyAt(_slot));
    }

    /// Called on rejection so the client can clear an optimistic cooldown.
    public void Reject(CommandContext context) => context.Hero.AcknowledgeCast(_slot, 0.0);

    public override string Describe() => $"Cast(slot {_slot} -> {_ability?.DisplayName ?? "?"})";
}
