using System.Collections.Generic;
using System.Text;
using Godot;
using Wipebound.Combat;

namespace Wipebound.Session;

/// <summary>
/// A fingerprint of the balance data a run was played against.
///
/// A four minute clear against a boss with 4000 health is not the achievement a
/// 400 health boss gives you, so a ladder that mixes them measures nothing. The
/// hash travels with the run record and the backend only ranks values it knows.
///
/// It walks EXPORTED PROPERTIES rather than a hand-written list of fields, and
/// that is the point. An earlier version hashed the number of effects on an
/// ability, so damage values, applied statuses, hazard durations, summon counts
/// and targeting rules could all change without changing the fingerprint. A
/// generic walk cannot rot that way: a new effect with new numbers is covered the
/// day it is written, by nobody remembering anything.
/// </summary>
public static class ContentHash
{
    private const int MaxDepth = 6;

    /// Properties every Resource carries that say nothing about balance.
    private static readonly HashSet<string> Ignored = new()
    {
        "script", "resource_path", "resource_name", "resource_local_to_scene", "Resource",
    };

    public static string Current { get; } = Compute();

    private static string Compute()
    {
        var builder = new StringBuilder();

        foreach (BossPhase phase in DefaultEncounter.Build()) Append(builder, phase, 0);
        foreach (Ability ability in PlayerKit.Build()) Append(builder, ability, 0);
        Append(builder, MinionKit.Claw(), 0);

        // Statuses are balance too: a vulnerability multiplier or a shield size
        // changes a fight as surely as a damage number does.
        foreach (StatusEffect status in StatusLibrary.All) Append(builder, status, 0);

        return Fnv1a(builder.ToString());
    }

    private static void Append(StringBuilder builder, Variant value, int depth)
    {
        if (depth > MaxDepth)
        {
            builder.Append("...");
            return;
        }

        switch (value.VariantType)
        {
            case Variant.Type.Nil:
                builder.Append("nil");
                return;

            case Variant.Type.Object:
                Append(builder, value.AsGodotObject() as Resource, depth);
                return;

            case Variant.Type.Array:
                builder.Append('[');
                foreach (Variant item in value.AsGodotArray()) { Append(builder, item, depth + 1); builder.Append(','); }
                builder.Append(']');
                return;

            case Variant.Type.Dictionary:
                builder.Append('{');
                foreach (var pair in value.AsGodotDictionary())
                {
                    Append(builder, pair.Key, depth + 1);
                    builder.Append(':');
                    Append(builder, pair.Value, depth + 1);
                    builder.Append(',');
                }
                builder.Append('}');
                return;

            default:
                builder.Append(value.ToString());
                return;
        }
    }

    private static void Append(StringBuilder builder, Resource resource, int depth)
    {
        if (resource is null)
        {
            builder.Append("null");
            return;
        }

        builder.Append(resource.GetType().Name).Append('(');

        foreach (Godot.Collections.Dictionary property in resource.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)property["usage"];
            if (!usage.HasFlag(PropertyUsageFlags.Storage)) continue;

            string name = property["name"].AsString();
            if (Ignored.Contains(name)) continue;

            builder.Append(name).Append('=');
            Append(builder, resource.Get(name), depth + 1);
            builder.Append(';');
        }

        builder.Append(')');
    }

    /// FNV-1a: short, stable across runs and platforms, and not a security
    /// boundary -- nobody gains anything by colliding it, since the backend only
    /// ranks values it has been told about.
    private static string Fnv1a(string text)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offset;
        foreach (char character in text)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash.ToString("x16");
    }
}
