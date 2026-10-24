using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vinliste.Core.Ratings;

/// <summary>Normalisation shared by query building and matching.</summary>
public static partial class WineText
{
    /// <summary>Words that say nothing about which wine it is.</summary>
    public static readonly HashSet<string> Stopwords =
    [
        "a", "al", "and", "the", "of", "d", "de", "del", "della", "dei", "degli", "di", "da", "do", "dos", "das",
        "du", "des", "la", "le", "les", "lo", "il", "el", "en", "et", "y", "e", "von", "vom", "van", "der",
        "ch", "chateau", "cht", "dom", "domaine", "tenuta", "ten", "castello", "cantina", "cantine", "bodega",
        "bodegas", "weingut", "azienda", "agricola", "fattoria", "podere", "estate", "winery", "wines", "wine",
        "vin", "vino", "vini", "vinho", "red", "rouge", "rosso", "tinto", "blanc", "bianco", "blanco", "white",
        "rose", "rosato", "rosado", "doc", "docg", "aoc", "aop", "igt", "igp", "doca", "vdp", "cuvee",
        "caves", "cave",
        // Classification wording: sites write it inconsistently ("1er Cru" / "Premier Cru", "Grand Cru
        // Classé" or nothing). The climat or cuvée name next to it is what tells wines apart.
        "grand", "grande", "premier", "1er", "cru", "crus", "classe", "brut",
        // Corporate words in producer names ("Symington Family Est.", "Soc. Agr. Bellavista").
        "est", "estate", "estates", "family", "familia", "soc", "agr", "spa", "sas", "sarl", "gmbh", "ltd",
        "inc", "brands", "vineyards", "vignobles", "domaines", "group", "schlossgut", "weing",
    ];

    /// <summary>Words that separate one wine from its sibling (Riserva vs the normale).</summary>
    public static readonly HashSet<string> Qualifiers =
    [
        "riserva", "reserva", "reserve", "gran", "superiore", "crianza", "spatlese", "auslese", "kabinett",
        "smaragd", "federspiel", "ripasso", "amarone", "appassimento",
        // Dosage: Extra Brut, Brut Nature and Dosage Zéro are different wines from the Brut.
        "extra", "nature", "zero",
    ];

    /// <summary>Words that mark a château's second or third wine.</summary>
    public static readonly HashSet<string> SecondWineMarkers =
    [
        "moulin", "chapelle", "petit", "petite", "pagodes", "carruades", "pavillon", "fiefs", "dame", "second",
        "seconde", "alter", "ego", "clarence", "tourelles", "connetable", "ygrec",
    ];

    /// <summary>
    /// Words a rating site adds that say nothing about which wine it is: English region and
    /// country names (Vinmonopolet writes them in Norwegian), tasting-sample notes and the like.
    /// </summary>
    public static readonly HashSet<string> NoiseWords =
    [
        "france", "italy", "spain", "portugal", "germany", "austria", "australia", "chile", "argentina", "usa",
        "california", "oregon", "washington", "new", "zealand", "south", "africa", "greece", "hungary", "lebanon",
        "piedmont", "tuscany", "burgundy", "loire", "alsace", "veneto", "sicily", "napa", "sonoma", "provence",
        "languedoc", "roussillon", "bordeaux", "barrel", "sample", "style", "blancs", "noirs", "vineyard",
        "vineyards", "organic", "bio", "vv", "wines", "winery", "selection", "valley", "coast", "hills",
        "vigna", "vigneto", "lieu", "dit",
    ];

    private static readonly HashSet<string> RedWords = ["rouge", "rosso", "tinto", "red"];
    private static readonly HashSet<string> WhiteWords = ["blanc", "bianco", "blanco", "white", "weiss"];
    private static readonly HashSet<string> RoseWords = ["rose", "rosato", "rosado"];

    /// <summary>"red", "white", "rose" or null from colour words, ignoring "Blanc de Blancs/Noirs" (a sparkling style).</summary>
    public static string? ColourOf(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Contains("blanc de", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("blanc de blancs", " ").Replace("blanc de noirs", " ");
        }
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(RoseWords.Contains))
        {
            return "rose";
        }
        var red = tokens.Any(RedWords.Contains);
        var white = tokens.Any(WhiteWords.Contains);
        return red == white ? null : red ? "red" : "white";
    }

    /// <summary>Cuvée numbers ("Collection 244", "171ème"), which must agree when both sides have one.</summary>
    public static IReadOnlySet<string> CuveeNumbers(string? text) =>
        Tokens(text).Select(t => CuveeNumberRegex().Match(t)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>Text without parenthesised parts, which hold appellations and classifications on rating sites.</summary>
    public static string WithoutParentheses(string value) => CollapseSpaces(ParenthesesRegex().Replace(value, " "));

    public static bool IsNonVintage(string? value) =>
        !string.IsNullOrEmpty(value) && NonVintageRegex().IsMatch(value);

    /// <summary>Grape and region words: shared by many wines, so they cannot identify one.</summary>
    public static readonly HashSet<string> Generic =
    [
        "barbera", "nebbiolo", "sangiovese", "cabernet", "sauvignon", "merlot", "pinot", "noir", "syrah", "shiraz",
        "grenache", "garnacha", "tempranillo", "malbec", "zinfandel", "primitivo", "chardonnay", "riesling",
        "franc", "blend", "alba", "asti", "langhe", "barolo", "barbaresco", "chianti", "classico", "montalcino",
        "brunello", "bordeaux", "bourgogne", "burgundy", "rioja", "ribera", "duero", "medoc", "haut", "saint",
        "st", "emilion", "valpolicella", "rhone", "cotes", "napa", "valley", "toscana", "piemonte", "mosel",
        "champagne", "prosecco", "cava", "porto", "port", "douro",
    ];

    public static string FoldDiacritics(string value)
    {
        var replaced = value
            .Replace("ø", "o").Replace("Ø", "O").Replace("æ", "ae").Replace("Æ", "AE")
            .Replace("å", "a").Replace("Å", "A").Replace("ß", "ss").Replace("œ", "oe").Replace("Œ", "OE");
        var normalized = replaced.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Expands Vinmonopolet's abbreviations ("Ch.", "Dom.", "Ten." …).</summary>
    public static string ExpandAbbreviations(string value)
    {
        foreach (var (pattern, replacement) in Abbreviations)
        {
            value = pattern.Replace(value, replacement);
        }
        return CollapseSpaces(value);
    }

    /// <summary>Removes bottle-size tokens ("5l", "150 cl", "Magnum") that sites never use in names.</summary>
    public static string StripVolume(string value) => CollapseSpaces(VolumeRegex().Replace(value, " "));

    public static string StripVintage(string value) => CollapseSpaces(VintageRegex().Replace(value, " "));

    public static string? ExtractVintage(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        var match = VintageRegex().Match(value);
        return match.Success ? match.Value : null;
    }

    /// <summary>Lower-case ASCII words with volume, vintage and punctuation removed.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        var text = FoldDiacritics(ExpandAbbreviations(value)).ToLowerInvariant();
        text = VolumeRegex().Replace(text, " ");
        text = VintageRegex().Replace(text, " ");
        text = NonWordRegex().Replace(text, " ");
        return CollapseSpaces(text);
    }

    public static IReadOnlyList<string> Tokens(string? value) =>
        Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Tokens that identify the wine: not stopwords, not grape/region words, not numbers.</summary>
    public static IReadOnlyList<string> DistinctiveTokens(string? value) =>
        Tokens(value).Where(IsDistinctive).Distinct().ToList();

    public static bool IsDistinctive(string token) =>
        token.Length > 2 && !Stopwords.Contains(token) && !Generic.Contains(token) && !Qualifiers.Contains(token)
        && !token.All(char.IsDigit);

    /// <summary>ASCII-only text for search engine queries.</summary>
    public static string AsciiQuery(string value) =>
        CollapseSpaces(new string(FoldDiacritics(value).Where(c => c < 128).ToArray()));

    public static string CollapseSpaces(string value) => SpacesRegex().Replace(value, " ").Trim();

    private static readonly (Regex Pattern, string Replacement)[] Abbreviations =
    [
        (new Regex(@"\bCh(?:t)?\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Château "),
        (new Regex(@"\bDom\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Domaine "),
        (new Regex(@"\bTen\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Tenuta "),
        (new Regex(@"\bBod\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Bodegas "),
        (new Regex(@"\bCant\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Cantina "),
        (new Regex(@"\bCast\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Castello "),
        (new Regex(@"\bAz\.\s*Agr\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Azienda Agricola "),
        (new Regex(@"\bSt\.\s+(?=[A-ZÉ])", RegexOptions.Compiled), "Saint "),
        (new Regex(@"\bRis\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Riserva "),
        (new Regex(@"\bSup\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Superiore "),
        (new Regex(@"\bV\.\s*V\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Vieilles Vignes "),
        (new Regex(@"\bEst\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Estate "),
    ];

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex VintageRegex();

    // Also an unclosed "(Saint…" left by a truncated search title.
    [GeneratedRegex(@"\([^)]*(?:\)|$)")]
    private static partial Regex ParenthesesRegex();

    [GeneratedRegex(@"^(\d{2,3})(?:e|eme|th|st|nd|rd)?$")]
    private static partial Regex CuveeNumberRegex();

    [GeneratedRegex(@"(?:^|\s)(?:N\.?V\.?|non[- ]vintage)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NonVintageRegex();

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\s*(?:cl|ml|l|liter|litre)\b|\b(?:dbl\.?\s*|double\s+)?magnum\b|\bjeroboam\b|\bhalvflaske\b", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}
