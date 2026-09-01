using Godot;
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

    private static readonly string[] Directions =
    {
        Bindings.MoveUp, Bindings.MoveDown, Bindings.MoveLeft, Bindings.MoveRight,
    };

    private readonly RandomNumberGenerator _rng = new();

    private Node3D _hero;
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

        NetworkManager.Instance.LocalHeroReady += hero => _hero = hero;
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

        if (_castAction is not null && now >= _castUntil) ReleaseCast();

        if (now >= _nextCast)
        {
            _nextCast = now + CastEvery;
            Cast();
        }

        if (now >= _nextReport)
        {
            _nextReport = now + ReportEvery;
            GD.Print($"[bot] rtt {Net.NetClock.Instance.Rtt * 1000.0:0}ms " +
                     $"at {_hero.GlobalPosition.X:0.0},{_hero.GlobalPosition.Z:0.0}");
        }
    }

    /// <summary>
    /// Pick a new heading. Sometimes one key, sometimes two, because a diagonal
    /// is the case that would trip an un-normalised speed check and it needs to
    /// happen often rather than by luck.
    /// </summary>
    private void Turn()
    {
        Release();

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

        int slot = (int)(_rng.Randi() % (uint)Bindings.AbilitySlots);
        _castAction = Bindings.Ability(slot);
        _castUntil = Time.GetTicksMsec() / 1000.0 + HoldFor;
        Input.ActionPress(_castAction);
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
