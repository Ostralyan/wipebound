using Godot;
using Wipebound.Net;

namespace Wipebound.World;

/// <summary>Entry scene. Wires the world into the network layer and bakes navigation.</summary>
public partial class Main : Node3D
{
    public override void _Ready()
    {
        // Before anything can read an action. Heroes poll movement and abilities
        // through Bindings every physics frame, and Input.GetVector on an action
        // that was never registered is an error rather than a zero.
        Player.Bindings.Install();

        // Synchronous bake so the navmesh is ready before heroes probe for it.
        // Anything you add as a child of NavRegion becomes part of the walkable
        // solve -- drop obstacle meshes in there and heroes start pathing around them.
        var region = GetNode<NavigationRegion3D>("NavRegion");
        region.BakeNavigationMesh(false);
        GD.Print($"[world] navmesh baked: {region.NavigationMesh.GetPolygonCount()} polygons");

        ApplyGridMaterial();

        // What fingerprint is this build? An operator has to put it in the
        // backend's RANKED_CONTENT_HASHES before runs from this server can be
        // ranked, and until now the only way to read it was to finish a run and
        // fish it out of the submission JSON.
        //
        //   wipebound-server.x86_64 --headless -- --content-hash
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--content-hash") >= 0)
        {
            bool verbose = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--verbose-manifest") >= 0;
            GD.Print(verbose ? Session.ContentHash.Manifest() : Session.ContentHash.Compute());
            GetTree().Quit(0);
            return;
        }

        // godot --headless -- --selftest   (exits non-zero on failure, for CI)
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--selftest") >= 0)
        {
            GetTree().Quit(Dev.SelfTest.Run() == 0 ? 0 : 1);
            return;
        }

        NetworkManager.Instance.RegisterWorld(GetNode<Node>("Heroes"));
    }

    /// <summary>
    /// A procedural grid on the ground. This is not decoration: on a flat untextured
    /// plane you cannot judge camera pitch, height or scale at all, and those are
    /// exactly the values you came here to tune.
    /// </summary>
    private void ApplyGridMaterial()
    {
        var shader = new Shader
        {
            Code = """
                shader_type spatial;

                varying vec3 world_pos;

                uniform vec3 base_color : source_color = vec3(0.11, 0.13, 0.16);
                uniform vec3 line_color : source_color = vec3(0.24, 0.29, 0.35);
                uniform float cell = 2.0;

                void vertex() {
                    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                }

                void fragment() {
                    vec2 uv = world_pos.xz / cell;
                    vec2 grid = abs(fract(uv - 0.5) - 0.5) / fwidth(uv);
                    float line = 1.0 - min(min(grid.x, grid.y), 1.0);
                    ALBEDO = mix(base_color, line_color, line);
                    ROUGHNESS = 0.92;
                    SPECULAR = 0.1;
                }
                """,
        };

        GetNode<MeshInstance3D>("NavRegion/Ground/GroundMesh").MaterialOverride =
            new ShaderMaterial { Shader = shader };
    }
}
