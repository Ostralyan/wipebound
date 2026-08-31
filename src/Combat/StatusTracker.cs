using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wipebound.Combat;

/// <summary>One live instance of a StatusEffect on one combatant.</summary>
public sealed class ActiveStatus
{
    public StatusEffect Definition { get; init; }
    public ICombatant Source { get; set; }
    public double ExpiresAt { get; set; }
    public int Stacks { get; set; } = 1;
    public double NextTickAt { get; set; }

    public double RemainingAt(double now) => Mathf.Max(0.0, ExpiresAt - now);
}

/// <summary>
/// Every status currently on one combatant, plus the aggregate they add up to.
///
/// The aggregates are recomputed on change rather than queried on demand, because
/// they are read every physics frame by movement and every damage application,
/// and change perhaps once a second.
///
/// REPLICATION. Statuses are server-authoritative, but the client needs its own:
/// hero movement is client-authoritative, so a client that does not know it is
/// slowed will keep running at full speed and the server's speed clamp will drag
/// it back, which looks exactly like rubber-banding. So the whole set travels as
/// a small encoded string on the server-authoritative synchronizer.
///
/// A string, rather than an array of dictionaries, for two reasons: Godot's
/// change detection over it is unambiguous, and you can read the live state of
/// every buff in the game straight out of a log line.
///
/// Expiry times are absolute server-clock times, which is what makes a decoded
/// status on a client agree with the server about when it ends.
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
    public bool Rooted { get; private set; }
    public bool Silenced { get; private set; }

    public bool Has(string id) => Find(id) is not null;

    private ActiveStatus Find(string id)
    {
        foreach (ActiveStatus status in _active)
            if (status.Definition.Id == id) return status;
        return null;
    }

    // -- server side ------------------------------------------------------

    public void Apply(StatusEffect definition, ICombatant source, double now)
    {
        if (definition is null) return;

        ActiveStatus existing = Find(definition.Id);

        if (existing is null)
        {
            _active.Add(new ActiveStatus
            {
                Definition = definition,
                Source = source,
                ExpiresAt = now + definition.Duration,
                Stacks = 1,
                NextTickAt = now + definition.TickInterval,
            });
        }
        else
        {
            switch (definition.Stacking)
            {
                case StackRule.Ignore:
                    return;
                case StackRule.Stack:
                    existing.Stacks = Mathf.Min(existing.Stacks + 1, Mathf.Max(1, definition.MaxStacks));
                    break;
            }

            existing.Source = source;
            existing.ExpiresAt = now + definition.Duration;
        }

        Rebuild();
    }

    public void Remove(string id)
    {
        ActiveStatus status = Find(id);
        if (status is null) return;
        _active.Remove(status);
        Rebuild();
    }

    public void Clear()
    {
        if (_active.Count == 0) return;
        _active.Clear();
        Rebuild();
    }

    /// <summary>Server: expire what is done and run whatever ticks.</summary>
    public void Tick(ICombatant owner, double now)
    {
        bool changed = PruneExpired(now);

        foreach (ActiveStatus status in _active)
        {
            if (status.Definition.OnTick.Count == 0) continue;
            if (now < status.NextTickAt) continue;

            status.NextTickAt = now + Mathf.Max(0.05f, status.Definition.TickInterval);

            var single = new List<ICombatant> { owner };
            var context = new EffectContext
            {
                AbilityName = status.Definition.DisplayName,
                Caster = status.Source ?? owner,
                Targets = single,
                Candidates = single,
            };

            for (int stack = 0; stack < status.Stacks; stack++)
                foreach (AbilityEffect effect in status.Definition.OnTick)
                    effect?.Resolve(context);
        }

        if (changed) Rebuild();
    }

    /// <summary>Client: drop what has visibly ended, so the HUD stays honest between updates.</summary>
    public void PruneForDisplay(double now)
    {
        if (PruneExpired(now)) Rebuild();
    }

    private bool PruneExpired(double now)
    {
        int removed = _active.RemoveAll(status => now >= status.ExpiresAt);
        return removed > 0;
    }

    // -- wire form --------------------------------------------------------

    private void Rebuild()
    {
        float move = 1f, taken = 1f, dealt = 1f, regen = 1f;
        bool rooted = false, silenced = false;

        var builder = new StringBuilder();

        foreach (ActiveStatus status in _active)
        {
            StatusEffect definition = status.Definition;

            // Per stack, so three stacks of a 1.2x vulnerability is 1.728x.
            move *= Mathf.Pow(definition.MoveSpeedMultiplier, status.Stacks);
            taken *= Mathf.Pow(definition.DamageTakenMultiplier, status.Stacks);
            dealt *= Mathf.Pow(definition.DamageDealtMultiplier, status.Stacks);
            regen *= Mathf.Pow(definition.ManaRegenMultiplier, status.Stacks);

            rooted |= definition.Rooted;
            silenced |= definition.Silenced;

            if (builder.Length > 0) builder.Append('|');
            builder.Append(definition.Id).Append(':')
                   .Append(status.ExpiresAt.ToString("0.##", CultureInfo.InvariantCulture)).Append(':')
                   .Append(status.Stacks);
        }

        MoveSpeedMultiplier = move;
        DamageTakenMultiplier = taken;
        DamageDealtMultiplier = dealt;
        ManaRegenMultiplier = regen;
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
                if (parts.Length != 3) continue;

                StatusEffect definition = StatusLibrary.Get(parts[0]);
                if (definition is null) continue;

                _active.Add(new ActiveStatus
                {
                    Definition = definition,
                    ExpiresAt = double.Parse(parts[1], CultureInfo.InvariantCulture),
                    Stacks = int.Parse(parts[2], CultureInfo.InvariantCulture),
                });
            }
        }

        Rebuild();
    }
}
