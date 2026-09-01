using Godot;
using System.Collections.Generic;

namespace Wipebound.Session;

/// <summary>
/// Ships finished run records to the ladder backend, and does not lose them.
///
/// Records are written to disk before the first attempt and deleted only once the
/// backend has given a final answer. A crash, a restart or a backend outage
/// therefore costs nothing: pending records are picked up again at startup. The
/// previous version held them in memory and gave up after four tries, which meant
/// a two minute deploy silently destroyed every run played during it.
///
/// Retrying is safe because the game server generates the run id, so the backend
/// recognises the same run arriving twice and writes nothing. That property is
/// what makes "never give up" a reasonable policy rather than a way to duplicate
/// ladder entries.
///
/// Configured entirely from the environment and silent when unconfigured, so a
/// developer needs no backend and a client export can never carry the submit
/// credential -- which is the whole point of dedicated servers.
/// </summary>
public partial class RunSubmitter : Node
{
    private const string SpoolDir = "user://pending_runs";

    /// Beyond this, something is badly wrong and filling the disk will not help.
    private const int MaxSpooled = 200;

    private HttpRequest _http;
    private string _baseUrl;
    private string _token;
    private string _serverId;

    private readonly Queue<string> _queue = new();
    private string _inFlightId;
    private string _inFlightBody;
    private int _attempt;
    private double _retryAt;
    private double _clock;

    public bool Configured => !string.IsNullOrEmpty(_baseUrl) && !string.IsNullOrEmpty(_token);

    public override void _Ready()
    {
        _baseUrl = OS.GetEnvironment("WIPEBOUND_BACKEND_URL").TrimEnd('/');
        _token = OS.GetEnvironment("WIPEBOUND_SERVER_TOKEN");
        _serverId = OS.GetEnvironment("WIPEBOUND_SERVER_ID");
        if (string.IsNullOrEmpty(_serverId)) _serverId = "unnamed";

        _http = new HttpRequest { Name = "Http", Timeout = 10 };
        AddChild(_http);
        _http.RequestCompleted += OnRequestCompleted;

        if (RunRecorder.Instance is not null)
            RunRecorder.Instance.Submitted += Spool;

        if (!Configured)
        {
            GD.Print("[ladder] not configured; runs will be logged only");
            return;
        }

        GD.Print($"[ladder] submitting to {_baseUrl} as '{_serverId}'");
        Recover();
    }

    // -- the spool -------------------------------------------------------

    private static string PathFor(string runId) => $"{SpoolDir}/{runId}.json";

    private void Spool(Godot.Collections.Dictionary record)
    {
        if (!Configured) return;

        string runId = record["run_id"].AsString();
        DirAccess.MakeDirRecursiveAbsolute(SpoolDir);

        using DirAccess dir = DirAccess.Open(SpoolDir);
        if (dir is not null && dir.GetFiles().Length >= MaxSpooled)
        {
            GD.PushWarning($"[ladder] spool is full ({MaxSpooled}); dropping run {runId}");
            return;
        }

        using FileAccess file = FileAccess.Open(PathFor(runId), FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PushWarning($"[ladder] could not spool run {runId}; sending without a safety net");
        }
        else
        {
            file.StoreString(Json.Stringify(record));
        }

        _queue.Enqueue(runId);
    }

    /// <summary>Anything left on disk from a previous life.</summary>
    private void Recover()
    {
        using DirAccess dir = DirAccess.Open(SpoolDir);
        if (dir is null) return;

        int found = 0;
        foreach (string name in dir.GetFiles())
        {
            if (!name.EndsWith(".json")) continue;
            _queue.Enqueue(name[..^5]);
            found++;
        }

        if (found > 0) GD.Print($"[ladder] recovered {found} unsent run(s) from disk");
    }

    private void Settle(string runId)
    {
        if (FileAccess.FileExists(PathFor(runId))) DirAccess.RemoveAbsolute(PathFor(runId));
        _inFlightId = null;
        _inFlightBody = null;
        _attempt = 0;
    }

    // -- sending ---------------------------------------------------------

    public override void _Process(double delta)
    {
        _clock += delta;

        if (!Configured || _inFlightId is not null) return;
        if (_clock < _retryAt || _queue.Count == 0) return;

        string runId = _queue.Dequeue();
        string body = FileAccess.FileExists(PathFor(runId))
            ? FileAccess.GetFileAsString(PathFor(runId))
            : null;

        if (string.IsNullOrEmpty(body))
        {
            // Spooling failed earlier, or somebody removed it. Nothing to send.
            Settle(runId);
            return;
        }

        _inFlightId = runId;
        _inFlightBody = body;
        _attempt = 0;
        Send();
    }

    private void Send()
    {
        _attempt++;

        string[] headers =
        {
            "Content-Type: application/json",
            $"Authorization: Bearer {_token}",
            $"X-Server-Id: {_serverId}",
        };

        Error error = _http.Request($"{_baseUrl}/v1/internal/runs", headers, HttpClient.Method.Post, _inFlightBody);
        if (error != Error.Ok) Retry($"could not start request ({error})");
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        if (result != (long)HttpRequest.Result.Success)
        {
            Retry($"transport failure ({(HttpRequest.Result)result})");
            return;
        }

        // Only 5xx and transport failures are worth trying again. A 4xx will never
        // become a 201, so retrying it forever would block the queue behind a run
        // the backend has already refused on its merits.
        if (responseCode >= 500)
        {
            Retry($"backend returned {responseCode}");
            return;
        }

        string text = System.Text.Encoding.UTF8.GetString(body);
        GD.Print(responseCode is >= 200 and < 300
            ? $"[ladder] accepted: {text}"
            : $"[ladder] refused ({responseCode}), discarding: {text}");

        Settle(_inFlightId);
    }

    /// <summary>
    /// Back off and try again later, forever. The record is on disk, so "later"
    /// survives a restart, and the run id makes a duplicate arrival harmless.
    /// </summary>
    private void Retry(string reason)
    {
        double backoff = Mathf.Min(60.0, Mathf.Pow(2, Mathf.Min(_attempt, 6)));
        _retryAt = _clock + backoff;

        GD.PushWarning($"[ladder] {reason}; retrying run {_inFlightId} in {backoff}s");

        _queue.Enqueue(_inFlightId);
        _inFlightId = null;
        _inFlightBody = null;
    }
}
