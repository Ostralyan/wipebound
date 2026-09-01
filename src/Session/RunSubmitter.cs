using Godot;
using System.Collections.Generic;

namespace Wipebound.Session;

/// <summary>
/// Ships finished run records to the ladder backend.
///
/// Configured entirely from the environment, and silent when unconfigured, so a
/// developer running --host locally needs no backend and a client export can
/// never carry the submit credential. That last part is the whole point: if the
/// token shipped to players, dedicated servers would buy nothing.
///
/// Retries are safe because the game server generates the run id, so the backend
/// can recognise the same run arriving twice and insert nothing.
/// </summary>
public partial class RunSubmitter : Node
{
    private const int MaxAttempts = 4;

    private HttpRequest _http;
    private string _baseUrl;
    private string _token;
    private string _serverId;

    private readonly Queue<string> _queue = new();
    private string _inFlight;
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
            RunRecorder.Instance.Submitted += record => Enqueue(Json.Stringify(record));

        GD.Print(Configured
            ? $"[ladder] submitting to {_baseUrl} as '{_serverId}'"
            : "[ladder] not configured; runs will be logged only");
    }

    private void Enqueue(string body)
    {
        if (!Configured) return;
        _queue.Enqueue(body);
    }

    public override void _Process(double delta)
    {
        _clock += delta;

        if (!Configured || _inFlight is not null) return;
        if (_clock < _retryAt) return;
        if (_queue.Count == 0) return;

        _inFlight = _queue.Dequeue();
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

        Error error = _http.Request($"{_baseUrl}/v1/internal/runs", headers, HttpClient.Method.Post, _inFlight);
        if (error != Error.Ok) Failed($"could not start request ({error})");
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        if (result != (long)HttpRequest.Result.Success)
        {
            Failed($"transport failure ({(HttpRequest.Result)result})");
            return;
        }

        // 4xx means this run will never be accepted, so retrying is just noise.
        // Only 5xx and transport failures are worth trying again.
        if (responseCode >= 500)
        {
            Failed($"backend returned {responseCode}");
            return;
        }

        string text = System.Text.Encoding.UTF8.GetString(body);
        GD.Print(responseCode is >= 200 and < 300
            ? $"[ladder] accepted: {text}"
            : $"[ladder] refused ({responseCode}): {text}");

        _inFlight = null;
        _attempt = 0;
    }

    private void Failed(string reason)
    {
        if (_attempt >= MaxAttempts)
        {
            GD.PushWarning($"[ladder] giving up on a run after {_attempt} attempts: {reason}");
            _inFlight = null;
            return;
        }

        // Exponential, so a backend that is restarting is not hammered.
        double backoff = Mathf.Pow(2, _attempt);
        _retryAt = _clock + backoff;
        GD.PushWarning($"[ladder] {reason}; retrying in {backoff}s");

        // Re-queue rather than resend in place, so _Process owns the timing.
        _queue.Enqueue(_inFlight);
        _inFlight = null;
    }
}
