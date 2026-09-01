using System.Collections.Generic;
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

    private static string Compute()
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
        foreach (StatusEffect status in StatusLibrary.All) Append(builder, status, 0, path);

        AppendTunedScenes(builder);
        AppendConstants(builder);

        return Fnv1a(builder.ToString());
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
        builder.Append("constants(")
               .Append("arena=").Append(Player.Hero.ArenaRadius).Append(';')
               .Append("speedMargin=").Append(Player.Hero.SpeedChangeMargin).Append(';')
               .Append("ack=").Append(Player.Hero.AcknowledgeDistance).Append(';')
               .Append("tolerance=").Append(MovementValidator.SpeedTolerance).Append(';')
               .Append("burst=").Append(MovementValidator.BurstSeconds).Append(';')
               .Append("garbage=").Append(MovementValidator.GarbageClaimPenalty).Append(';')
               .Append("attentionHalfLife=").Append(TargetSelection.AttentionHalfLife).Append(';')
               .Append("attentionMemory=").Append(TargetSelection.AttentionMemory).Append(';')
               .Append("maxCreditedRtt=").Append(Net.NetClock.MaxCreditedRtt).Append(';')
               .Append(')');
    }

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

        foreach (Godot.Collections.Dictionary property in resource.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)(long)property["usage"];
            if (!usage.HasFlag(PropertyUsageFlags.Storage)) continue;

            string name = property["name"].AsString();
            if (Ignored.Contains(name)) continue;

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
