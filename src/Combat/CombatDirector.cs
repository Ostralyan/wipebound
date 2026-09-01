using Godot;
using System.Collections.Generic;
using Wipebound.Net;

namespace Wipebound.Combat;

/// <summary>A patch of ground that keeps hurting. Server-side; clients draw it.</summary>
public sealed class HazardInstance
{
    public Hazard Definition { get; init; }
    public ICombatant Owner { get; init; }
    public TelegraphArea Area { get; init; }
    public double ExpiresAt { get; init; }
    public double NextTickAt { get; set; }
}

/// <summary>One cast in flight. Server-side; clients only ever see the telegraph.</summary>
public sealed class CastInstance
{
    public Ability Ability { get; init; }
    public ICombatant Caster { get; init; }
    public TelegraphArea Area { get; init; }
    public double StartAt { get; init; }
    public double CastEndAt { get; init; }
    public double ResolveAt { get; init; }

    /// Set instead of removing, so a cast can be cancelled while the list that
    /// holds it is being walked.
    public bool Cancelled { get; set; }
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

    /// Fires when a cast ends without resolving.
    [Signal] public delegate void CastInterruptedEventHandler(int casterTeam);

    private readonly List<CastInstance> _pending = new();
    private readonly List<HazardInstance> _hazards = new();

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

    /// <summary>
    /// Drop whatever this caster had in flight. Interrupts and wipes both land here;
    /// with casts as objects, stopping one is a list removal rather than a special
    /// case threaded through whoever was casting.
    /// </summary>
    public void CancelFor(ICombatant caster)
    {
        bool any = false;

        // MARK, never remove. An interrupt reaches this from inside the resolution
        // of another cast, so removing here would mutate the list _PhysicsProcess is
        // walking -- which is exactly the exception the first interrupt produced.
        foreach (CastInstance cast in _pending)
        {
            if (cast.Cancelled || !ReferenceEquals(cast.Caster, caster)) continue;
            cast.Cancelled = true;
            any = true;
        }

        if (any && caster is not null) Rpc(MethodName.CastEnded, (int)caster.Team);
    }

    public void CancelAll()
    {
        foreach (CastInstance cast in _pending) cast.Cancelled = true;
        _hazards.Clear();
    }

    // ---------------------------------------------------------------------
    // Hazards
    // ---------------------------------------------------------------------

    public void SpawnHazard(ICombatant owner, Hazard definition, TelegraphArea area, double now)
    {
        if (!IsServer || definition is null) return;

        GD.Print($"[hazard] {definition.DisplayName} at {Flat(area.Center)} r={area.Radius} for {definition.Duration}s");

        _hazards.Add(new HazardInstance
        {
            Definition = definition,
            Owner = owner,
            Area = area,
            ExpiresAt = now + definition.Duration,
            NextTickAt = now,
        });

        // Drawn as a telegraph that is already full and simply persists: a hazard is
        // not counting down to anything, it is dangerous the whole time.
        Rpc(MethodName.ShowHazard, area.ToDictionary(), now, now + definition.Duration, definition.Tint);
    }

    private void TickHazards(double now)
    {
        for (int i = _hazards.Count - 1; i >= 0; i--)
        {
            HazardInstance hazard = _hazards[i];

            if (now >= hazard.ExpiresAt)
            {
                _hazards.RemoveAt(i);
                continue;
            }

            if (now < hazard.NextTickAt) continue;
            hazard.NextTickAt = now + Mathf.Max(0.05f, hazard.Definition.TickInterval);

            List<ICombatant> candidates = Combatants.Living(this, hazard.Owner, hazard.Definition.Affects);
            var standing = new List<ICombatant>();

            foreach (ICombatant candidate in candidates)
                if (hazard.Area.Contains(candidate.CombatPosition))
                    standing.Add(candidate);

            if (standing.Count == 0) continue;

            // Same spirit as the resolve log: say who the server thinks is standing
            // in it, so "I was not in that" can be checked rather than argued.
            GD.Print($"[hazard] {hazard.Definition.DisplayName} caught {standing.Count} " +
                     $"({string.Join(", ", standing.ConvertAll(c => c.CombatName))})");

            var context = new EffectContext
            {
                AbilityName = hazard.Definition.DisplayName,
                Caster = hazard.Owner,
                Area = hazard.Area,
                Targets = standing,
                Candidates = candidates,
                Now = now,
            };

            foreach (AbilityEffect effect in hazard.Definition.OnTick)
                effect?.Resolve(context);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void ShowHazard(Godot.Collections.Dictionary areaData, double from, double until, Color color)
        => TelegraphView.Spawn(this, TelegraphArea.FromDictionary(areaData), from, from, color, until);

    /// Lets a HUD stop showing a cast bar for something that was interrupted.
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
    private void CastEnded(int casterTeam) => EmitSignal(SignalName.CastInterrupted, casterTeam);

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
        if (!IsServer) return;

        double now = Now;
        TickHazards(now);

        if (_pending.Count == 0) return;

        // Walk forwards over the casts that existed when the frame began, and never
        // remove while walking. Resolving a cast runs arbitrary effects, and those
        // effects can cancel casts or start new ones; a snapshot bound plus
        // mark-and-sweep makes that safe instead of a race with the iterator.
        int existing = _pending.Count;

        for (int i = 0; i < existing; i++)
        {
            CastInstance cast = _pending[i];
            if (cast.Cancelled) continue;

            // A caster that died or was freed mid-cast takes its mechanic with it.
            // Without this a dead boss keeps hitting people.
            if (cast.Caster?.Node is null || !GodotObject.IsInstanceValid(cast.Caster.Node) || !cast.Caster.IsAlive)
            {
                cast.Cancelled = true;
                continue;
            }

            if (now < cast.ResolveAt) continue;

            cast.Cancelled = true;
            Resolve(cast, now);
        }

        _pending.RemoveAll(cast => cast.Cancelled);
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
