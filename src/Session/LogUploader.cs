using System.Collections.Generic;
using Godot;
using Wipebound.Net;

namespace Wipebound.Session;

/// <summary>
/// Ships combat logs to the backend, on their own schedule.
///
/// Separate from RunSubmitter because the two have different urgency and
/// different consequences. A run record is four numbers per player and the
/// ladder is waiting for it; a log is tens of kilobytes and nothing is. Sending
/// them together would make the ladder wait on the larger, and would mean a log
/// that cannot be accepted takes its run down with it.
///
/// The spool is already on disk: RunRecorder writes each fight to user://runs
/// before this ever sees it, so a server that dies mid-upload loses nothing and
/// a backend that is down for a day is caught up on in one pass.
/// </summary>
public partial class LogUploader : Node
{
    /// <summary>
    /// A ceiling on the spool, because this runs unattended.
    ///
    /// A backend that has been unreachable for a week must not fill the disk of
    /// a game server that is otherwise working perfectly. The OLDEST go first:
    /// recent fights are the ones somebody is waiting to look at, and a log
    /// nobody has asked about in a week is the cheapest thing to lose.
    /// </summary>
    private const int MaxSpooled = 500;

    /// <summary>
    /// How many times a log will wait for its run to catch up.
    ///
    /// The two are uploaded independently, so a log can arrive before the run
    /// record it belongs to and be told the run does not exist. That is not a
    /// verdict, it is a race -- and it happened on the very first end-to-end
    /// run of this pipeline. It resolves as soon as RunSubmitter's own queue
    /// drains, so the answer is to wait; the cap is there because a log whose
    /// run was refused outright would otherwise wait for ever.
    /// </summary>
    private const int MaxWaitsForRun = 12;

    /// Sent one at a time. Nothing is racing, and a queue that cannot overlap
    /// cannot deliver the same log twice under a slow connection.
    private HttpRequest _http;

    private readonly Queue<string> _queue = new();
    private readonly Dictionary<string, int> _attempts = new();

    private string _baseUrl;
    private string _token;
    private string _serverId;
    private string _inFlight;
    private double _retryAt;
    private double _clock;

    public bool Configured => !string.IsNullOrEmpty(_baseUrl) && !string.IsNullOrEmpty(_token);

    public override void _Ready()
    {
        _baseUrl = OS.GetEnvironment("WIPEBOUND_BACKEND_URL").TrimEnd('/');
        _token = OS.GetEnvironment("WIPEBOUND_SERVER_TOKEN");
        _serverId = OS.GetEnvironment("WIPEBOUND_SERVER_ID");

        _http = new HttpRequest { Name = "Http", Timeout = 30 };
        AddChild(_http);
        _http.RequestCompleted += OnRequestCompleted;

        Prune();
        Recover();
    }

    /// <summary>Pick up anything left from a previous life.</summary>
    private void Recover()
    {
        foreach (string runId in Spooled()) _queue.Enqueue(runId);
        if (_queue.Count > 0) GD.Print($"[logs] {_queue.Count} waiting to upload");
    }

    private static List<string> Spooled()
    {
        var found = new List<string>();
        using DirAccess dir = DirAccess.Open(RunRecorder.LogDirectory);
        if (dir is null) return found;

        foreach (string file in dir.GetFiles())
            if (file.EndsWith(".json.gz"))
                found.Add(file.Replace(".json.gz", ""));

        found.Sort();
        return found;
    }

    private static string PathFor(string runId) => $"{RunRecorder.LogDirectory}/{runId}.json.gz";

    /// <summary>
    /// Keep the spool bounded, oldest first.
    ///
    /// Run ids are random rather than ordered, so age comes from the filesystem
    /// rather than from the name.
    /// </summary>
    private void Prune()
    {
        List<string> spooled = Spooled();
        if (spooled.Count <= MaxSpooled) return;

        string directory = ProjectSettings.GlobalizePath(RunRecorder.LogDirectory);
        spooled.Sort((a, b) =>
            System.IO.File.GetLastWriteTimeUtc(System.IO.Path.Combine(directory, $"{a}.json.gz"))
                .CompareTo(System.IO.File.GetLastWriteTimeUtc(System.IO.Path.Combine(directory, $"{b}.json.gz"))));

        int excess = spooled.Count - MaxSpooled;
        for (int i = 0; i < excess; i++)
        {
            DirAccess.RemoveAbsolute(PathFor(spooled[i]));
            GD.PushWarning($"[logs] spool full, dropped {spooled[i]}");
        }
    }

    /// <summary>A fight just ended. It is already on disk; queue it.</summary>
    public void Offer(string runId)
    {
        if (!NetworkManager.Instance.IsServer) return;

        Prune();
        if (!_queue.Contains(runId) && _inFlight != runId) _queue.Enqueue(runId);
    }

    public override void _Process(double delta)
    {
        _clock += delta;

        if (!Configured || _inFlight is not null || _queue.Count == 0) return;
        if (_clock < _retryAt) return;

        _inFlight = _queue.Dequeue();
        Send();
    }

    private void Send()
    {
        byte[] body = FileAccess.FileExists(PathFor(_inFlight))
            ? FileAccess.GetFileAsBytes(PathFor(_inFlight))
            : null;

        if (body is null || body.Length == 0)
        {
            // Nothing to send and nothing to keep.
            Settle();
            return;
        }

        string[] headers =
        {
            "Content-Type: application/json",

            // Sent still compressed, exactly as it was written. The backend
            // stores these bytes and serves them back unchanged, so a replay
            // reads what the server wrote rather than a re-encoding of it.
            "Content-Encoding: gzip",
            $"Authorization: Bearer {_token}",
            $"X-Server-Id: {_serverId}",
        };

        Error error = _http.RequestRaw(
            $"{_baseUrl}/v1/internal/runs/{_inFlight}/log", headers, HttpClient.Method.Post, body);

        if (error != Error.Ok) Retry($"could not start request ({error})");
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        if (result != (long)HttpRequest.Result.Success)
        {
            Retry($"transport failure ({(HttpRequest.Result)result})");
            return;
        }

        // The same judgement RunSubmitter makes, for the same reason: a fault or
        // a rate limit is worth waiting out, and a verdict about the payload
        // never changes however many times it is sent.
        if (SubmissionPolicy.ShouldRetry(responseCode))
        {
            Retry($"backend returned {responseCode}");
            return;
        }

        // A log-specific case the shared policy has no business knowing about:
        // 404 here means the run has not landed YET, not that it never will.
        if (responseCode == 404 && Waited() < MaxWaitsForRun)
        {
            Retry("its run has not been accepted yet");
            return;
        }

        if (responseCode is < 200 or > 299)
            GD.PushWarning($"[logs] {_inFlight} refused with {responseCode}; dropping it");

        Settle();
    }

    /// <summary>Accepted, or refused in a way that will not change. Either way it goes.</summary>
    private void Settle()
    {
        if (_inFlight is null) return;

        DirAccess.RemoveAbsolute(PathFor(_inFlight));
        _attempts.Remove(_inFlight);
        _inFlight = null;
        _retryAt = 0.0;
    }

    private int Waited() => _attempts.TryGetValue(_inFlight, out int seen) ? seen : 0;

    private void Retry(string why)
    {
        int attempt = _attempts.TryGetValue(_inFlight, out int seen) ? seen + 1 : 1;
        _attempts[_inFlight] = attempt;

        _retryAt = _clock + SubmissionPolicy.BackoffFor(attempt);
        GD.PushWarning($"[logs] {_inFlight}: {why}; retrying in {SubmissionPolicy.BackoffFor(attempt):0}s");

        _queue.Enqueue(_inFlight);
        _inFlight = null;
    }
}
