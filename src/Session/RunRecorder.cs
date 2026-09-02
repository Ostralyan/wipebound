using Godot;
using System.Collections.Generic;
using Wipebound.Combat;
using Wipebound.Net;
using Wipebound.Player;

namespace Wipebound.Session;

/// <summary>
/// Assembles the record of one attempt, on the server, for the backend to ingest.
///
/// THE CLIENT NEVER SUBMITS A SCORE. Everything here is computed from state the
/// server already owns: the clock is the server's, the damage numbers come from
/// the chokepoint every hit passes through, and the integrity figures come from
/// the position validator. A client's only influence on its own record is playing
/// the game.
///
/// Runs from a player-hosted session are recorded but marked unrankable rather
/// than suppressed. Whoever hosts IS the authority and can trivially forge
/// everything in here, so such a run cannot count -- but local testing should not
/// have to be a different code path, because a code path nobody runs is a code
/// path nobody has debugged.
/// </summary>
public partial class RunRecorder : Node
{
    public static RunRecorder Instance { get; private set; }

    /// <summary>
    /// Everything that happened, for a site to render and a replay to play back.
    ///
    /// Lives beside the summary rather than inside it: the ladder needs four
    /// numbers per player and should not wait on a megabyte of events to get
    /// them. See CombatLog.
    /// </summary>
    public CombatLog Log { get; } = new();

    public const string LogDirectory = "user://runs";

    /// <summary>
    /// Bumped by hand when the record's shape changes, so the backend can reject
    /// what it does not understand instead of guessing.
    ///
    /// 2 added player_id, display_name and identity to every player line. They
    /// are required, not optional, so a version 1 record and a version 2 record
    /// mean genuinely different things -- and leaving the number at 1 would have
    /// let a mid-deployment mix of old servers and a new backend disagree while
    /// both insisted they agreed. A rejection naming the version is a far better
    /// failure than a deserialisation error.
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// Snapshots of players who left before the attempt ended.
    ///
    /// The roster used to be read from whichever hero nodes still existed, and a
    /// disconnect frees the node -- so leaving took your contribution out of the
    /// record, and your overreach evidence with it. Quitting before the end was a
    /// way to erase having cheated.
    /// </summary>
    private readonly Dictionary<int, Godot.Collections.Dictionary> _departed = new();

    private string _bossId;
    private string _runId;
    private double _startedAt;
    private bool _inProgress;

    public override void _Ready() => Instance = this;

    /// <summary>
    /// Keep the log's cast of characters current, and write down where they are.
    ///
    /// Introducing actors here rather than at their spawn sites means nothing has
    /// to remember to do it: anyone who exists and is being sampled is, by
    /// definition, already known. The type knowledge lives here because this is
    /// where a Hero can be told from a Minion.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!_inProgress || !NetworkManager.Instance.IsServer) return;

        double now = NetClock.Instance.ServerTime;
        var present = new System.Collections.Generic.List<ICombatant>();

        foreach (Node node in GetTree().GetNodesInGroup(Combatants.GroupName))
        {
            if (node is not ICombatant combatant) continue;
            present.Add(combatant);

            if (Log.Knows(combatant.CombatId)) continue;

            switch (combatant)
            {
                case Hero hero:
                    Log.Introduce(now, hero, "hero", PlayerKit.NameOf(hero.Class), hero.PlayerId);
                    break;
                case Boss boss:
                    Log.Introduce(now, boss, "boss", boss.DisplayName);
                    break;
                default:
                    Log.Introduce(now, combatant, "minion");
                    break;
            }
        }

        Log.SamplePositions(now, present);
    }

    public void BeginAttempt(string bossId)
    {
        if (!NetworkManager.Instance.IsServer || _inProgress) return;

        _departed.Clear();
        _bossId = bossId;
        _runId = NewRunId();
        _startedAt = NetClock.Instance.ServerTime;
        _inProgress = true;
        Log.Begin(bossId, _startedAt);
    }

    public void CompleteAttempt(bool victory)
    {
        if (!NetworkManager.Instance.IsServer || !_inProgress) return;
        _inProgress = false;
        Log.Finish();

        // A wipe leaves dead heroes in the tree; an EMPTY roster means everybody
        // disconnected, so nobody played this attempt and there is nothing to
        // record. Found by running the whole stack: the backend correctly refused
        // the rosterless run this used to send, which is the right answer to the
        // wrong question being asked.
        if (RosterSize() == 0 && _departed.Count == 0)
        {
            GD.Print("[run] attempt abandoned: nobody was left in it");
            return;
        }

        Godot.Collections.Dictionary record = Build(victory);

        WriteLog(_runId, NetClock.Instance.ServerTime);
        (GetTree().Root.GetNodeOrNull("LogUploader") as LogUploader)?.Offer(_runId);
        GD.Print($"[run] {Json.Stringify(record)}");

        // RunSubmitter listens here and ships it. Kept as a hook rather than a
        // direct call so recording a run does not depend on being able to send one.
        Submitted?.Invoke(record);
    }

    private int RosterSize()
    {
        int count = 0;
        foreach (Node node in GetTree().GetNodesInGroup(Hero.GroupName))
            if (node is Hero) count++;

        return count;
    }

    /// <summary>Hook for whatever ships the record onward.</summary>
    public System.Action<Godot.Collections.Dictionary> Submitted;

    /// <summary>
    /// Put the fight on disk, gzipped, beside the summary that will be posted.
    ///
    /// Written locally first rather than sent inline with the run. The ladder
    /// needs four numbers per player and should not wait on a megabyte of
    /// events to get them, and a log that fails to upload must not take the run
    /// record down with it -- so this is a file an uploader can carry
    /// separately, on its own schedule, and retry.
    ///
    /// Standard gzip, from .NET, rather than Godot's own compressed container:
    /// the reader is going to be a website.
    /// </summary>
    private void WriteLog(string runId, double endedAt)
    {
        try
        {
            string directory = ProjectSettings.GlobalizePath(LogDirectory);
            System.IO.Directory.CreateDirectory(directory);

            byte[] json = System.Text.Encoding.UTF8.GetBytes(
                Json.Stringify(Log.ToDocument(runId, endedAt)));

            string path = System.IO.Path.Combine(directory, $"{runId}.json.gz");

            using var file = System.IO.File.Create(path);
            using var gzip = new System.IO.Compression.GZipStream(
                file, System.IO.Compression.CompressionLevel.Optimal);
            gzip.Write(json, 0, json.Length);

            GD.Print($"[log] {runId}: {Log.EventCount} events, {json.Length / 1024}KB raw" +
                     (Log.Truncated ? " (TRUNCATED)" : ""));
        }
        catch (System.Exception error)
        {
            // A fight that happened is worth more than a file that did not
            // write. Never let logging take down the run it describes.
            GD.PushWarning($"[log] could not write {runId}: {error.Message}");
        }
    }

    /// <summary>Called before a departing hero's node is freed, so it stays in the record.</summary>
    public void CaptureDeparting(Hero hero)
    {
        if (!NetworkManager.Instance.IsServer || !_inProgress || hero is null) return;
        _departed[hero.PeerId] = LineFor(hero, departed: true);
    }

    private static Godot.Collections.Dictionary LineFor(Hero hero, bool departed) => new()
    {
        // Peer is kept because it is a true fact about the SESSION -- it is how
        // this slot was addressed while the fight ran. It is no longer what
        // identifies the person, because it was never capable of that: a fresh
        // random integer every connection meant two runs by one player shared no
        // key and the ladder could rank without ever attributing.
        ["peer"] = hero.PeerId,

        // Provenance travels with the claim, exactly as authority does for the
        // run. "anonymous" means the server accepted this without checking,
        // because there is nothing yet to check it against.
        ["player_id"] = hero.PlayerId,
        ["display_name"] = hero.PlayerName,
        ["identity"] = hero.IdentityProvider,
        ["damage_done"] = Mathf.RoundToInt(hero.DamageDone),
        ["healing_done"] = Mathf.RoundToInt(hero.HealingDone),
        ["damage_taken"] = Mathf.RoundToInt(hero.DamageTaken),
        ["overreach_cm"] = Mathf.RoundToInt(hero.Overreach * 100f),
        ["departed"] = departed,
    };

    /// <summary>
    /// Generated here, not by the backend, so a submission can be retried after a
    /// network failure without posting the same run twice.
    /// </summary>
    private static string NewRunId()
    {
        byte[] bytes = new Crypto().GenerateRandomBytes(16);
        var hex = new System.Text.StringBuilder(32);
        foreach (byte value in bytes) hex.Append(value.ToString("x2"));
        return hex.ToString();
    }

    private Godot.Collections.Dictionary Build(bool victory)
    {
        double endedAt = NetClock.Instance.ServerTime;
        bool dedicated = NetworkManager.Instance.Mode == NetworkManager.NetMode.DedicatedServer;

        // Everyone who took part, not merely everyone still connected.
        var lines = new Dictionary<int, Godot.Collections.Dictionary>(_departed);

        foreach (Node node in GetTree().GetNodesInGroup(Hero.GroupName))
            if (node is Hero hero) lines[hero.PeerId] = LineFor(hero, departed: false);

        var players = new Godot.Collections.Array();
        int worstOverreachCm = 0;

        foreach (Godot.Collections.Dictionary line in lines.Values)
        {
            worstOverreachCm = Mathf.Max(worstOverreachCm, line["overreach_cm"].AsInt32());
            players.Add(line);
        }

        // Integers throughout. A ladder sorts on these, and float ordering brings
        // precision surprises, inconsistent ties and platform-dependent formatting
        // to a place where all three are visible to players.
        return new Godot.Collections.Dictionary
        {
            ["schema"] = SchemaVersion,
            ["run_id"] = _runId,
            ["boss"] = _bossId,
            ["outcome"] = victory ? "kill" : "wipe",
            ["duration_ms"] = Mathf.RoundToInt((float)(endedAt - _startedAt) * 1000f),
            ["content_hash"] = ContentHash.Current,
            ["engine"] = Engine.GetVersionInfo()["string"].AsString(),

            // Reported as facts. Note there is no "rankable" field: whether a run
            // counts is the backend's conclusion to draw, not this process's claim
            // to make. Same rule as clients sending intent rather than outcomes,
            // one level up.
            ["authority"] = dedicated ? "dedicated" : "player_hosted",
            ["worst_overreach_cm"] = worstOverreachCm,
            ["players"] = players,
        };
    }
}
