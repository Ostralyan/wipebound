using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using Wipebound.Combat;

namespace Wipebound.Session;

/// <summary>
/// A fingerprint of the balance data a run was played against.
///
/// A four minute clear against a boss with 4000 health is not the achievement a
/// 400 health boss gives you, so a ladder that mixes them measures nothing.
///
/// Two things this has been caught missing, both worth naming because both were
/// silent:
///
///   A fixed recursion cap truncated deep content. The Cinders chain runs phase ->
///   ability -> SpawnHazardEffect -> Hazard -> OnTick -> DamageEffect, which ran
///   past the limit, so its tick damage could be changed without changing the
///   fingerprint. Depth is now bounded by CYCLE detection rather than a number, so
///   nesting can grow without quietly falling off the end.
///
///   Balance that lives on NODES rather than Resources was invisible entirely --
///   boss health, hero speed, mana regeneration, minion stats. Those are walked
///   too now, restricted to script-declared exports so that moving a mesh in the
///   editor does not invalidate a season.
/// </summary>
public static class ContentHash
{
    /// A backstop only; genuine recursion is bounded by the path set below.
    private const int MaxDepth = 64;

    /// Properties every Resource carries that say nothing about balance.
    private static readonly HashSet<string> Ignored = new()
    {
        "script", "resource_path", "resource_name", "resource_local_to_scene", "Resource",
    };

    /// Scenes whose script-declared exports are gameplay parameters.
    private static readonly string[] TunedScenes =
    {
        "res://src/Player/Hero.tscn",
        "res://src/Combat/Minion.tscn",
    };

    public static string Current { get; } = Compute();

    /// <summary>
    /// Recomputed rather than cached, so a test can check the fingerprint is the
    /// same under a different locale.
    /// </summary>
    public static string Compute() => Fnv1a(Manifest());

    /// <summary>
    /// The exact bytes Compute() hashes.
    ///
    /// Exists because "your server reports a different fingerprint from the one
    /// the ladder whitelists" is otherwise undebuggable: two sixteen-character
    /// hashes tell you that they differ and nothing about where. Diff this
    /// between the two builds instead.
    /// </summary>
    public static string Manifest()
    {
        var builder = new StringBuilder();
        var path = new HashSet<ulong>();

        foreach (BossPhase phase in DefaultEncounter.Build()) Append(builder, phase, 0, path);
        foreach (HeroClass hero in System.Enum.GetValues<HeroClass>())
            foreach (Ability ability in PlayerKit.For(hero))
                Append(builder, ability, 0, path);
        Append(builder, MinionKit.Claw(), 0, path);

        // Statuses are balance too: a vulnerability multiplier or a shield size
        // changes a fight as surely as a damage number does.
        //
        // Sorted by id, because the registry hands them back in Dictionary order
        // and .NET does not promise what that is. Two builds of identical content
        // must produce identical bytes here or honest players get refused, which
        // is the same failure as formatting numbers in the ambient locale.
        var statuses = new List<StatusEffect>(StatusLibrary.All);
        statuses.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        foreach (StatusEffect status in statuses) Append(builder, status, 0, path);

        AppendTunedScenes(builder);
        AppendConstants(builder);

        return builder.ToString();
    }

    /// <summary>
    /// Gameplay constants that reflection cannot see.
    ///
    /// A const is not a property, so the walk over exported values misses it
    /// entirely -- and several of these decide whether a run is even eligible for
    /// a ladder. There is no way to enumerate them, so this list is maintained by
    /// hand and that is a real cost: a constant added here and forgotten here is
    /// invisible again. Kept short for exactly that reason, and anything that can
    /// reasonably be an [Export] should be one instead.
    /// </summary>
    private static void AppendConstants(StringBuilder builder)
    {
        // Sorted like every other block. These are written by hand in a fixed
        // order, so they were already deterministic -- but a manifest with one
        // block that plays by different rules is a manifest nobody can check
        // mechanically, and the check is what caught the export drift.
        var constants = new SortedDictionary<string, double>(StringComparer.Ordinal)
        {
            ["arena"] = Player.Hero.ArenaRadius,
            ["speedMargin"] = Player.Hero.SpeedChangeMargin,
            ["ack"] = Player.Hero.AcknowledgeDistance,
            ["tolerance"] = MovementValidator.SpeedTolerance,
            ["burst"] = MovementValidator.BurstSeconds,
            ["garbage"] = MovementValidator.GarbageClaimPenalty,
            ["attentionHalfLife"] = TargetSelection.AttentionHalfLife,
            ["attentionMemory"] = TargetSelection.AttentionMemory,
            ["maxCreditedRtt"] = Net.NetClock.MaxCreditedRtt,
        };

        builder.Append("constants(");
        foreach ((string name, double value) in constants)
            builder.Append(name).Append('=').Append(Number(value)).Append(';');
        builder.Append(')');
    }

    /// <summary>
    /// The single place a number becomes text here.
    ///
    /// Invariant, and round-trip formatted. Appending a float directly used the
    /// AMBIENT CULTURE, so a machine with a comma decimal separator wrote 0,15
    /// where another wrote 0.15 -- identical builds, different fingerprints, and a
    /// ladder that rejected runs depending on where the server happened to be.
    /// </summary>
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Instantiate the scenes that carry tuning and read their script exports.
    ///
    /// Instantiate does not enter the tree, so no _Ready runs and nothing has to be
    /// unwound; each is freed immediately. It costs a few milliseconds once.
    /// </summary>
    private static void AppendTunedScenes(StringBuilder builder)
    {
        foreach (string scenePath in TunedScenes)
        {
            var packed = GD.Load<PackedScene>(scenePath);
            if (packed is null) continue;

            Node node = packed.Instantiate();
            AppendNode(builder, scenePath, node);
            node.Free();
        }

        // Autoloads carry tuning too -- the resolve grace a telegraph is judged
        // against, and the rate a client may act at -- and they are scripts rather
        // than scenes, so they are constructed directly and freed.
        foreach (Node singleton in new Node[] { new Combat.CombatDirector(), new Combat.Commands.CommandRouter() })
        {
            AppendNode(builder, singleton.GetType().Name, singleton);
            singleton.Free();
        }

        // The boss lives in the world scene rather than one of its own.
        var world = GD.Load<PackedScene>("res://src/Main.tscn");
        if (world is null) return;

        Node main = world.Instantiate();
        Node boss = main.GetNodeOrNull("Boss");
        if (boss is not null) AppendNode(builder, "boss", boss);
        main.Free();
    }

    private static void AppendNode(StringBuilder builder, string label, Node node)
    {
        builder.Append(label).Append('(');

        var names = new List<string>();
        foreach (Godot.Collections.Dictionary property in node.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)property["usage"];

            // Script variables only. Engine properties -- transforms, collision
            // layers, mesh references -- are deterministic but they are scene
            // dressing, and a cosmetic edit should not void a ladder season.
            if (!usage.HasFlag(PropertyUsageFlags.ScriptVariable)) continue;
            if (!usage.HasFlag(PropertyUsageFlags.Storage)) continue;

            string name = property["name"].AsString();
            if (Ignored.Contains(name)) continue;

            names.Add(name);
        }

        // Sorted for the same reason the resource walk is: enumeration order is
        // not the same in an exported build as it is in the editor.
        names.Sort(StringComparer.Ordinal);

        foreach (string name in names)
        {
            builder.Append(name).Append('=');
            Append(builder, node.Get(name), 0, new HashSet<ulong>());
            builder.Append(';');
        }

        builder.Append(')');
    }

    private static void Append(StringBuilder builder, Variant value, int depth, HashSet<ulong> path)
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
                Append(builder, value.AsGodotObject() as Resource, depth, path);
                return;

            case Variant.Type.Array:
                builder.Append('[');
                foreach (Variant item in value.AsGodotArray())
                {
                    Append(builder, item, depth + 1, path);
                    builder.Append(',');
                }
                builder.Append(']');
                return;

            case Variant.Type.Dictionary:
                builder.Append('{');
                foreach (var pair in value.AsGodotDictionary())
                {
                    Append(builder, pair.Key, depth + 1, path);
                    builder.Append(':');
                    Append(builder, pair.Value, depth + 1, path);
                    builder.Append(',');
                }
                builder.Append('}');
                return;

            // Everything carrying a decimal is formatted here rather than left to
            // Godot's stringify, so the encoding is ours and provably invariant.
            case Variant.Type.Float:
                builder.Append(Number(value.AsDouble()));
                return;

            case Variant.Type.Vector2:
            {
                Vector2 v = value.AsVector2();
                builder.Append(Number(v.X)).Append(',').Append(Number(v.Y));
                return;
            }

            case Variant.Type.Vector3:
            {
                Vector3 v = value.AsVector3();
                builder.Append(Number(v.X)).Append(',').Append(Number(v.Y)).Append(',').Append(Number(v.Z));
                return;
            }

            case Variant.Type.Color:
            {
                Color c = value.AsColor();
                builder.Append(Number(c.R)).Append(',').Append(Number(c.G)).Append(',')
                       .Append(Number(c.B)).Append(',').Append(Number(c.A));
                return;
            }

            case Variant.Type.Int:
                builder.Append(value.AsInt64().ToString(CultureInfo.InvariantCulture));
                return;

            default:
                builder.Append(value.ToString());
                return;
        }
    }

    private static void Append(StringBuilder builder, Resource resource, int depth, HashSet<ulong> path)
    {
        if (resource is null)
        {
            builder.Append("null");
            return;
        }

        // Only a genuine cycle stops the walk. A resource shared between two phases
        // is not a cycle and is hashed in full at each place it appears.
        ulong id = resource.GetInstanceId();
        if (!path.Add(id))
        {
            builder.Append("cycle");
            return;
        }

        builder.Append(resource.GetType().Name).Append('(');

        // BY NAME, never in the order the engine hands them back.
        //
        // This is not tidiness. An exported build enumerates a script's
        // properties in a different order from the editor, so the same commit
        // produced two different fingerprints -- byte-for-byte the same values,
        // concatenated in a different sequence. The ladder whitelists one hash,
        // and a dedicated server built from that very commit reported another,
        // which would have refused every ranked run on the live servers while
        // every test on a developer's machine passed.
        //
        // Sorting makes the manifest canonical: it can then only change when a
        // NUMBER changes, which is the single thing it is supposed to mean.
        var names = new List<string>();
        foreach (Godot.Collections.Dictionary property in resource.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)property["usage"];
            if (!usage.HasFlag(PropertyUsageFlags.Storage)) continue;

            string name = property["name"].AsString();
            if (Ignored.Contains(name)) continue;

            names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);

        foreach (string name in names)
        {
            builder.Append(name).Append('=');
            Append(builder, resource.Get(name), depth + 1, path);
            builder.Append(';');
        }

        builder.Append(')');
        path.Remove(id);
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
