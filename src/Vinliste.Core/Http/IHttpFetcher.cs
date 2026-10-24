namespace Vinliste.Core.Http;

public sealed record FetchResponse(int StatusCode, string Body, string? RetryAfter = null)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// Fetches a URL as text. Core code only talks to the network through this
/// interface so tests can use canned responses and the app can route sites
/// that reject plain HTTP clients through a real browser engine.
/// </summary>
public interface IHttpFetcher
{
    Task<FetchResponse> GetAsync(string url, string accept, CancellationToken cancellationToken);
}
