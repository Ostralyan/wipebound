using Godot;

namespace Wipebound.Combat;

/// <summary>
/// What a projectile looks like. Cosmetic, and only ever cosmetic.
///
/// It is told the flight once -- origin, direction, speed, and the two absolute
/// times it lives between -- and works out where to be from the shared clock
/// every frame. Nothing is streamed. That means a client which dropped packets
/// for half a second does not see projectiles stutter and catch up; it sees them
/// exactly where the server thinks they are, because both are evaluating the
/// same function of the same clock.
///
/// The server decides every hit. If this view is wrong, somebody dies to a
/// bullet they did not see, which is why the flight is a formula rather than a
/// position update that can arrive late.
/// </summary>
public partial class ProjectileView : Node3D
{
    private const string GroupName = "projectile_view";

    /// Off the floor, so it reads as a thing in the air rather than a decal.
    private const float Height = 1.2f;

    public long Id;

    private Vector3 _origin;
    private Vector3 _direction;
    private float _speed;
    private double _spawnedAt;
    private double _expiresAt;

    public static void Spawn(Node context, long id, Vector3 origin, Vector3 direction, float speed,
                             float radius, double spawnedAt, double expiresAt, Color tint)
    {
        Node root = context.GetTree()?.GetFirstNodeInGroup("telegraph_root") ?? context.GetParent();
        if (root is null) return;

        var view = new ProjectileView
        {
            Name = "Projectile",
            Id = id,
            _origin = origin,
            _direction = direction,
            _speed = speed,
            _spawnedAt = spawnedAt,
            _expiresAt = expiresAt,
        };

        view.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = 8, Rings = 4 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = tint,
                EmissionEnabled = true,
                Emission = tint,
                EmissionEnergyMultiplier = 2.2f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });

        view.AddToGroup(GroupName);
        root.AddChild(view);
        view.GlobalPosition = origin + Vector3.Up * Height;
    }

    /// <summary>It hit somebody. The server said so.</summary>
    public static void EndOne(Node context, long id)
    {
        foreach (Node node in context.GetTree()?.GetNodesInGroup(GroupName) ?? new Godot.Collections.Array<Node>())
            if (node is ProjectileView view && view.Id == id)
                view.QueueFree();
    }

    public static void EndAll(Node context)
    {
        foreach (Node node in context.GetTree()?.GetNodesInGroup(GroupName) ?? new Godot.Collections.Array<Node>())
            node.QueueFree();
    }

    public override void _Process(double delta)
    {
        double now = Net.NetClock.Instance?.ServerTime ?? 0.0;

        // Reaching the end of its range is the one despawn a client may decide for
        // itself, because it is a function of the same numbers the server used.
        if (now >= _expiresAt)
        {
            QueueFree();
            return;
        }

        GlobalPosition = _origin + _direction * (_speed * (float)(now - _spawnedAt)) + Vector3.Up * Height;
    }
}
