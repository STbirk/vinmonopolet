namespace Vinliste.Core.Ratings;

/// <summary>What we know about the wine being rated.</summary>
public sealed record WineIdentity
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    /// <summary>Producer from the product page. Often the owner or importer-facing company, not the brand.</summary>
    public string? Producer { get; init; }
    public string? Vintage { get; init; }
    public string? Country { get; init; }
    public string? District { get; init; }
    public string? SubDistrict { get; init; }
    /// <summary>Vinmonopolet category code ("rødvin", "hvitvin" …), used to tell red from white.</summary>
    public string? CategoryCode { get; init; }

    /// <summary>
    /// The name to search and compare with. Vinmonopolet names nearly always start with the
    /// brand; the product page's producer is often the owner ("André Lurton" for Château La
    /// Louvière), so it is only added when the name has no identifying word of its own
    /// ("Barbera d'Alba Superiore").
    /// </summary>
    public string FullName =>
        string.IsNullOrWhiteSpace(Producer) || WineText.DistinctiveTokens(Name).Count > 0 ? Name : $"{Producer} {Name}";

    /// <summary>
    /// "red", "white", "rose", or "not-rose" for sparkling and other wines whose name does not say
    /// rosé (a Champagne must not match the house's rosé), or null when unknown.
    /// </summary>
    public string? Colour => CategoryCode switch
    {
        "rødvin" => "red",
        "hvitvin" => "white",
        "rosévin" => "rose",
        null => WineText.ColourOf(Name),
        _ => WineText.ColourOf(Name) ?? "not-rose",
    };
}

/// <summary>A wine as a rating site names it.</summary>
public sealed record RatingCandidate
{
    public required string Name { get; init; }
    public string? Producer { get; init; }
    public string? Vintage { get; init; }
    public double? Score { get; init; }
    public int? RatingCount { get; init; }
    public string? Url { get; init; }
}

public sealed record MatchResult(RatingCandidate Candidate, double Confidence);

/// <summary>
/// Decides whether a rating-site wine is the Vinmonopolet wine. Stricter than plain fuzzy
/// matching: the producer must be present, vintages and colours must agree when both are
/// known, and wine-specific words (cuvée names, Riserva, second-wine names) must agree.
/// </summary>
public static class WineMatcher
{
    public static MatchResult? BestMatch(WineIdentity wine, IEnumerable<RatingCandidate> candidates, double threshold)
    {
        var wineVintage = wine.Vintage ?? WineText.ExtractVintage(wine.Name);
        (RatingCandidate Candidate, double Score)? best = null;
        foreach (var candidate in candidates)
        {
            if (Score(wine, candidate) is not { } score)
            {
                continue;
            }
            if (best is null || score > best.Value.Score + 0.01
                || (Math.Abs(score - best.Value.Score) <= 0.01 && Better(candidate, best.Value.Candidate, wineVintage)))
            {
                best = (candidate, score);
            }
        }
        return best is { } found && found.Score >= threshold ? new MatchResult(found.Candidate, found.Score) : null;
    }

    /// <summary>Best confidence even when below the threshold, for "closest miss" reporting.</summary>
    public static double BestConfidence(WineIdentity wine, IEnumerable<RatingCandidate> candidates) =>
        candidates.Select(c => Score(wine, c) ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>0–100 confidence, or null when the candidate is ruled out.</summary>
    public static double? Score(WineIdentity wine, RatingCandidate candidate) =>
        RawScore(wine, candidate) is { } raw ? Math.Round(Math.Clamp(raw, 0, 100), 1) : null;

    /// <summary>The words that must appear for a candidate to be from the same producer.</summary>
    public static IReadOnlyList<string> ProducerTokens(WineIdentity wine)
    {
        var nameTokens = WineText.Tokens(wine.Name).ToHashSet();
        // The producer's words that also appear in the name are the brand as Vinmonopolet writes it.
        var fromProducer = WineText.DistinctiveTokens(wine.Producer).Where(nameTokens.Contains).ToList();
        if (fromProducer.Count > 0)
        {
            return fromProducer;
        }
        // Otherwise the name's first identifying word ("Ressia", "Chiccheri"), or the producer's
        // when the name has none ("Barbera d'Alba Superiore" by Ressia).
        var fromName = WineText.DistinctiveTokens(wine.Name).Take(1).ToList();
        return fromName.Count > 0 ? fromName : WineText.DistinctiveTokens(wine.Producer).Take(1).ToList();
    }

    /// <summary>Unclamped score; ties at 100 are broken by vintage, then score, then rating count.</summary>
    private static double? RawScore(WineIdentity wine, RatingCandidate candidate)
    {
        var candidateName = WineText.WithoutParentheses(candidate.Name);
        var candidateProducer = candidate.Producer is null ? null : WineText.WithoutParentheses(candidate.Producer);
        var candidateText = $"{candidateProducer} {candidateName}";
        var candidateTokens = WineText.Tokens($"{candidate.Producer} {candidate.Name}").ToHashSet();
        if (candidateTokens.Count == 0)
        {
            return null;
        }

        var wineVintage = wine.Vintage ?? WineText.ExtractVintage(wine.Name);
        var candidateVintage = candidate.Vintage ?? WineText.ExtractVintage(candidate.Name)
            ?? (WineText.IsNonVintage(candidate.Name) ? "NV" : null);
        if (wineVintage is not null && candidateVintage is not null && wineVintage != candidateVintage)
        {
            return null;
        }

        // Every producer word has to be there: "Haut-Brion" alone is not "La Mission Haut-Brion".
        var producerTokens = ProducerTokens(wine);
        if (producerTokens.Any(t => !candidateTokens.Contains(t)))
        {
            return null;
        }

        // Red is never the producer's white or rosé of the same name, and a Champagne is not its rosé.
        var candidateColour = WineText.ColourOf(candidate.Name);
        if (wine.Colour is { } colour && candidateColour is not null
            && (colour == "not-rose" ? candidateColour == "rose" : colour != candidateColour))
        {
            return null;
        }

        // Numbered cuvées (Krug 171ème, Roederer Collection 244) must be the same number.
        var wineNumbers = WineText.CuveeNumbers(wine.Name);
        var candidateNumbers = WineText.CuveeNumbers(candidate.Name);
        if (wineNumbers.Count > 0 && candidateNumbers.Count > 0 && !wineNumbers.Overlaps(candidateNumbers))
        {
            return null;
        }

        var wineText = wine.FullName;
        var wineTokens = WineText.Tokens(wineText).ToHashSet();
        var similarity = TokenSetRatio(Meaningful(wineText), Meaningful(candidateText));

        // Wine-specific words the candidate lacks (e.g. "La Casa", "Sciulun") point to a sibling wine.
        var missing = WineText.DistinctiveTokens(wineText)
            .Count(t => !producerTokens.Contains(t) && !candidateTokens.Contains(t) && !WineText.NoiseWords.Contains(t));

        // Extra words the wine does not have point to a sibling (a cru, a cuvée, a second wine),
        // unless they are explained: the appellation, the producer as Vinmonopolet names it,
        // English region names, sample notes.
        var explained = WineText.Tokens($"{wine.District} {wine.SubDistrict} {wine.Country} {wine.Producer}").ToHashSet();
        var extras = WineText.DistinctiveTokens(candidateText)
            .Where(t => t.Length > 1 && !wineTokens.Contains(t) && !explained.Contains(t) && !WineText.NoiseWords.Contains(t))
            .ToList();
        var secondWine = extras.Count(WineText.SecondWineMarkers.Contains);
        var otherExtras = extras.Count - secondWine;

        // Riserva vs normale, Extra Brut vs Brut: a qualifier on one side only is a different wine.
        var qualifierMismatch = WineText.Qualifiers.Count(q => wineTokens.Contains(q) != candidateTokens.Contains(q));

        // One unexplained word must take a perfect name below the default threshold (82): with
        // equal names, "Gevrey-Chambertin" and "Gevrey-Chambertin Les Cazetiers" differ only by it.
        return similarity - 20 * missing - 20 * otherExtras - 30 * secondWine - 25 * qualifierMismatch;
    }

    /// <summary>Normalised text without stopwords, so "Tinto", "Domaine" or "Brut" do not dilute similarity.</summary>
    private static string Meaningful(string text)
    {
        var tokens = WineText.Tokens(text).Where(t => !WineText.Stopwords.Contains(t)).ToList();
        return tokens.Count > 0 ? string.Join(' ', tokens) : WineText.Normalize(text);
    }


    /// <summary>Token-set similarity (as in fuzzywuzzy's token_set_ratio), 0–100.</summary>
    public static double TokenSetRatio(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }
        var common = string.Join(' ', a.Intersect(b).Order());
        var onlyA = string.Join(' ', a.Except(b).Order());
        var onlyB = string.Join(' ', b.Except(a).Order());
        var withA = (common + " " + onlyA).Trim();
        var withB = (common + " " + onlyB).Trim();
        return new[] { Ratio(common, withA), Ratio(common, withB), Ratio(withA, withB) }.Max() * (common.Length == 0 ? 0.6 : 1);
    }

    /// <summary>Levenshtein similarity, 0–100.</summary>
    public static double Ratio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 100;
        }
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        var distance = previous[b.Length];
        return (1 - (double)distance / Math.Max(a.Length, b.Length)) * 100;
    }

    /// <summary>
    /// Tie-break between equally good candidates: the wine's own vintage (unless it has no score
    /// yet and the other does), then a scored candidate, then more ratings.
    /// </summary>
    private static bool Better(RatingCandidate candidate, RatingCandidate current, string? wineVintage)
    {
        var candidateIsVintage = wineVintage is not null && candidate.Vintage == wineVintage;
        var currentIsVintage = wineVintage is not null && current.Vintage == wineVintage;
        if (candidateIsVintage != currentIsVintage)
        {
            var (vintageOne, other) = candidateIsVintage ? (candidate, current) : (current, candidate);
            var vintageWins = vintageOne.Score is not null || other.Score is null;
            return candidateIsVintage == vintageWins;
        }
        if ((candidate.Score is not null) != (current.Score is not null))
        {
            return candidate.Score is not null;
        }
        return (candidate.RatingCount ?? 0) > (current.RatingCount ?? 0);
    }
}
