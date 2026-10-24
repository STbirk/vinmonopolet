using System.Net;

namespace Vinliste.Core.Http;

/// <summary>Plain HttpClient transport with cookies, a browser User-Agent and retry on 429/5xx.</summary>
public sealed class HttpClientFetcher : IHttpFetcher, IDisposable
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly HttpClient _client;
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public HttpClientFetcher(
        TimeSpan? timeout = null,
        int maxAttempts = 4,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        // HTTP/2 matters: Yahoo answers HTTP/1.1 clients with errors.
        _client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,nb;q=0.8");
        _maxAttempts = maxAttempts;
        _delay = delay ?? Task.Delay;
    }

    public async Task<FetchResponse> GetAsync(string url, string accept, CancellationToken cancellationToken)
    {
        FetchResponse? last = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            try
            {
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                last = new FetchResponse((int)response.StatusCode, body, retryAfter);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // IOException: the connection dropped while reading the body.
                last = new FetchResponse(0, ex.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new FetchResponse(0, "Request timed out");
            }

            if (!ShouldRetry(last.StatusCode) || attempt == _maxAttempts)
            {
                return last;
            }
            await _delay(RetryDelay(last, attempt), cancellationToken).ConfigureAwait(false);
        }
        return last!;
    }

    internal static bool ShouldRetry(int statusCode) => statusCode is 0 or 429 or >= 500;

    internal static TimeSpan RetryDelay(FetchResponse response, int attempt)
    {
        if (response.RetryAfter is not null
            && double.TryParse(response.RetryAfter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds + 1, 1, 600));
        }
        return TimeSpan.FromSeconds(Math.Min(60, 5 * attempt));
    }

    public void Dispose() => _client.Dispose();
}
