using System.Net;
using Microsoft.Extensions.Logging;

namespace Lumen.Providers.Http;

/// <summary>
/// Retries idempotent (GET) requests up to twice on transient failures (5xx, 408, 429,
/// connection errors, per-attempt timeouts) with jittered backoff.
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] BaseDelays = [TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(900)];

    private readonly ILogger<TransientRetryHandler> _logger;

    public TransientRetryHandler(ILogger<TransientRetryHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (attempt < MaxAttempts && IsTransient(response.StatusCode))
                {
                    _logger.LogDebug(
                        "Transient status {Status} from {Uri}; retrying (attempt {Attempt}/{Max})",
                        (int)response.StatusCode, request.RequestUri, attempt, MaxAttempts);
                    response.Dispose();
                    await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _logger.LogDebug(ex, "Transient failure calling {Uri}; retrying (attempt {Attempt}/{Max})",
                    request.RequestUri, attempt, MaxAttempts);
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode >= 500 ||
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseDelay = BaseDelays[Math.Min(attempt, BaseDelays.Length) - 1];
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        return Task.Delay(baseDelay + jitter, cancellationToken);
    }
}
