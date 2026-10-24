namespace Vinliste.Core.Models;

public static class RatingStatus
{
    public const string Matched = "matched";
    public const string MatchedNoScore = "matched_no_score";
    public const string NoMatch = "no_match";
    public const string Blocked = "blocked";
    public const string Error = "error";
}

/// <summary>One rating source's result for one wine.</summary>
public sealed record RatingResult
{
    public required string Code { get; init; }
    public required string Source { get; init; }
    public required string Status { get; init; }
    /// <summary>Score on the source's native scale (Vivino 0–5, others 0–100).</summary>
    public double? Score { get; init; }
    /// <summary>Score normalised to 0–100.</summary>
    public double? Score100 { get; init; }
    public int? RatingCount { get; init; }
    public string? MatchedName { get; init; }
    public string? MatchedVintage { get; init; }
    public string? Url { get; init; }
    public double? MatchConfidence { get; init; }
    public string? SearchQuery { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}
