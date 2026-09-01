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

    private string _bossId;
    private string _runId;
    private double _startedAt;
    private bool _inProgress;

    public override void _Ready() => Instance = this;

    public void BeginAttempt(string bossId)
    {
        if (!NetworkManager.Instance.IsServer || _inProgress) return;

        _bossId = bossId;
        _runId = NewRunId();
        _startedAt = NetClock.Instance.ServerTime;
        _inProgress = true;
    }

    public void CompleteAttempt(bool victory)
    {
        if (!NetworkManager.Instance.IsServer || !_inProgress) return;
        _inProgress = false;

        Godot.Collections.Dictionary record = Build(victory);
        GD.Print($"[run] {Json.Stringify(record)}");

        // Where the POST to the backend goes. Deliberately not written yet: the
        // shape has to be settled before anything depends on it.
        Submitted?.Invoke(record);
    }

    /// <summary>Hook for whatever ships the record onward.</summary>
    public System.Action<Godot.Collections.Dictionary> Submitted;

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

        var players = new Godot.Collections.Array();
        int worstOverreachCm = 0;

        foreach (Node node in GetTree().GetNodesInGroup(Hero.GroupName))
        {
            if (node is not Hero hero) continue;

            int overreachCm = Mathf.RoundToInt(hero.Overreach * 100f);
            worstOverreachCm = Mathf.Max(worstOverreachCm, overreachCm);

            players.Add(new Godot.Collections.Dictionary
            {
                ["peer"] = hero.PeerId,
                ["damage_done"] = Mathf.RoundToInt(hero.DamageDone),
                ["healing_done"] = Mathf.RoundToInt(hero.HealingDone),
                ["damage_taken"] = Mathf.RoundToInt(hero.DamageTaken),
                ["overreach_cm"] = overreachCm,
            });
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
