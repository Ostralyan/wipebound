using Godot;
using Wipebound.Net;

namespace Wipebound.Combat;

/// <summary>
/// The picture of a pending mechanic. Purely client-side and entirely disposable:
/// it holds no state the server needs, and deleting it would not stop the damage.
///
/// It reads its progress off the shared clock rather than counting down from when
/// it was created, so a peer whose packet arrived late spawns an already
/// part-filled telegraph that still finishes at the right moment -- rather than a
/// full-length one that finishes late and lies about the deadline.
/// </summary>
public partial class TelegraphView : Node3D
{
    /// Every live view joins this, so a cancelled cast or an encounter reset can
    /// find and remove its drawing without holding references across the network.
    public const string GroupName = "telegraph_view";

    private const float FlashSeconds = 0.22f;
    private const float GroundOffset = 0.06f;

    /// One Shader compiled for the whole game; each telegraph gets its own cheap
    /// ShaderMaterial pointing at it.
    private static Shader _shader;

    /// Matches the CastInstance or HazardInstance that asked for this drawing.
    public long Id { get; private set; }

    private ShaderMaterial _material;
    private double _castStart;
    private double _castEnd;

    /// Equal to _castEnd for a warning. Later, for a hazard that stays.
    private double _expiresAt;

    public static void Spawn(Node context, long id, TelegraphArea area, double castStart, double castEnd,
                             Color color, double expiresAt = 0.0)
    {
        Node root = context.GetTree().GetFirstNodeInGroup("telegraph_root") ?? context.GetParent();
        if (root is null) return;

        var view = new TelegraphView
        {
            Name = "Telegraph",
            Id = id,
            _castStart = castStart,
            _castEnd = castEnd,
            _expiresAt = Mathf.Max(expiresAt, castEnd),
            _material = new ShaderMaterial { Shader = EnsureShader(), RenderPriority = 1 },
        };

        view.SetAreaUniforms(area, color);

        // The shader works in world space, so the quad only has to cover the shape.
        float extent = area.BoundingRadius + 1.5f;
        view.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(extent * 2f, extent * 2f) },
            MaterialOverride = view._material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        view.AddToGroup(GroupName);
        root.AddChild(view);
        view.GlobalPosition = new Vector3(area.Center.X, GroundOffset, area.Center.Z);
    }

    /// <summary>Take one drawing off the ground -- a cast that was interrupted.</summary>
    public static void EndOne(Node context, long id)
    {
        foreach (Node node in context.GetTree().GetNodesInGroup(GroupName))
            if (node is TelegraphView view && view.Id == id)
                view.QueueFree();
    }

    /// <summary>Take everything off the ground -- an encounter reset.</summary>
    public static void EndAll(Node context)
    {
        foreach (Node node in context.GetTree().GetNodesInGroup(GroupName))
            node.QueueFree();
    }

    private void SetAreaUniforms(TelegraphArea area, Color color)
    {
        _material.SetShaderParameter("shape", (int)area.Shape);
        _material.SetShaderParameter("center", new Vector2(area.Center.X, area.Center.Z));
        _material.SetShaderParameter("facing", area.Facing);
        _material.SetShaderParameter("radius", area.Radius);
        _material.SetShaderParameter("inner_radius", area.InnerRadius);
        _material.SetShaderParameter("half_angle", area.HalfAngle);
        _material.SetShaderParameter("half_width", area.HalfWidth);
        _material.SetShaderParameter("tint", color);
        _material.SetShaderParameter("fill", 0f);
        _material.SetShaderParameter("flash", 0f);
    }

    public override void _Process(double delta)
    {
        double now = NetClock.Instance.ServerTime;
        double span = Mathf.Max(_castEnd - _castStart, 0.0001);

        _material.SetShaderParameter("fill", (float)Mathf.Clamp((now - _castStart) / span, 0.0, 1.0));

        if (now <= _castEnd) return;

        // A hazard sits fully drawn for its whole life rather than counting down.
        if (now < _expiresAt)
        {
            _material.SetShaderParameter("fill", 1f);
            return;
        }

        // Landed, or burnt out. Bloom white and get out of the way.
        double since = now - _expiresAt;
        if (since >= FlashSeconds)
        {
            QueueFree();
            return;
        }

        _material.SetShaderParameter("fill", 1f);
        _material.SetShaderParameter("flash", (float)(1.0 - since / FlashSeconds));
    }

    /// <summary>
    /// The drawn shape. Every branch mirrors TelegraphArea.Field exactly -- same
    /// signed field, same zero crossing, so the rim you see is the boundary the
    /// server tests. Edit one and you must edit the other.
    /// </summary>
    private static Shader EnsureShader() => _shader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, shadows_disabled;

            varying vec3 world_pos;

            uniform int shape;
            uniform vec2 center;
            uniform float facing;
            uniform float radius;
            uniform float inner_radius;
            uniform float half_angle;
            uniform float half_width;
            uniform float fill;
            uniform float flash;
            uniform vec4 tint : source_color;

            // Negative inside, zero on the boundary, positive outside, in metres.
            float telegraph_field(vec2 p) {
                float d = length(p);

                if (shape == 0) {
                    return d - radius;
                }
                if (shape == 1) {
                    return max(d - radius, inner_radius - d);
                }

                vec2 fwd = vec2(-sin(facing), -cos(facing));

                if (shape == 2) {
                    if (d < 0.02) { return -radius; }
                    float c = clamp(dot(p, fwd) / d, -1.0, 1.0);
                    return max(d - radius, (acos(c) - half_angle) * d);
                }

                vec2 rgt = vec2(cos(facing), -sin(facing));
                float along = dot(p, fwd);
                float across = dot(p, rgt);
                return max(along - radius, max(-along, abs(across) - half_width));
            }

            void vertex() {
                world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            }

            void fragment() {
                vec2 p = world_pos.xz - center;
                float f = telegraph_field(p);
                if (f > 0.0) {
                    discard;
                }

                float d = length(p);

                // Bright rim on the boundary: the exact line the server tests.
                float rim = smoothstep(-0.45, 0.0, f);

                // Radial sweep showing how much time is left.
                float filled = 1.0 - smoothstep(radius * fill - 0.2, radius * fill + 0.2, d);

                vec3 body = mix(tint.rgb * 0.40, tint.rgb, filled);
                vec3 col = mix(body, mix(tint.rgb, vec3(1.0), 0.6), rim);

                float alpha = mix(0.20, 0.52, filled);
                alpha = mix(alpha, 0.88, rim);

                // Urgency in the last fifth.
                alpha *= 1.0 + 0.30 * step(0.8, fill) * sin(TIME * 26.0);

                col = mix(col, vec3(1.0), flash * 0.75);
                alpha = mix(alpha, 0.95, flash);

                ALBEDO = col;
                ALPHA = clamp(alpha, 0.0, 1.0) * tint.a;
            }
            """,
    };
}
