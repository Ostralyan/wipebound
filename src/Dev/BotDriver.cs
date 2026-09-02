using System.Collections.Generic;
using Godot;
using Wipebound.Combat;
using Wipebound.Net;
using Wipebound.Player;

namespace Wipebound.Dev;

/// <summary>
/// A client that plays badly, but honestly.
///
/// It exists because the movement validator can only be exercised by a client
/// that actually MOVES, and no headless client does. Every network test this
/// project has ever run had a hero standing perfectly still, which is the one
/// case the validator finds easy.
///
/// It drives the real input path -- Input.ActionPress on the same actions a
/// keyboard presses -- rather than writing to Velocity. That matters: the
/// diagonal normalisation, the camera-relative basis and the cooldown gate are
/// all upstream of the thing being tested, and a bot that bypassed them would
/// certify a code path nobody plays.
///
/// AIMING WORKS THE SAME WAY, and that is why nothing in the game changed to
/// support it. Every ability in this game is aimed with the cursor, so a bot
/// that wanted to hit something needed a cursor pointed at it -- not a private
/// channel for handing Hero an aim point, which would have left the one line
/// that actually reads the mouse untested. It moves the mouse instead, with the
/// same event an operating system sends, and Hero cannot tell the difference.
///
/// Before this it aimed wherever a headless cursor happens to sit, cast into
/// empty ground for whole fights, and reported zero damage in every run record
/// -- which is exactly what a bypass would have hidden rather than revealed.
///
/// Honest by construction, therefore. Any overreach the server bills it is a
/// FALSE POSITIVE, which is exactly the measurement worth having under latency.
///
///   godot --headless -- --join 127.0.0.1 --bot --class 1
/// </summary>
public partial class BotDriver : Node
{
    /// Long enough to look like walking somewhere, short enough to keep the
    /// validator's speed-change grace under continuous pressure.
    private const double TurnEvery = 1.4;

    private const double CastEvery = 2.2;

    /// The bot reports the latency it is actually experiencing, because a test
    /// that shapes the network and then cannot see whether the shaping worked
    /// proves nothing either way.
    private const double ReportEvery = 5.0;

    /// Comfortably more than one physics tick at 60Hz, and about as long as a
    /// person leans on a key.
    private const double HoldFor = 0.2;

    /// Close enough that everything in every kit reaches, so bots spread out
    /// again rather than piling onto one point.
    private const float CloseEnough = 5f;

    /// Below this the component is not worth a key: pressing both of a pair
    /// cancels to standing still.
    private const float Deadzone = 0.25f;

    private static readonly string[] Directions =
    {
        Bindings.MoveUp, Bindings.MoveDown, Bindings.MoveLeft, Bindings.MoveRight,
    };

    private readonly RandomNumberGenerator _rng = new();

    /// Typed, because choosing a target means knowing what the ability is FOR.
    private Wipebound.Player.Hero _hero;

    /// What the cursor is currently pointed at.
    private ICombatant _lookingAt;
    private double _nextTurn;
    private double _nextCast;
    private double _nextReport;
    private string _castAction;
    private double _castUntil;
    private int _held = -1;
    private int _heldToo = -1;

    public override void _Ready()
    {
        // Seeded, so two runs of the same scenario are comparable. Varying this
        // is a flag away if a scenario ever needs to be re-rolled.
        _rng.Seed = 20260901;

        NetworkManager.Instance.LocalHeroReady += hero => _hero = hero as Wipebound.Player.Hero;
        GD.Print("[bot] driving this client");
    }

    public override void _Process(double delta)
    {
        if (_hero is null || !GodotObject.IsInstanceValid(_hero) || !_hero.IsInsideTree()) return;

        double now = Time.GetTicksMsec() / 1000.0;

        if (now >= _nextTurn)
        {
            _nextTurn = now + TurnEvery;
            Turn();
        }

        // Kept pointed every frame, not only when casting: facing follows the
        // cursor, so a bot that only aimed at the moment it pressed a key would
        // spend the rest of the fight looking somewhere else.
        if (_lookingAt is null || !Combatants.Placed(_lookingAt) || !_lookingAt.IsAlive)
            _lookingAt = Choose(TargetFilter.Enemies);

        PointAt(_lookingAt);

        if (_castAction is not null && now >= _castUntil) ReleaseCast();

        if (now >= _nextCast)
        {
            _nextCast = now + CastEvery;
            Cast();
        }

        if (now >= _nextReport)
        {
            _nextReport = now + ReportEvery;
            string aim = RtsCamera.MouseGroundPoint(this, out Vector3 ground)
                ? $"{ground.X:0.0},{ground.Z:0.0}"
                : "nowhere";

            GD.Print($"[bot] rtt {Net.NetClock.Instance.Rtt * 1000.0:0}ms " +
                     $"at {_hero.GlobalPosition.X:0.0},{_hero.GlobalPosition.Z:0.0} " +
                     $"aiming {aim} " +
                     $"({_lookingAt?.CombatName ?? "nobody"})");
        }
    }

    /// <summary>
    /// Pick a new heading. Sometimes one key, sometimes two, because a diagonal
    /// is the case that would trip an un-normalised speed check and it needs to
    /// happen often rather than by luck.
    /// </summary>
    /// <summary>
    /// Pick a new heading: toward whatever it is aiming at, if that is far away,
    /// and otherwise somewhere arbitrary.
    ///
    /// Wandering alone was enough to exercise the movement validator, and not
    /// enough to exercise anything else. A Warden's kit is a six metre cone, so
    /// a bot that never closed the distance aimed perfectly and connected with
    /// nothing -- it reported zero damage for entire fights while the melee path
    /// went untested.
    ///
    /// Still honest movement: the same actions, the same speed, the same
    /// normalisation. Only the choice of direction is less random.
    /// </summary>
    private void Turn()
    {
        Release();

        if (Approach()) return;

        _held = (int)(_rng.Randi() % 4);
        Input.ActionPress(Directions[_held]);

        if (_rng.Randf() < 0.6f)
        {
            // Perpendicular, so the pair is a real diagonal rather than two keys
            // that cancel each other out and stand still.
            _heldToo = _held < 2 ? 2 + (int)(_rng.Randi() % 2) : (int)(_rng.Randi() % 2);
            Input.ActionPress(Directions[_heldToo]);
        }
    }

    /// <summary>
    /// Hold the keys that walk toward the current target, in the same
    /// camera-relative frame the game reads them in. False when there is nobody
    /// to walk to, or when already close enough to be swinging.
    /// </summary>
    private bool Approach()
    {
        if (_lookingAt is null || !Combatants.Placed(_lookingAt)) return false;
        if (!RtsCamera.GroundBasis(this, out Vector3 forward, out Vector3 right)) return false;

        Vector3 gap = _lookingAt.Node.GlobalPosition - _hero.GlobalPosition;
        gap.Y = 0f;

        // Inside this and it is already in range of everything it owns; wander
        // instead, so a fight is not three bots standing on one square.
        if (gap.LengthSquared() < CloseEnough * CloseEnough) return false;

        gap = gap.Normalized();
        float sideways = gap.Dot(right);
        float ahead = -gap.Dot(forward);

        // The same mapping Hero reads: Input.GetVector's Y is negative for up.
        if (sideways > Deadzone) Hold(3);
        else if (sideways < -Deadzone) Hold(2);

        if (ahead > Deadzone) Hold(1);
        else if (ahead < -Deadzone) Hold(0);

        return _held >= 0;
    }

    private void Hold(int direction)
    {
        if (_held < 0) _held = direction;
        else _heldToo = direction;

        Input.ActionPress(Directions[direction]);
    }

    /// <summary>
    /// Press a button, and HOLD it.
    ///
    /// This used to press and release inside one _Process call. Hero polls
    /// abilities in _PhysicsProcess, so the release always landed before any
    /// physics tick saw the press, and the bot never cast anything at all. The
    /// evidence was in every run record -- damage_done: 0 for every bot -- and
    /// it was printed and not read.
    /// </summary>
    private void Cast()
    {
        ReleaseCast();

        int slots = Mathf.Min(_hero.Kit.Count, Bindings.AbilitySlots);
        if (slots <= 0) return;

        int slot = (int)(_rng.Randi() % (uint)slots);
        Ability ability = _hero.AbilityAt(slot);
        if (ability is null) return;

        // Point at something this ability would actually want. A heal aimed at
        // the boss is a heal that never lands, which is why healing meters read
        // zero however long the bots played.
        ICombatant wanted = Choose(ability.Affects);
        if (wanted is not null)
        {
            _lookingAt = wanted;
            PointAt(_lookingAt);
        }

        _castAction = Bindings.Ability(slot);
        _castUntil = Time.GetTicksMsec() / 1000.0 + HoldFor;
        Input.ActionPress(_castAction);
    }

    /// <summary>
    /// The nearest thing this filter allows, using the game's own filtering so a
    /// bot can never aim somewhere the rules would not.
    /// </summary>
    private ICombatant Choose(TargetFilter filter)
    {
        List<ICombatant> candidates = Combatants.Living(this, _hero, filter);

        ICombatant best = null;
        float closest = float.MaxValue;

        foreach (ICombatant candidate in candidates)
        {
            if (!Combatants.Placed(candidate)) continue;

            float distance = candidate.CombatPosition.DistanceSquaredTo(_hero.GlobalPosition);
            if (distance >= closest) continue;

            closest = distance;
            best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Move the cursor onto a target, with the event an operating system would
    /// send.
    ///
    /// Not Input.WarpMouse: that asks the display server to move a pointer, and
    /// a headless one has no pointer to move. A parsed motion event goes through
    /// the engine instead, so the viewport updates the position Hero reads and
    /// the whole aim path -- unproject, ray, ground plane, whoever is under the
    /// cursor -- runs exactly as it does for a person. Nothing in the game knows
    /// a bot is playing.
    ///
    /// THE COORDINATE SPACES ARE NOT THE SAME, which is the part that cost an
    /// afternoon. UnprojectPosition answers in VIEWPORT pixels; an arriving
    /// event is in WINDOW pixels and the viewport scales it on the way in. A
    /// headless window reports 64 pixels against a 1280 viewport, so a position
    /// sent unconverted landed twenty times too far out -- the cursor moved, it
    /// simply moved somewhere nothing was standing, and every single-target
    /// ability found nobody under it.
    ///
    /// The viewport's own final transform is that conversion, so it is asked
    /// rather than assumed: on a real window it is identity and this is a no-op.
    /// </summary>
    private void PointAt(ICombatant target)
    {
        if (target?.Node is null || !Combatants.Placed(target)) return;

        Viewport viewport = GetViewport();
        Camera3D camera = viewport?.GetCamera3D();
        if (camera is null) return;

        // The VISIBLE position, for the reason TargetReticle records beside its
        // own copy of this line: CombatPosition is the server's validated one
        // and never advances on a client, so aiming there points at where the
        // target spawned while they run around somewhere else. It reads
        // correctly for a boss, whose CombatPosition IS its transform, which is
        // exactly why the mistake survives a casual test.
        //
        // Flattened, because the production path intersects the ground plane:
        // unprojecting a point a metre in the air and re-projecting it onto y=0
        // lands past the target, every time.
        Vector3 at = target.Node.GlobalPosition;
        Vector2 inViewport = camera.UnprojectPosition(new Vector3(at.X, 0f, at.Z));
        Vector2 inWindow = viewport.GetFinalTransform() * inViewport;

        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Position = inWindow,
            GlobalPosition = inWindow,
        });
    }

    private void ReleaseCast()
    {
        if (_castAction is null) return;
        Input.ActionRelease(_castAction);
        _castAction = null;
    }

    private void Release()
    {
        if (_held >= 0) Input.ActionRelease(Directions[_held]);
        if (_heldToo >= 0) Input.ActionRelease(Directions[_heldToo]);
        _held = -1;
        _heldToo = -1;
    }
}
