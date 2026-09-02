using System.Collections.Generic;
using Godot;
using Wipebound.Combat;

namespace Wipebound.Session;

/// <summary>What happened, at what moment, to whom.</summary>
public enum LogEventType
{
    /// a = absorbed by shields, b = overkill
    Damage = 0,

    /// a = overhealing, which is the difference between healing done and healing
    /// that mattered, and the only reason an HPS number means anything
    Heal = 1,

    /// a = wind-up milliseconds
    CastStart = 2,

    /// a = how many it landed on
    CastResolve = 3,

    /// <summary>
    /// The server's verdict on one combatant against one telegraph.
    ///
    /// a = signed distance from the edge in centimetres, negative inside.
    /// b = 1 if it landed.
    ///
    /// This is the event no client-authored log can produce, and the most useful
    /// one in the set: it is the difference between "took 40 damage" and "was
    /// 1.8m inside the circle when it went off". Avoidable damage stops being an
    /// inference and becomes a measurement.
    /// </summary>
    Judged = 4,

    /// a = stacks, b = remaining milliseconds
    AuraApplied = 5,
    AuraRemoved = 6,

    Interrupt = 7,

    /// a = how many effects were stripped
    Dispel = 8,

    Death = 9,
    Spawn = 10,

    /// a = what it cost. Casting is the only thing that spends, so this is the
    /// resource half of a rotation: what an ability bought, and what for.
    ResourceSpent = 11,

    /// a = phase index. The fight changes shape at thresholds, and a meter that
    /// cannot say which phase a spike happened in cannot explain it.
    PhaseChanged = 12,
}

/// <summary>
/// Everything that happened in one attempt, in the order it happened.
///
/// Written by the SERVER, which is the whole point. A combat log parsed from a
/// file the client wrote can be edited, truncated or simply missing, so sites
/// built on those can rank but cannot arbitrate. This one is produced by the
/// same code that already decides every number -- Combatants.ResolveIncoming and
/// ResolveHealing are the single chokepoint every damage and heal passes
/// through -- so it is evidence rather than testimony, and the ladder can rest
/// on it.
///
/// Plain C# and no scene dependency, so the encoding is reachable by tests
/// without standing up a fight.
/// </summary>
public sealed class CombatLog
{
    /// Bumped when the document's shape changes, so a reader can refuse what it
    /// does not understand rather than guess.
    /// 2 added mana to the tracks and two event types, and renamed the string
    /// table from "abilities" to "names" now that it holds statuses and phases
    /// too. A reader written for 1 would mis-key the lanes, so the number moves.
    public const int FormatVersion = 2;

    /// <summary>
    /// How often the server writes down where everybody is.
    ///
    /// Ten times a second. Positions replicate at about twenty, and a replay
    /// interpolates between samples anyway, so the second ten buys nothing a
    /// viewer can see and doubles the largest part of the document.
    /// </summary>
    public const int PositionIntervalMs = 100;

    /// <summary>
    /// Absent, rather than at the origin.
    ///
    /// A dead or not-yet-spawned actor has no position, and writing 0,0 would put
    /// them in the middle of the arena -- which in this game is where the boss
    /// stands.
    /// </summary>
    public const int NoPosition = -32768;

    /// <summary>
    /// A ceiling on events, because this runs on an unattended server.
    ///
    /// A normal three-minute fight produces a few thousand. Something has gone
    /// wrong long before two hundred thousand, and the answer to that is a
    /// truncated log and a flag saying so, not a server that runs out of memory
    /// during a raid.
    /// </summary>
    public const int MaxEvents = 200_000;

    /// <summary>
    /// A ceiling on samples, and on everything else that grows with time.
    ///
    /// Capping events alone was not bounding anything: the position stream is
    /// ten samples a second for every actor and is by far the largest part of a
    /// document, so an encounter that never ended kept growing after the event
    /// cap stopped it. Thirty-six thousand samples is an hour of fighting, which
    /// is already far beyond any real attempt.
    /// </summary>
    public const int MaxSamples = 36_000;

    /// The same idea for what gets drawn. A channel fires a projectile every
    /// tenth of a second, so this is the one an endless fight reaches first.
    public const int MaxDrawn = 50_000;

    /// x, z, facing, health, mana -- five numbers per actor per sample. All of
    /// them change continuously, which is exactly why they are samples rather
    /// than events: as events they would be the largest stream in the document
    /// by a wide margin.
    public const int LaneStride = 5;

    private readonly List<int[]> _events = new();
    private readonly List<string> _abilities = new();
    private readonly Dictionary<string, int> _abilityIndex = new();
    private readonly Dictionary<int, ActorRecord> _actors = new();
    private readonly Dictionary<int, List<int>> _track = new();

    /// <summary>
    /// What was DRAWN, as opposed to what happened.
    ///
    /// Kept apart from the event stream on purpose. A damage meter reads events
    /// and never touches these; a replay reads both. Forcing footprint geometry
    /// into the event row would have widened every row in the document to carry
    /// eight numbers that only three event types use.
    ///
    /// Each of these is recorded at the moment the server BROADCASTS it to
    /// clients, from the same values, so a replay draws what the players saw
    /// rather than a reconstruction that might disagree with it.
    /// </summary>
    private readonly Godot.Collections.Array _telegraphs = new();
    private readonly Godot.Collections.Array _hazards = new();
    private readonly Godot.Collections.Array _projectiles = new();

    private double _startedAt;
    private double _lastSampleAt;
    private int _samples;

    public bool Recording { get; private set; }
    public bool Truncated { get; private set; }
    public int EventCount => _events.Count;
    public string BossId { get; private set; } = string.Empty;

    private sealed class ActorRecord
    {
        public string Name = string.Empty;
        public string Kind = "unknown";
        public string ClassName = string.Empty;
        public string PlayerId = string.Empty;
        public int Team;
    }

    public void Begin(string bossId, double now)
    {
        _events.Clear();
        _abilities.Clear();
        _abilityIndex.Clear();
        _actors.Clear();
        _track.Clear();
        _telegraphs.Clear();
        _hazards.Clear();
        _projectiles.Clear();

        BossId = bossId;
        _startedAt = now;
        _lastSampleAt = now - PositionIntervalMs / 1000.0;
        _samples = 0;
        Truncated = false;
        Recording = true;
    }

    public void Finish() => Recording = false;

    /// <summary>
    /// Remember who somebody is, once, so events can be integers.
    ///
    /// Names and classes repeat on every line otherwise, and a name is the
    /// longest field in the document.
    /// </summary>
    public bool Knows(int combatId) => _actors.ContainsKey(combatId);

    public void Introduce(double now, ICombatant actor, string kind,
                          string className = "", string playerId = "")
    {
        if (!Recording || actor is null) return;

        _actors[actor.CombatId] = new ActorRecord
        {
            Name = actor.CombatName,
            Kind = kind,
            ClassName = className,
            PlayerId = playerId,
            Team = (int)actor.Team,
        };

        Add(LogEventType.Spawn, At(now), actor, null, string.Empty, 0, 0, 0);
    }

    public void Damage(double now, ICombatant source, ICombatant target, string ability,
                       float landed, float absorbed, float overkill)
        => Add(LogEventType.Damage, At(now), source, target, ability,
               Round(landed), Round(absorbed), Round(overkill));

    public void Heal(double now, ICombatant source, ICombatant target, string ability,
                     float landed, float overheal)
        => Add(LogEventType.Heal, At(now), source, target, ability, Round(landed), Round(overheal), 0);

    public void CastStart(double now, ICombatant caster, string ability, float castSeconds)
        => Add(LogEventType.CastStart, At(now), caster, null, ability, 0, Round(castSeconds * 1000f), 0);

    public void CastResolve(double now, ICombatant caster, string ability, int hits)
        => Add(LogEventType.CastResolve, At(now), caster, null, ability, 0, hits, 0);

    /// <summary>Where somebody stood relative to a telegraph's edge, and whether it caught them.</summary>
    public void Judged(double now, ICombatant caster, ICombatant candidate, string ability,
                       float fieldMetres, bool hit)
        => Add(LogEventType.Judged, At(now), caster, candidate, ability, 0,
               Round(fieldMetres * 100f), hit ? 1 : 0);

    public void Aura(double now, bool applied, ICombatant source, ICombatant target,
                     string status, int stacks, double remainingSeconds)
        => Aura(now, applied, source?.CombatId ?? 0, target, status, stacks, remainingSeconds);

    /// <summary>
    /// By source ID, because that is what a StatusTracker holds.
    ///
    /// The source is not decoration. Burning and Hunted exist separately per
    /// caster, so a reader that keyed auras by target and name alone would let
    /// one caster's instance overwrite another's and one removal clear both.
    /// </summary>
    public void Aura(double now, bool applied, int sourceId, ICombatant target,
                     string status, int stacks, double remainingSeconds)
        => Add(applied ? LogEventType.AuraApplied : LogEventType.AuraRemoved,
               At(now), sourceId, target?.CombatId ?? 0, status, 0, stacks,
               Round((float)remainingSeconds * 1000f));

    public void Interrupt(double now, ICombatant source, ICombatant target, string ability)
        => Add(LogEventType.Interrupt, At(now), source, target, ability, 0, 0, 0);

    public void Dispel(double now, ICombatant source, ICombatant target, string ability, int stripped)
        => Add(LogEventType.Dispel, At(now), source, target, ability, 0, stripped, 0);

    public void Death(double now, ICombatant victim, ICombatant killer, string ability)
        => Add(LogEventType.Death, At(now), killer, victim, ability, 0, 0, 0);

    public void Spent(double now, ICombatant caster, string ability, float amount)
        => Add(LogEventType.ResourceSpent, At(now), caster, null, ability, 0, Round(amount), 0);

    public void Phase(double now, ICombatant boss, string name, int index)
        => Add(LogEventType.PhaseChanged, At(now), boss, null, name, 0, index, 0);

    /// <summary>
    /// Write down where everyone is, if enough time has passed.
    ///
    /// Called every server tick and mostly does nothing, which is why the check
    /// lives here rather than in the caller: one place decides the rate.
    /// </summary>
    public void SamplePositions(double now, IReadOnlyList<ICombatant> present)
    {
        if (!Recording) return;
        if (now - _lastSampleAt < PositionIntervalMs / 1000.0) return;

        if (_samples >= MaxSamples)
        {
            Truncated = true;
            return;
        }

        _lastSampleAt = now;

        // Every known actor gets a slot every sample, present or not, so the
        // arrays stay parallel and a reader needs no index arithmetic. The
        // absent ones compress to nothing.
        foreach (int id in _actors.Keys)
        {
            if (!_track.TryGetValue(id, out List<int> lane))
            {
                lane = new List<int>();
                _track[id] = lane;
            }

            // Backfill anyone introduced late, so every lane is the same length.
            while (lane.Count < _samples * LaneStride)
                for (int slot = 0; slot < LaneStride; slot++)
                    lane.Add(NoPosition);
        }

        foreach ((int id, List<int> lane) in _track)
        {
            ICombatant actor = Find(present, id);

            if (actor is null || !actor.IsAlive)
            {
                for (int slot = 0; slot < LaneStride; slot++) lane.Add(NoPosition);
                continue;
            }

            Vector3 at = actor.CombatPosition;
            lane.Add(Centimetres(at.X));
            lane.Add(Centimetres(at.Z));

            // Facing in tenths of a degree, which fits the same signed 16-bit
            // budget as a position and is finer than anybody can see.
            float yaw = actor.Node is null ? 0f : Mathf.RadToDeg(actor.Node.Rotation.Y);
            lane.Add(Mathf.PosMod(Mathf.RoundToInt(yaw * 10f), 3600));

            // Health per thousand rather than absolute, so a bar can be drawn
            // without also knowing every actor's maximum.
            float fraction = actor.HealthPool is null ? 1f : actor.HealthPool.Fraction;
            lane.Add(Mathf.Clamp(Mathf.RoundToInt(fraction * 1000f), 0, 1000));

            // Absent rather than full for anything without a resource: a boss
            // showing a full mana bar it does not have would be a lie a viewer
            // cannot tell from the truth.
            lane.Add(actor.ResourcePool is null
                ? NoPosition
                : Mathf.Clamp(Mathf.RoundToInt(actor.ResourcePool.Fraction * 1000f), 0, 1000));
        }

        _samples++;
    }

    private static ICombatant Find(IReadOnlyList<ICombatant> present, int id)
    {
        foreach (ICombatant candidate in present)
            if (candidate.CombatId == id)
                return candidate;

        return null;
    }

    // -- what was on the ground -------------------------------------------

    /// <summary>
    /// A telegraph, with the geometry clients were told to draw.
    ///
    /// The ability's NAME is not enough to replay it. The footprint is computed
    /// from the caster's position and where they aimed, by code and tuning data
    /// that live in the game build -- so a website holding only a name could not
    /// reproduce the circle. The resolved shape travels instead.
    /// </summary>
    public void Telegraph(long id, ICombatant caster, string ability,
                          Godot.Collections.Dictionary area, double castStart, double castEnd, Color colour)
    {
        if (!Recording || Full(_telegraphs)) return;

        _telegraphs.Add(new Godot.Collections.Dictionary
        {
            ["id"] = id,
            ["source"] = caster?.CombatId ?? 0,
            ["ability"] = ability,
            ["from_ms"] = At(castStart),
            ["until_ms"] = At(castEnd),
            ["area"] = area,
            ["colour"] = colour.ToHtml(false),
        });
    }

    public void Hazard(long id, ICombatant owner, string name,
                       Godot.Collections.Dictionary area, double from, double until, Color tint)
    {
        if (!Recording || Full(_hazards)) return;

        _hazards.Add(new Godot.Collections.Dictionary
        {
            ["id"] = id,
            ["source"] = owner?.CombatId ?? 0,
            ["name"] = name,
            ["from_ms"] = At(from),
            ["until_ms"] = At(until),
            ["area"] = area,
            ["colour"] = tint.ToHtml(false),
        });
    }

    /// <summary>
    /// One shot, described by the same five numbers a client uses to fly it.
    ///
    /// Position is a function of those, so a replay evaluates the identical
    /// formula and puts the projectile exactly where the player saw it, without
    /// a single per-frame sample being stored.
    /// </summary>
    public void Projectile(long id, ICombatant owner, string ability, Vector3 origin, Vector3 direction,
                           float speed, float radius, double spawnedAt, double expiresAt, Color tint)
    {
        if (!Recording || Full(_projectiles)) return;

        _projectiles.Add(new Godot.Collections.Dictionary
        {
            ["id"] = id,
            ["source"] = owner?.CombatId ?? 0,
            ["ability"] = ability,
            ["from_ms"] = At(spawnedAt),
            ["until_ms"] = At(expiresAt),
            ["x_cm"] = Centimetres(origin.X),
            ["z_cm"] = Centimetres(origin.Z),
            ["dx"] = direction.X,
            ["dz"] = direction.Z,
            ["speed_cms"] = Round(speed * 100f),
            ["radius_cm"] = Round(radius * 100f),
            ["colour"] = tint.ToHtml(false),
        });
    }

    // -- encoding ---------------------------------------------------------

    /// <summary>
    /// The document a reader gets.
    ///
    /// Events are ARRAYS, not objects. Nine thousand rows of
    /// {"timestamp":..,"type":..} spend most of their bytes on the same field
    /// names; the column order is declared once at the top instead, which keeps
    /// the document self-describing without repeating itself.
    /// </summary>
    public Godot.Collections.Dictionary ToDocument(string runId, double endedAt)
    {
        var actors = new Godot.Collections.Array();
        foreach ((int id, ActorRecord record) in _actors)
        {
            actors.Add(new Godot.Collections.Dictionary
            {
                ["id"] = id,
                ["name"] = record.Name,
                ["kind"] = record.Kind,
                ["class"] = record.ClassName,
                ["player_id"] = record.PlayerId,
                ["team"] = record.Team,
            });
        }

        var events = new Godot.Collections.Array();
        foreach (int[] row in _events)
        {
            var encoded = new Godot.Collections.Array();
            foreach (int value in row) encoded.Add(value);
            events.Add(encoded);
        }

        // One table for every name the events refer to -- abilities, statuses
        // and phases alike. They are all strings that repeat, and a second table
        // would only mean a second index space to get wrong.
        var abilities = new Godot.Collections.Array();
        foreach (string ability in _abilities) abilities.Add(ability);

        var lanes = new Godot.Collections.Dictionary();
        foreach ((int id, List<int> lane) in _track)
        {
            var encoded = new Godot.Collections.Array();
            foreach (int value in lane) encoded.Add(value);
            lanes[id.ToString()] = encoded;
        }

        return new Godot.Collections.Dictionary
        {
            ["format"] = FormatVersion,
            ["run_id"] = runId,
            ["boss"] = BossId,
            ["duration_ms"] = At(endedAt),
            ["truncated"] = Truncated,
            ["columns"] = new Godot.Collections.Array
            {
                "t_ms", "type", "source", "target", "ability", "amount", "a", "b",
            },
            ["actors"] = actors,
            ["names"] = abilities,
            ["events"] = events,
            ["tracks"] = new Godot.Collections.Dictionary
            {
                ["interval_ms"] = PositionIntervalMs,
                ["absent"] = NoPosition,
                ["stride"] = new Godot.Collections.Array
                {
                    "x_cm", "z_cm", "facing_decideg", "health_permille", "mana_permille",
                },
                ["samples"] = _samples,
                ["lanes"] = lanes,
            },
            ["telegraphs"] = _telegraphs,
            ["hazards"] = _hazards,
            ["projectiles"] = _projectiles,
        };
    }

    // -- internals --------------------------------------------------------

    private void Add(LogEventType type, int at, ICombatant source, ICombatant target,
                     string ability, int amount, int a, int b)
        => Add(type, at, source?.CombatId ?? 0, target?.CombatId ?? 0, ability, amount, a, b);

    private void Add(LogEventType type, int at, int sourceId, int targetId,
                     string ability, int amount, int a, int b)
    {
        if (!Recording) return;

        if (_events.Count >= MaxEvents)
        {
            Truncated = true;
            return;
        }

        _events.Add(new[]
        {
            at,
            (int)type,
            sourceId,
            targetId,
            Intern(ability),
            amount,
            a,
            b,
        });
    }

    /// <summary>
    /// Whether a drawn-thing list has taken all it is going to.
    ///
    /// Marks the document truncated rather than silently dropping, so a reader
    /// can tell a short fight from one that outran its budget.
    /// </summary>
    private bool Full(Godot.Collections.Array list)
    {
        if (list.Count < MaxDrawn) return false;

        Truncated = true;
        return true;
    }

    private int Intern(string ability)
    {
        if (string.IsNullOrEmpty(ability)) return -1;
        if (_abilityIndex.TryGetValue(ability, out int index)) return index;

        index = _abilities.Count;
        _abilities.Add(ability);
        _abilityIndex[ability] = index;
        return index;
    }

    /// Milliseconds since the fight began. Relative, so a document does not
    /// depend on the server's wall clock to be readable.
    private int At(double now) => Mathf.Max(0, Mathf.RoundToInt((float)(now - _startedAt) * 1000f));

    private static int Round(float value) => Mathf.RoundToInt(value);

    private static int Centimetres(float metres)
    {
        int cm = Mathf.RoundToInt(metres * 100f);
        return Mathf.Clamp(cm, NoPosition + 1, 32767);
    }
}
