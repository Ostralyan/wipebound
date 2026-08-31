using Godot;
using System.Collections.Generic;
using Wipebound.Net;

namespace Wipebound.Combat;

/// <summary>One cast in flight. Server-side; clients only ever see the telegraph.</summary>
public sealed class CastInstance
{
    public Ability Ability { get; init; }
    public ICombatant Caster { get; init; }
    public TelegraphArea Area { get; init; }
    public double StartAt { get; init; }
    public double CastEndAt { get; init; }
    public double ResolveAt { get; init; }
}

/// <summary>
/// Runs every cast in the game, whoever started it.
///
/// Casts used to be four fields on Boss -- _casting, _area, _castEndAt, _resolveAt
/// -- which meant a boss could only ever have ONE mechanic in flight. Real
/// encounters violate that constantly: a slow arena-wide winds up while spot
/// mechanics fire underneath it. Reifying a cast as an object and holding a LIST
/// of them removes that ceiling, and makes interrupts a list removal rather than
/// a special case.
///
/// It also means a player ability and a boss mechanic travel the identical path.
/// The only asymmetry left in combat is where the aim point comes from.
/// </summary>
public partial class CombatDirector : Node
{
    public static CombatDirector Instance { get; private set; }

    /// Floor for the resolve grace; the real value also accounts for the worst
    /// connected round trip. See ComputeGrace.
    [Export] public float MinimumResolveGrace { get; set; } = 0.12f;

    /// Fires on every peer when a telegraph appears, so HUDs can draw a cast bar
    /// without knowing anything about the encounter.
    [Signal] public delegate void CastStartedEventHandler(int casterTeam, string casterName, string label,
                                                          double startTime, double endTime, Color color);

    private readonly List<CastInstance> _pending = new();

    private static bool IsServer => NetworkManager.Instance.IsServer;
    private static double Now => NetClock.Instance.ServerTime;

    public override void _Ready()
    {
        Instance = this;
        NetworkManager.Instance.ModeChanged += OnSessionBoundary;
    }

    /// <summary>
    /// Hosting, joining or leaving ends whatever was happening.
    ///
    /// Session lifecycle was previously spread across NetworkManager, Boss and this
    /// class with nobody owning the boundary, so stale casts and boss health
    /// survived a leave-and-rehost. The director owns it because it is the one
    /// thing that already knows about every cast in flight.
    /// </summary>
    private void OnSessionBoundary()
    {
        CancelAll();

        foreach (Node node in GetTree().GetNodesInGroup(Boss.GroupName))
            if (node is Boss boss) boss.ResetForNewSession();

        foreach (Node node in GetTree().GetNodesInGroup(Combatants.GroupName))
            if (node is ICombatant combatant && combatant.Team == Team.Players)
                combatant.OnEncounterReset();
    }

    public bool IsCasting(ICombatant caster)
    {
        foreach (CastInstance cast in _pending)
            if (ReferenceEquals(cast.Caster, caster)) return true;
        return false;
    }

    public void CancelFor(ICombatant caster) => _pending.RemoveAll(cast => ReferenceEquals(cast.Caster, caster));

    public void CancelAll() => _pending.Clear();

    /// <summary>
    /// Start a cast. The caller is responsible for having already validated it --
    /// cost, cooldown, silence. By the time it reaches here it is happening.
    /// </summary>
    public CastInstance Begin(ICombatant caster, Ability ability, Vector3 aimPoint)
    {
        if (!IsServer || ability is null || caster is null) return null;

        double now = Now;
        TelegraphArea area = ability.BuildArea(caster.CombatPosition, aimPoint);
        double castEnd = now + ability.CastSeconds;

        var cast = new CastInstance
        {
            Ability = ability,
            Caster = caster,
            Area = area,
            StartAt = now,
            CastEndAt = castEnd,
            ResolveAt = castEnd + ComputeGrace(ability),
        };

        _pending.Add(cast);

        // castStart and castEnd are ABSOLUTE times on the shared clock, never
        // "starting now". That is what lets a client whose packet arrived late draw
        // an already part-filled telegraph that still finishes on time.
        Rpc(MethodName.ShowTelegraph, area.ToDictionary(), ability.DisplayName,
            now, castEnd, ability.TelegraphColor, (int)caster.Team, caster.CombatName,
            ability.ShowTelegraph);

        return cast;
    }

    /// <summary>
    /// THE TRAILING EDGE.
    ///
    /// Resolving the instant a telegraph visually ends produces the worst bug in the
    /// genre: "I dodged that and still died." With one-way latency L a client only
    /// starts drawing at L, its circle finishes at L + duration, and a player
    /// stepping out right then has that move reach us at L + duration + L.
    /// Resolving at `duration` judges them on a position from before they moved.
    ///
    /// So we wait one full round trip past the visual end. The damage lands about a
    /// tenth of a second late, which nobody perceives, and the same wait guarantees
    /// every client finished seeing the warning.
    ///
    /// An ability with NO telegraph gets no grace at all. Nothing was shown, so
    /// nothing could be dodged, and waiting would only delay the caster's feedback.
    /// </summary>
    private double ComputeGrace(Ability ability)
        => ability.ShowTelegraph
            ? Mathf.Max(MinimumResolveGrace, NetClock.Instance.WorstPeerRtt)
            : 0.0;

    public override void _PhysicsProcess(double delta)
    {
        if (!IsServer || _pending.Count == 0) return;

        double now = Now;

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            CastInstance cast = _pending[i];

            // A caster that died or was freed mid-cast takes its mechanic with it.
            // Without this a dead boss keeps hitting people.
            if (cast.Caster?.Node is null || !GodotObject.IsInstanceValid(cast.Caster.Node) || !cast.Caster.IsAlive)
            {
                _pending.RemoveAt(i);
                continue;
            }

            if (now < cast.ResolveAt) continue;

            _pending.RemoveAt(i);
            Resolve(cast, now);
        }
    }

    private void Resolve(CastInstance cast, double now)
    {
        Ability ability = cast.Ability;
        List<ICombatant> candidates = Combatants.Living(this, cast.Caster, ability.Affects);
        var targets = new List<ICombatant>();

        GD.Print($"[resolve] {cast.Caster.CombatName} :: {ability.DisplayName} at {Flat(cast.Area.Center)}");

        foreach (ICombatant candidate in candidates)
        {
            // CombatPosition is the validated position, never the raw claim. Field()
            // is negative inside and positive outside, in metres -- so this line is
            // also how you check the shader against the maths: stand on the edge and
            // watch the number cross zero.
            float field = cast.Area.Field(candidate.CombatPosition);
            bool hit = field <= 0f;
            if (hit) targets.Add(candidate);

            GD.Print($"[resolve]   {candidate.CombatName} at {Flat(candidate.CombatPosition)} " +
                     $"field={field:+0.00;-0.00}m -> {(hit ? "HIT" : "safe")}");
        }

        var context = new EffectContext
        {
            AbilityName = ability.DisplayName,
            Caster = cast.Caster,
            Area = cast.Area,
            Targets = targets,
            Candidates = candidates,
            Now = now,
        };

        foreach (AbilityEffect effect in ability.Effects)
        {
            if (effect is null) continue;
            GD.Print($"[resolve]   {effect.Describe(context)}");
            effect.Resolve(context);
        }
    }

    /// <summary>
    /// Draw the warning. Runs on every peer including the server, which ignores it
    /// when headless -- the visual has no authority over anything.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ShowTelegraph(Godot.Collections.Dictionary areaData, string label,
                               double castStart, double castEnd, Color color,
                               int casterTeam, string casterName, bool drawFootprint)
    {
        if (drawFootprint)
            TelegraphView.Spawn(this, TelegraphArea.FromDictionary(areaData), castStart, castEnd, color);

        EmitSignal(SignalName.CastStarted, casterTeam, casterName, label, castStart, castEnd, color);
    }

    private static string Flat(Vector3 v) => $"({v.X:0.0}, {v.Z:0.0})";
}
