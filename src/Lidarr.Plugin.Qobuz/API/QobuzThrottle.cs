using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QobuzApiSharp.Exceptions;

namespace NzbDrone.Plugin.Qobuz.API;

// Process-wide pacing for calls to the Qobuz JSON API (album/track metadata, file-URL lookups).
// Every caller goes through one gate, so the spacing holds no matter how many albums or tracks
// are downloading at once, and a 429 from any of them pauses all of them.
//
// Indexer search pages don't pass through here: they go through Lidarr's own HttpClient, which is
// paced by the indexer's RateLimit (see the indexer's "Search Request Delay" setting).
public static class QobuzThrottle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object Lock = new();
    private static DateTime _nextAllowedUtc = DateTime.MinValue;
    private static TimeSpan _minInterval = TimeSpan.FromMilliseconds(1000);

    public static TimeSpan MinInterval
    {
        get { lock (Lock) return _minInterval; }
        set { lock (Lock) _minInterval = value < TimeSpan.Zero ? TimeSpan.Zero : value; }
    }

    public static async Task WaitAsync(CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            TimeSpan wait;
            lock (Lock)
                wait = _nextAllowedUtc - DateTime.UtcNow;

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, token);

            lock (Lock)
            {
                var next = DateTime.UtcNow + _minInterval;
                if (next > _nextAllowedUtc)
                    _nextAllowedUtc = next;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    // For the synchronous call sites (QobuzApiSharp's API is sync; the indexer parser is sync).
    public static void Wait() => WaitAsync().GetAwaiter().GetResult();

    // Push the next allowed call out by at least `cooldown`, for every caller.
    public static void BackOff(TimeSpan cooldown)
    {
        lock (Lock)
        {
            var until = DateTime.UtcNow + cooldown;
            if (until > _nextAllowedUtc)
                _nextAllowedUtc = until;
        }
    }

    // True for Qobuz telling us to slow down: HTTP 429 from the API or the CDN. QobuzApiSharp only
    // keeps the status when the error body is Qobuz's JSON; a non-JSON 429 page surfaces as a parse
    // error carrying the raw body, so that case is matched on the body text.
    public static bool IsRateLimited(Exception ex) => ex switch
    {
        null => false,
        ApiErrorResponseException api => api.ResponseStatusCode == "429" || MentionsTooManyRequests(api.ResponseReason) || IsRateLimited(api.InnerException),
        ApiResponseParseErrorException parse => MentionsTooManyRequests(parse.ResponseContent) || IsRateLimited(parse.InnerException),
        HttpRequestException http => http.StatusCode == HttpStatusCode.TooManyRequests,
        _ => IsRateLimited(ex.InnerException),
    };

    private static bool MentionsTooManyRequests(string text)
        => !string.IsNullOrEmpty(text) && text.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
}
