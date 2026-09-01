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

    /// Bumped by hand when the record's shape changes, so the backend can reject
    /// what it does not understand instead of guessing.
    public const int SchemaVersion = 1;

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

    public void BeginAttempt(string bossId)
    {
        if (!NetworkManager.Instance.IsServer || _inProgress) return;

        _departed.Clear();
        _bossId = bossId;
        _runId = NewRunId();
        _startedAt = NetClock.Instance.ServerTime;
        _inProgress = true;
    }

    public void CompleteAttempt(bool victory)
    {
        if (!NetworkManager.Instance.IsServer || !_inProgress) return;
        _inProgress = false;

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

    /// <summary>Called before a departing hero's node is freed, so it stays in the record.</summary>
    public void CaptureDeparting(Hero hero)
    {
        if (!NetworkManager.Instance.IsServer || !_inProgress || hero is null) return;
        _departed[hero.PeerId] = LineFor(hero, departed: true);
    }

    private static Godot.Collections.Dictionary LineFor(Hero hero, bool departed) => new()
    {
        ["peer"] = hero.PeerId,
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
