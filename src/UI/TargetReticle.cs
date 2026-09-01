using Godot;
using Wipebound.Combat;
using Wipebound.Net;
using Wipebound.Player;

namespace Wipebound.UI;

/// <summary>
/// A ring under whoever the cursor is over.
///
/// Aiming at a person is done by hovering them, which works and is completely
/// invisible: press Mend at the wrong moment and it lands on whoever happened to
/// be nearest the cursor, with nothing on screen to say so beforehand. Healing the
/// wrong ally is the kind of mistake a player should be able to see coming.
///
/// Purely cosmetic and purely local. It resolves the same way the cast does, so
/// what it shows is what would be sent -- but the server still decides, and this
/// has no say in anything.
/// </summary>
public partial class TargetReticle : Node3D
{
    [Export] public Color AllyTint { get; set; } = new("4ade80");
    [Export] public Color EnemyTint { get; set; } = new("f87171");

    /// Matches the pick radius the cast itself uses, so the ring never promises
    /// somebody the ability would not reach for.
    [Export] public float PickRadius { get; set; } = 2.5f;

    private MeshInstance3D _ring;
    private StandardMaterial3D _material;
    private Hero _hero;

    public override void _Ready()
    {
        _ring = GetNode<MeshInstance3D>("Ring");

        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            AlbedoColor = AllyTint,
        };

        _ring.MaterialOverride = _material;
        Visible = false;

        NetworkManager.Instance.LocalHeroReady += node => _hero = node as Hero;
    }

    public override void _Process(double delta)
    {
        if (_hero is null || !IsInstanceValid(_hero) || !_hero.IsAlive)
        {
            Visible = false;
            return;
        }

        if (!RtsCamera.MouseGroundPoint(this, out Vector3 ground))
        {
            Visible = false;
            return;
        }

        // Anyone at all, not only legal targets: seeing the ring on an enemy while
        // holding a heal is exactly the feedback that stops the mistake.
        ICombatant hovered = Combatants.UnderCursor(this, ground, _hero, TargetFilter.All, PickRadius);
        if (hovered is null)
        {
            Visible = false;
            return;
        }

        Visible = true;
        GlobalPosition = hovered.CombatPosition + new Vector3(0f, 0.08f, 0f);
        _material.AlbedoColor = hovered.Team == _hero.Team ? AllyTint : EnemyTint;
    }
}
