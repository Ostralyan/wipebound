using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wipebound.Combat;

/// <summary>One live instance of a StatusEffect on one combatant, from one caster.</summary>
public sealed class ActiveStatus
{
    public StatusEffect Definition { get; init; }

    /// Null on clients, which decode instances without resolving who cast them.
    public ICombatant Source { get; set; }

    /// Survives the wire, so a client can tell two casters' instances apart.
    public int SourceId { get; set; }

    public double ExpiresAt { get; set; }
    public int Stacks { get; set; } = 1;
    public double NextTickAt { get; set; }

    /// Remaining shield, for statuses that absorb. Per instance, because a shield
    /// is spent rather than scaled.
    public float AbsorbRemaining { get; set; }

    public double RemainingAt(double now) => Mathf.Max(0.0, ExpiresAt - now);
}

/// <summary>
/// Every status currently on one combatant, plus the aggregate they add up to.
///
/// INSTANCES ARE PER CASTER. That is the general model: a status declared Shared
/// collapses to a single instance, but a shared-only design can never express
/// "this damage-over-time is mine and should credit me" or "this mark belongs to
/// that player". The narrower behaviour is reachable from the wider one and not
/// the other way round, so the wider one is the substrate.
///
/// Modifiers aggregate over DISTINCT status ids using the largest stack count, so
/// five players applying the same vulnerability do not multiply it five times.
/// Periodic effects run per instance, because those genuinely are each caster's.
/// Shields sum, because two shields really are more shield.
///
/// Aggregates are recomputed on change rather than queried, because they are read
/// every physics frame by movement and every damage application, and change
/// perhaps once a second.
///
/// REPLICATION. Statuses are server-authoritative, but the client needs its own:
/// hero movement is client-authoritative, so a client that does not know it is
/// slowed keeps running at full speed and the server's clamp drags it back, which
/// reads as rubber banding. The set travels as a small encoded string -- Godot's
/// change detection over it is unambiguous, and you can read every live status in
/// the game straight out of a log line. Expiry times are absolute server-clock
/// times, which is what makes a decoded status agree about when it ends.
/// </summary>
public sealed class StatusTracker
{
    private readonly List<ActiveStatus> _active = new();

    public IReadOnlyList<ActiveStatus> Active => _active;

    /// Cached wire form, rebuilt only when the set changes.
    public string Encoded { get; private set; } = "";

    public float MoveSpeedMultiplier { get; private set; } = 1f;
    public float DamageTakenMultiplier { get; private set; } = 1f;
    public float DamageDealtMultiplier { get; private set; } = 1f;
    public float ManaRegenMultiplier { get; private set; } = 1f;
    public float AbsorbRemaining { get; private set; }
    public bool Rooted { get; private set; }
    public bool Silenced { get; private set; }

    public bool Has(string id) => Find(id, null) is not null;

    private ActiveStatus Find(string id, int? sourceId)
    {
        foreach (ActiveStatus status in _active)
        {
            if (status.Definition.Id != id) continue;
            if (sourceId is not null && status.SourceId != sourceId) continue;
            return status;
        }

        return null;
    }

    /// <summary>
    /// Whose statuses these are.
    ///
    /// Set by the combatant that owns the tracker, because a status transition
    /// is only fully described from here: this is the single place that knows
    /// the definition, the source, the resulting stack count, the expiry, and
    /// every way one can leave -- expiry, dispel, a spent shield, death.
    ///
    /// Logging used to live on the effects instead, which is why dispels emitted
    /// no removal at all and per-source instances were flattened. Damage does not
    /// have that problem because it reports from its own chokepoint.
    /// </summary>
    public ICombatant Owner { get; set; }

    /// <summary>
    /// Where transitions are written. Handed in rather than looked up, so this
    /// class can be driven end to end by a test: reaching for a global here
    /// would have meant the only way to check a dispel actually emits removals
    /// was to run a whole fight.
    /// </summary>
    public Session.CombatLog Journal { get; set; }

    /// <summary>
    /// Write down a transition. No-op off the server, where RunRecorder is not
    /// recording, so the client's mirror of this list stays silent.
    /// </summary>
    private void Note(bool applied, ActiveStatus status, double now)
    {
        if (Owner is null || status is null) return;

        Journal?.Aura(
            now, applied, status.SourceId, Owner, status.Definition.DisplayName,
            status.Stacks, applied ? Mathf.Max(status.ExpiresAt - now, 0.0) : 0.0);
    }

    // -- server side ------------------------------------------------------

    public void Apply(StatusEffect definition, ICombatant source, double now)
    {
        if (definition is null) return;

        int sourceId = source?.CombatId ?? 0;
        bool perSource = definition.Scope == StatusScope.PerSource;
        ActiveStatus existing = Find(definition.Id, perSource ? sourceId : null);

        if (existing is null)
        {
            _active.Add(new ActiveStatus
            {
                Definition = definition,
                Source = source,
                SourceId = sourceId,
                ExpiresAt = now + definition.Duration,
                Stacks = 1,
                NextTickAt = now + definition.TickInterval,
                AbsorbRemaining = definition.AbsorbAmount,
            });
        }
        else
        {
            if (definition.Stacking == StackRule.Ignore) return;

            if (definition.Stacking == StackRule.Stack)
                existing.Stacks = Mathf.Min(existing.Stacks + 1, Mathf.Max(1, definition.MaxStacks));

            existing.Source = source;
            existing.SourceId = sourceId;
            existing.ExpiresAt = now + definition.Duration;

            // Refreshing a shield restores it. A half-spent shield that stayed half
            // spent would make reapplying it strictly worse than waiting.
            if (definition.AbsorbAmount > 0f)
                existing.AbsorbRemaining = definition.AbsorbAmount;
        }

        Rebuild();

        // Reported from here rather than from the effect that asked, because the
        // resulting stack count and expiry are decided above and an effect can
        // only guess at them.
        Note(applied: true, Find(definition.Id, definition.Scope == StatusScope.PerSource ? sourceId : null), now);
    }

    public void Remove(string id, double now) => Remove(now, status => status.Definition.Id == id);

    /// <summary>Remove only the instance one particular caster applied.</summary>
    public void Remove(string id, int sourceId, double now)
        => Remove(now, status => status.Definition.Id == id && status.SourceId == sourceId);

    /// <summary>
    /// The one removal shape that is neither expiry, dispel, death nor a spent
    /// shield: something decided to take a status back.
    ///
    /// It goes through here so it reports like the rest. RemoveAll was doing the
    /// mutation directly and saying nothing, so a minion that moved on left its
    /// victim marked as hunted in the replay for the rest of the fight -- the one
    /// path out of the list that the chokepoint did not cover.
    /// </summary>
    private void Remove(double now, System.Predicate<ActiveStatus> match)
    {
        int removed = 0;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!match(_active[i])) continue;

            Note(applied: false, _active[i], now);
            _active.RemoveAt(i);
            removed++;
        }

        if (removed > 0) Rebuild();
    }

    public void Clear(double now)
    {
        if (_active.Count == 0) return;
        foreach (ActiveStatus status in _active) Note(applied: false, status, now);
        _active.Clear();
        Rebuild();
    }

    /// <summary>
    /// Strip up to <paramref name="count"/> dispellable statuses. Expiry effects do
    /// NOT run: removing something early is the entire point of removing it, and a
    /// bomb that detonated when cleansed would make cleansing it pointless.
    /// </summary>
    public int Dispel(bool beneficial, int count, double now)
    {
        int removed = 0;

        for (int i = _active.Count - 1; i >= 0 && removed < count; i--)
        {
            StatusEffect definition = _active[i].Definition;
            if (!definition.Dispellable || definition.Beneficial != beneficial) continue;

            Note(applied: false, _active[i], now);
            _active.RemoveAt(i);
            removed++;
        }

        if (removed > 0) Rebuild();
        return removed;
    }

    /// <summary>
    /// Spend shields against incoming damage and return what gets through. Server
    /// side: this mutates.
    /// </summary>
    public float AbsorbDamage(float amount, double now)
    {
        if (amount <= 0f || AbsorbRemaining <= 0f) return Mathf.Max(0f, amount);

        float remaining = amount;
        bool changed = false;

        for (int i = _active.Count - 1; i >= 0 && remaining > 0f; i--)
        {
            ActiveStatus status = _active[i];
            if (status.AbsorbRemaining <= 0f) continue;

            float soaked = Mathf.Min(status.AbsorbRemaining, remaining);
            status.AbsorbRemaining -= soaked;
            remaining -= soaked;
            changed = true;

            // A spent shield is gone, not a zero-strength status sitting on the bar.
            if (status.AbsorbRemaining > 0.0001f) continue;

            Note(applied: false, status, now);
            _active.RemoveAt(i);
        }

        if (changed) Rebuild();
        return remaining;
    }

    /// <summary>Server: run whatever ticks, then expire what is done.</summary>
    public void Tick(ICombatant owner, double now)
    {
        // Walk a SNAPSHOT. Resolving a tick runs arbitrary effects and they reach
        // back in here: a damage-over-time that kills its host clears the whole
        // list, and one that applies a status adds to it. Either mutation during a
        // foreach throws, which is what a burning player dying used to do.
        ActiveStatus[] ticking = _active.ToArray();

        foreach (ActiveStatus status in ticking)
        {
            if (status.Definition.OnTick.Count == 0) continue;
            if (now < status.NextTickAt) continue;

            // It may have been removed by an earlier tick in this same walk.
            if (!_active.Contains(status)) continue;

            status.NextTickAt = now + Mathf.Max(0.05f, status.Definition.TickInterval);
            Run(owner, status, now, status.Definition.OnTick, status.Definition.TickRadius);
        }

        List<ActiveStatus> expired = TakeExpired(now);
        if (expired is null) return;

        foreach (ActiveStatus status in expired)
        {
            // Logged here rather than in TakeExpired, which has no idea whose
            // status it is dropping. Without a removal event a replay would show
            // a buff that ended, and uptime would be a guess from durations that
            // a dispel or a death can cut short.
            Note(applied: false, status, now);

            if (status.Definition.OnExpire.Count > 0)
                Run(owner, status, now, status.Definition.OnExpire, status.Definition.ExpireRadius);
        }

        Rebuild();
    }

    /// <summary>Client: drop what has visibly ended. Never runs effects.</summary>
    public void PruneForDisplay(double now)
    {
        if (TakeExpired(now) is not null) Rebuild();
    }

    private List<ActiveStatus> TakeExpired(double now)
    {
        List<ActiveStatus> expired = null;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (now < _active[i].ExpiresAt) continue;
            (expired ??= new List<ActiveStatus>()).Add(_active[i]);
            _active.RemoveAt(i);
        }

        return expired;
    }

    /// <summary>
    /// Resolve one status's effects.
    ///
    /// The area is always a real circle centred on the bearer, never a default
    /// struct. A zero-radius footprint at the world origin was a live trap: any
    /// area-dependent effect used as a tick -- a knockback, say -- would have flung
    /// the whole raid toward (0, 0).
    /// </summary>
    private static void Run(ICombatant owner, ActiveStatus status, double now,
                            Godot.Collections.Array<AbilityEffect> effects, float radius)
    {
        ICombatant caster = status.Source ?? owner;
        var area = new TelegraphArea(TelegraphShape.Circle, owner.CombatPosition, 0f, Mathf.Max(radius, 0f));

        List<ICombatant> targets;
        List<ICombatant> candidates;

        if (radius <= 0f || owner.Node is null)
        {
            targets = new List<ICombatant> { owner };
            candidates = targets;
        }
        else
        {
            candidates = Combatants.Living(owner.Node, caster, status.Definition.AreaAffects);
            targets = new List<ICombatant>();

            foreach (ICombatant candidate in candidates)
                if (area.Contains(candidate.CombatPosition))
                    targets.Add(candidate);
        }

        var context = new EffectContext
        {
            AbilityName = status.Definition.DisplayName,
            Caster = caster,
            Area = area,
            Targets = targets,
            Candidates = candidates,
            Now = now,
        };

        for (int stack = 0; stack < status.Stacks; stack++)
            foreach (AbilityEffect effect in effects)
                effect?.Resolve(context);
    }

    // -- aggregation and wire form ---------------------------------------

    private void Rebuild()
    {
        // Modifiers aggregate over distinct ids using the biggest stack, so several
        // casters applying the same debuff do not multiply it once each.
        var strongest = new Dictionary<string, ActiveStatus>();
        foreach (ActiveStatus status in _active)
        {
            string id = status.Definition.Id;
            if (!strongest.TryGetValue(id, out ActiveStatus best) || status.Stacks > best.Stacks)
                strongest[id] = status;
        }

        float move = 1f, taken = 1f, dealt = 1f, regen = 1f;

        foreach (ActiveStatus status in strongest.Values)
        {
            StatusEffect definition = status.Definition;

            // Per stack, so three stacks of a 1.2x vulnerability is 1.728x.
            move *= Mathf.Pow(definition.MoveSpeedMultiplier, status.Stacks);
            taken *= Mathf.Pow(definition.DamageTakenMultiplier, status.Stacks);
            dealt *= Mathf.Pow(definition.DamageDealtMultiplier, status.Stacks);
            regen *= Mathf.Pow(definition.ManaRegenMultiplier, status.Stacks);
        }

        bool rooted = false, silenced = false;
        float absorb = 0f;
        var builder = new StringBuilder();

        foreach (ActiveStatus status in _active)
        {
            rooted |= status.Definition.Rooted;
            silenced |= status.Definition.Silenced;

            // Shields sum across instances: two shields really are more shield.
            absorb += status.AbsorbRemaining;

            if (builder.Length > 0) builder.Append('|');
            builder.Append(status.Definition.Id).Append(':')
                   .Append(status.ExpiresAt.ToString("0.##", CultureInfo.InvariantCulture)).Append(':')
                   .Append(status.Stacks).Append(':')
                   .Append(status.SourceId).Append(':')
                   .Append(status.AbsorbRemaining.ToString("0.#", CultureInfo.InvariantCulture));
        }

        MoveSpeedMultiplier = move;
        DamageTakenMultiplier = taken;
        DamageDealtMultiplier = dealt;
        ManaRegenMultiplier = regen;
        AbsorbRemaining = absorb;
        Rooted = rooted;
        Silenced = silenced;
        Encoded = builder.ToString();
    }

    /// <summary>Client: rebuild the whole set from the server's encoding.</summary>
    public void Decode(string payload)
    {
        _active.Clear();

        if (!string.IsNullOrEmpty(payload))
        {
            foreach (string entry in payload.Split('|'))
            {
                string[] parts = entry.Split(':');

                // Exactly the five fields Rebuild writes, and no fewer.
                //
                // This used to accept short entries "so the format can grow without
                // a version handshake", which was speculative: a client and its
                // server are the same build, so a truncated entry can only mean a
                // corrupt payload. Accepting one silently produced a status with a
                // missing source and no shield rather than an obvious fault.
                if (parts.Length != 5) continue;

                StatusEffect definition = StatusLibrary.Get(parts[0]);
                if (definition is null) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double expires)) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int stacks)) continue;
                if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sourceId)) continue;
                if (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float absorb)) continue;

                _active.Add(new ActiveStatus
                {
                    Definition = definition,
                    ExpiresAt = expires,
                    Stacks = stacks,
                    SourceId = sourceId,
                    AbsorbRemaining = absorb,
                });
            }
        }

        Rebuild();
    }
}
