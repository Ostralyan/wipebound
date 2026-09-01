using Godot;

namespace Wipebound.Session;

/// <summary>
/// When to try a run submission again, and how long to wait.
///
/// Pure, so the decisions are testable without a network. Both were wrong in the
/// first durable version: every response under 500 was discarded forever, and the
/// attempt counter reset on every dequeue so "exponential backoff" was a constant
/// two seconds.
/// </summary>
public static class SubmissionPolicy
{
    public const double FirstBackoffSeconds = 2.0;
    public const double MaxBackoffSeconds = 300.0;

    /// <summary>
    /// Whether this response leaves any hope. Server faults and rate limits pass;
    /// so do authentication failures, because a wrong token is a configuration
    /// mistake somebody can fix while the runs wait rather than a reason to throw
    /// away everything played before they noticed.
    ///
    /// A 400, 409 or 422 is a judgement about the payload itself and will never
    /// change, so retrying one forever would block the queue behind it.
    /// </summary>
    public static bool ShouldRetry(long statusCode) => statusCode switch
    {
        408 => true,   // request timeout
        429 => true,   // rate limited
        401 or 403 => true,   // misconfigured credential, fixable
        >= 500 => true,
        _ => false,
    };

    /// <summary>Doubling, from the first failure, capped so it stays plausible.</summary>
    public static double BackoffFor(int attempt)
    {
        int steps = Mathf.Clamp(attempt - 1, 0, 20);
        return Mathf.Min(MaxBackoffSeconds, FirstBackoffSeconds * Mathf.Pow(2, steps));
    }
}
