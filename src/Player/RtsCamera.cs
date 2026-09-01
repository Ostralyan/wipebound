using Godot;
using Wipebound.Net;

namespace Wipebound.Player;

/// <summary>
/// A Warcraft/Starcraft-style camera rig: fixed angle, free to pan, optionally
/// locked to your hero.
///
/// It is deliberately NOT a child of the hero. Parenting a camera to a physics
/// body couples it to that body's jitter, and an RTS camera has to be able to
/// leave the hero behind anyway.
///
/// Every field below is applied every frame, so you can run the game, open this
/// node in the inspector, and drag the values until the arena reads right. That
/// is the intended way to find your angle -- you will not guess it.
/// </summary>
public partial class RtsCamera : Node3D
{
    [ExportGroup("Angle")]
    [Export(PropertyHint.Range, "-89,-5,0.5")] public float PitchDegrees = -52f;
    [Export(PropertyHint.Range, "-180,180,1")] public float YawDegrees = -45f;

    [ExportGroup("Zoom")]
    [Export] public float Distance = 30f;
    [Export] public float MinDistance = 14f;
    [Export] public float MaxDistance = 60f;
    [Export] public float ZoomStep = 2.5f;

    [ExportGroup("Lens")]
    /// Orthographic reads as a clean tactical board with no skew at the screen
    /// edges; perspective reads closer to SC2. Flip it and see which you prefer.
    [Export] public bool Orthographic = false;
    [Export(PropertyHint.Range, "20,90,1")] public float FieldOfView = 62f;

    [ExportGroup("Feel")]
    [Export] public float FollowSmoothing = 9f;
    [Export] public float PanSpeed = 28f;
    [Export] public float BoundsRadius = 45f;
    [Export] public bool LockedToHero = true;

    /// Off by default: with two game windows on one monitor for testing, edge
    /// scrolling fires constantly and makes the camera unusable. Turn it on once
    /// you are playing fullscreen.
    [Export] public bool EdgeScrollEnabled = false;
    [Export] public int EdgeScrollMargin = 14;

    private Camera3D _camera;
    private Node3D _target;
    private Vector3 _focus;
    private bool _dragging;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera3D");
        _focus = GlobalPosition;

        NetworkManager.Instance.LocalHeroReady += OnLocalHeroReady;
    }

    private void OnLocalHeroReady(Node3D hero)
    {
        _target = hero;
        _focus = hero.GlobalPosition;
        LockedToHero = true;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        ApplyRig();

        Vector3 pan = ReadPanInput();
        if (pan != Vector3.Zero)
        {
            LockedToHero = false;
            _focus += pan * PanSpeed * dt;
        }
        // IsInstanceValid is not enough. A replicated hero stays valid for a
        // while after the engine takes it out of the tree on a server drop, and
        // reading GlobalPosition in that window is an error with our name on it.
        else if (LockedToHero && _target is not null && IsInstanceValid(_target) && _target.IsInsideTree())
        {
            _focus = _focus.Lerp(_target.GlobalPosition, 1f - Mathf.Exp(-FollowSmoothing * dt));
        }

        if (_focus.Length() > BoundsRadius)
            _focus = _focus.Normalized() * BoundsRadius;

        GlobalPosition = _focus;
    }

    /// <summary>Pushes the inspector values into the rig. Cheap, and makes tuning live.</summary>
    private void ApplyRig()
    {
        Rotation = new Vector3(Mathf.DegToRad(PitchDegrees), Mathf.DegToRad(YawDegrees), 0f);
        Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);

        _camera.Position = new Vector3(0f, 0f, Distance);
        _camera.Projection = Orthographic ? Camera3D.ProjectionType.Orthogonal : Camera3D.ProjectionType.Perspective;
        _camera.Fov = FieldOfView;
        _camera.Size = Distance * 0.8f;
        _camera.Near = 0.1f;
        _camera.Far = 600f;
    }

    // Ground-plane basis for the current yaw, so panning follows the screen rather
    // than the world axes.
    private Vector3 GroundForward
    {
        get { float y = Mathf.DegToRad(YawDegrees); return new Vector3(-Mathf.Sin(y), 0f, -Mathf.Cos(y)); }
    }

    private Vector3 GroundRight
    {
        get { float y = Mathf.DegToRad(YawDegrees); return new Vector3(Mathf.Cos(y), 0f, -Mathf.Sin(y)); }
    }

    private Vector3 ReadPanInput()
    {
        // Read as actions, not raw keys. A raw key here is invisible to the
        // rebinding screen AND to the test that proves no key does two jobs --
        // which is exactly how Space came to both re-lock the camera and cast a
        // defensive ability once the kit grew onto the spacebar.
        Vector2 move = Input.GetVector(
            Bindings.CameraLeft, Bindings.CameraRight, Bindings.CameraDown, Bindings.CameraUp);

        if (EdgeScrollEnabled)
        {
            Vector2 mouse = GetViewport().GetMousePosition();
            Vector2 size = GetViewport().GetVisibleRect().Size;
            if (mouse.X <= EdgeScrollMargin)          move.X -= 1f;
            if (mouse.X >= size.X - EdgeScrollMargin) move.X += 1f;
            if (mouse.Y <= EdgeScrollMargin)          move.Y += 1f;
            if (mouse.Y >= size.Y - EdgeScrollMargin) move.Y -= 1f;
        }

        return move == Vector2.Zero
            ? Vector3.Zero
            : (GroundRight * move.X + GroundForward * move.Y).Normalized();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                Distance -= ZoomStep;
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                Distance += ZoomStep;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle:
                _dragging = middle.Pressed;
                break;
            case InputEventMouseMotion motion when _dragging:
                LockedToHero = false;
                _focus += (-GroundRight * motion.Relative.X + GroundForward * motion.Relative.Y)
                          * (Distance * 0.0022f);
                break;
        }

        if (@event.IsActionPressed(Bindings.CameraRecenter)) LockedToHero = true;
    }

    /// <summary>
    /// Screen point under the mouse, projected onto the ground plane.
    ///
    /// In a click-to-move game this is the movement input itself, so it is core
    /// machinery rather than a convenience. A flat arena makes the maths exact and
    /// free; swap in a physics raycast once the ground has height.
    /// </summary>
    /// <summary>
    /// The screen's own forward and right, flattened to the ground.
    ///
    /// WASD is read through this rather than through world axes because the rig
    /// is yawed 45 degrees: "up the screen" is not -Z, and a player pressing W
    /// expects to walk up the screen. Taken from the live Camera3D rather than
    /// this node's yaw export so it stays correct if the rig is ever rotated.
    /// </summary>
    public static bool GroundBasis(Node context, out Vector3 forward, out Vector3 right)
    {
        forward = Vector3.Forward;
        right = Vector3.Right;

        Camera3D camera = context.GetViewport()?.GetCamera3D();
        if (camera is null) return false;

        Basis basis = camera.GlobalTransform.Basis;
        var flatForward = new Vector3(-basis.Z.X, 0f, -basis.Z.Z);
        var flatRight = new Vector3(basis.X.X, 0f, basis.X.Z);

        // Looking straight down leaves no forward in the ground plane.
        if (flatForward.LengthSquared() < 0.0001f || flatRight.LengthSquared() < 0.0001f) return false;

        forward = flatForward.Normalized();
        right = flatRight.Normalized();
        return true;
    }

    public static bool MouseGroundPoint(Node context, out Vector3 point)
    {
        point = Vector3.Zero;

        Viewport viewport = context.GetViewport();
        Camera3D camera = viewport?.GetCamera3D();
        if (camera is null) return false;

        Vector2 mouse = viewport.GetMousePosition();
        Vector3? hit = new Plane(Vector3.Up, 0f)
            .IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));

        if (hit is null) return false;
        point = hit.Value;
        return true;
    }
}
