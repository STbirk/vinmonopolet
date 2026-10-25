using System.Net;
using Vinliste.Core.Data;
using Vinliste.Core.Http;
using Vinliste.Core.Models;
using Vinliste.Core.Ratings;

namespace Vinliste.Core.Tests;

public class WineTextTests
{
    [Theory]
    [InlineData("Ch. Fourcas Dupré 2021", "Château Fourcas Dupré 2021")]
    [InlineData("Dom. de la Butte Bourgueil", "Domaine de la Butte Bourgueil")]
    [InlineData("Ten. Chiccheri Valpolicella", "Tenuta Chiccheri Valpolicella")]
    [InlineData("Barolo Ris. 2016", "Barolo Riserva 2016")]
    public void ExpandAbbreviations(string input, string expected) =>
        Assert.Equal(expected, WineText.ExpandAbbreviations(input));

    [Theory]
    [InlineData("Franco Conterno Bussia Riserva 5l 2011", "Franco Conterno Bussia Riserva 2011")]
    [InlineData("Cascina Sciulun Langhe Nebbiolo 9 l 2019", "Cascina Sciulun Langhe Nebbiolo 2019")]
    [InlineData("Barolo 150cl", "Barolo")]
    [InlineData("Barolo Magnum", "Barolo")]
    public void StripVolume(string input, string expected) =>
        Assert.Equal(expected, WineText.StripVolume(input));

    [Fact]
    public void Normalize_folds_norwegian_and_accents_and_drops_years()
    {
        Assert.Equal("chateau beausejour les hauts", WineText.Normalize("Château Beauséjour Les Hauts 2001"));
        Assert.Equal("rodvin aerlig", WineText.Normalize("Rødvin Ærlig"));
    }

    [Fact]
    public void DistinctiveTokens_skip_stopwords_grapes_and_qualifiers()
    {
        Assert.Equal(["ressia", "gepu"], WineText.DistinctiveTokens("Ressia Gepù Langhe Nebbiolo Riserva"));
    }
}

public class WineMatcherTests
{
    private static WineIdentity Wine(string name, string? producer = null, string? vintage = null) =>
        new() { Code = "x", Name = name, Producer = producer, Vintage = vintage ?? WineText.ExtractVintage(name) };

    private static RatingCandidate Candidate(string name, string? vintage = null, double? score = 90, string? producer = null) =>
        new() { Name = name, Vintage = vintage ?? WineText.ExtractVintage(name), Score = score, Producer = producer };

    [Fact]
    public void Exact_name_matches_with_high_confidence()
    {
        var match = WineMatcher.BestMatch(Wine("Ressia Gepù Langhe Nebbiolo 2021"), [Candidate("Ressia Gepu Langhe Nebbiolo")], 82);

        Assert.NotNull(match);
        Assert.True(match.Confidence >= 95);
    }

    [Fact]
    public void Different_vintage_is_rejected()
    {
        var wine = Wine("Castello di Radda Chianti Classico 2023", "Castello di Radda");

        Assert.Null(WineMatcher.BestMatch(wine, [Candidate("Castello di Radda 2022 Chianti Classico DOCG")], 82));
        Assert.NotNull(WineMatcher.BestMatch(wine, [Candidate("Castello di Radda 2023 Chianti Classico DOCG")], 82));
    }

    [Fact]
    public void Other_producer_is_rejected_even_when_the_rest_is_identical()
    {
        var wine = Wine("Barbera d'Alba Superiore 2021", "Ressia");

        Assert.Null(WineMatcher.Score(wine, Candidate("Réva Barbera d'Alba Superiore 2021")));
    }

    [Fact]
    public void Cuvee_name_must_be_present_so_the_generic_sibling_loses()
    {
        var wine = Wine("Caparzo Brunello di Montalcino Vigna La Casa 2021", "Caparzo");
        var generic = Candidate("Caparzo Brunello di Montalcino", score: 4.2);
        var cuvee = Candidate("Caparzo La Casa Brunello di Montalcino", score: 4.3);

        Assert.Null(WineMatcher.BestMatch(wine, [generic], 82));
        Assert.Same(cuvee, WineMatcher.BestMatch(wine, [generic, cuvee], 82)!.Candidate);
    }

    [Fact]
    public void Riserva_does_not_match_the_normale()
    {
        var wine = Wine("Franco Conterno Barolo Bussia 2016", "Franco Conterno");

        var normale = WineMatcher.Score(wine, Candidate("Franco Conterno 2016 Barolo Bussia"))!.Value;
        var riserva = WineMatcher.Score(wine, Candidate("Franco Conterno 2016 Barolo Riserva Bussia"))!.Value;

        Assert.True(normale > riserva + 5, $"normale {normale} vs riserva {riserva}");
        Assert.True(riserva < 82, $"riserva {riserva} would pass the default threshold");
    }

    [Fact]
    public void Producer_from_details_lets_short_listing_names_match()
    {
        // Vinmonopolet's name omits "Barolo" (a generic word) and has the bottle size.
        var wine = Wine("Franco Conterno Bussia Riserva 5l 2011", "Franco Conterno");

        Assert.NotNull(WineMatcher.BestMatch(wine, [Candidate("Franco Conterno Barolo Riserva Bussia 2011")], 82));
        // Deliberately conservative: one unexplained extra word ("Munie", a vineyard here) cannot be
        // told apart from a different cuvée ("Gevrey … Les Cazetiers"), so it is not accepted.
        Assert.Null(WineMatcher.BestMatch(wine, [Candidate("Franco Conterno Barolo Bussia Munie Riserva 2011")], 82));
    }

    [Fact]
    public void Abbreviated_tenuta_matches()
    {
        var wine = Wine("Ten. Chiccheri Valpolicella Superiore 2017");

        Assert.NotNull(WineMatcher.BestMatch(wine, [Candidate("Tenuta Chiccheri Valpolicella Superiore")], 82));
    }

    [Fact]
    public void Every_producer_word_is_required()
    {
        // "Haut-Brion" alone is not "La Mission Haut-Brion".
        var wine = Wine("Ch. La Mission Haut-Brion 2018", "Château La Mission Haut-Brion");

        Assert.Null(WineMatcher.Score(wine, Candidate("2018 Château Haut-Brion")));
        Assert.NotNull(WineMatcher.BestMatch(wine, [Candidate("2018 Château La Mission Haut-Brion")], 82));
    }

    [Fact]
    public void Second_wine_loses_to_the_grand_vin_and_is_rejected_alone()
    {
        var wine = Wine("Ch. la Lagune 2015", "Château La Lagune") with { District = "Bordeaux", SubDistrict = "Haut-Médoc" };
        var second = Candidate("Moulin du Château La Lagune", vintage: "2015");
        var grand = Candidate("Château La Lagune Haut-Médoc (3ème Grand Cru Classé en 1855)", vintage: "2015");

        Assert.Same(grand, WineMatcher.BestMatch(wine, [second, grand], 82)!.Candidate);
        Assert.Null(WineMatcher.BestMatch(wine, [second], 82));
    }

    [Fact]
    public void Vintage_rating_beats_all_vintages_rating()
    {
        var wine = Wine("Didier Dagueneau Silex 2021", "Didier Dagueneau");
        var allVintages = new RatingCandidate { Name = "Didier Dagueneau Silex", Score = 4.4, RatingCount = 9000 };
        var vintage = new RatingCandidate { Name = "Didier Dagueneau Silex 2021", Vintage = "2021", Score = 4.5, RatingCount = 120 };

        Assert.Same(vintage, WineMatcher.BestMatch(wine, [allVintages, vintage], 82)!.Candidate);
    }

    [Fact]
    public void Classification_wording_does_not_matter()
    {
        var wine = Wine("Buisson-Charles Corton-Clos du Roi Grand Cru 2017");

        Assert.NotNull(WineMatcher.BestMatch(wine, [Candidate("2017 Buisson-Charles Corton-Clos du Roi")], 82));
        Assert.NotNull(WineMatcher.BestMatch(Wine("Domaine X Meursault 1er Cru Charmes 2020"),
            [Candidate("Domaine X Meursault Premier Cru Charmes 2020")], 82));
    }

    [Fact]
    public void Colour_must_agree_with_the_wine_type()
    {
        var white = Wine("Bader-Mimeur Ch. de Chassagne-Montrachet 2020") with { CategoryCode = "hvitvin" };

        Assert.Null(WineMatcher.Score(white, Candidate("Bader-Mimeur Château de Chassagne-Montrachet Rouge 2020")));
        Assert.NotNull(WineMatcher.Score(white, Candidate("Bader-Mimeur Château de Chassagne-Montrachet Blanc 2020")));
        // Blanc de Blancs is a sparkling style, not a colour clash for anything.
        Assert.Null(WineText.ColourOf("Battenfeld Spanier Blanc de Blancs Extra Brut"));
    }

    [Fact]
    public void Bracketed_appellation_counts_towards_the_name()
    {
        // Wine Enthusiast titles: found live, rejected at 58% while the appellation in brackets was dropped.
        var montosoli = Wine("Altesino Brunello di Montalcino Montosoli 2019", "Altesino") with { CategoryCode = "rødvin" };
        var normale = Wine("Altesino Brunello di Montalcino 2019", "Altesino") with { CategoryCode = "rødvin" };
        var montosoliReview = Candidate("Altesino 2019 Vigna Montosoli Sangiovese (Brunello di Montalcino)");
        var normaleReview = Candidate("Altesino 2019 Sangiovese (Brunello di Montalcino)");

        Assert.True(WineMatcher.Score(montosoli, montosoliReview) >= 95);
        Assert.True(WineMatcher.Score(normale, normaleReview) >= 95);
        // The siblings still do not cross.
        Assert.True(WineMatcher.Score(montosoli, normaleReview) is null or < 82);
        Assert.True(WineMatcher.Score(normale, montosoliReview) is null or < 82);
    }

    [Fact]
    public void Grape_names_reveal_colour_when_no_colour_word_is_given()
    {
        // Found live: Wine Enthusiast's Pinot Noir from the same château was accepted for the white.
        var white = Wine("Bader-Mimeur Ch. de Chassagne-Montrachet 2022", "Bader-Mimeur") with { CategoryCode = "hvitvin" };

        Assert.Null(WineMatcher.Score(white, Candidate("Bader-Mimeur 2022 Château de Chassagne-Montrachet Pinot Noir")));
        Assert.Equal("white", WineText.ColourOf("Backhouse Chardonnay 2023"));
        Assert.Equal("red", WineText.ColourOf("Bründlmayer Pinot Noir"));
        // A red grape co-fermented with a white one says nothing about the colour.
        Assert.Null(WineText.ColourOf("Côte-Rôtie Syrah Viognier"));
    }

    [Fact]
    public void Owner_as_producer_does_not_block_the_brand()
    {
        // Vinmonopolet lists André Lurton as producer of Château La Louvière.
        var wine = Wine("Ch. La Louvière 2016", "André Lurton");

        Assert.Equal("Ch. La Louvière 2016", wine.FullName);
        Assert.NotNull(WineMatcher.BestMatch(wine, [Candidate("2016 Château La Louvière")], 82));
    }

    [Fact]
    public void Non_vintage_candidate_does_not_match_a_vintage_wine()
    {
        var wine = Wine("Bellavista Franciacorta Satèn 2020", "Bellavista");

        Assert.Null(WineMatcher.Score(wine, Candidate("NV Bellavista Franciacorta Gran Cuvée Satèn")));
    }

    // Siblings that share the wine's whole name plus one word (a cru, a cuvée, a second wine):
    // all found passing by the verification run, all must now fall below the default threshold.
    [Theory]
    [InlineData("Domaine Faiveley Gevrey-Chambertin 2019", "Domaine Faiveley", "rødvin", "2019 Domaine Faiveley Gevrey-Chambertin 1er Cru Les Cazetiers")]
    [InlineData("Domaine Faiveley Gevrey-Chambertin Les Cazetiers 2019", "Domaine Faiveley", "rødvin", "2019 Domaine Faiveley Gevrey-Chambertin")]
    [InlineData("Gaja Barbaresco 2019", "Gaja", "rødvin", "2019 Gaja Barbaresco Sorì Tildìn")]
    [InlineData("Ch. Figeac 2016", "Château Figeac", "rødvin", "2016 Château La Tour Figeac")]
    [InlineData("Ch. Talbot 2016", "Château Talbot", "rødvin", "2016 Connétable de Talbot")]
    [InlineData("Ch. Haut-Brion 2018", "Château Haut-Brion", "rødvin", "2018 Château La Mission Haut-Brion")]
    [InlineData("Ch. Margaux 2015", "Château Margaux", "rødvin", "2015 Château Brane-Cantenac Margaux")]
    [InlineData("Trimbach Riesling 2021", "Trimbach", "hvitvin", "2021 Trimbach Riesling Cuvée Frédéric Emile")]
    [InlineData("Dom Pérignon 2013", "Moët & Chandon", "musserende_vin", "2013 Dom Pérignon Rosé")]
    [InlineData("Bollinger Special Cuvée Brut", "Bollinger", "musserende_vin", "Bollinger Special Cuvée Extra Brut")]
    [InlineData("Louis Roederer Collection 244", "Louis Roederer", "musserende_vin", "Louis Roederer Collection 243")]
    [InlineData("E. Guigal Côtes du Rhône Rouge 2020", "E. Guigal", "rødvin", "E. Guigal Côtes du Rhône Rosé 2020")]
    [InlineData("William Fèvre Chablis Grand Cru Les Clos 2020", "William Fèvre", "hvitvin", "2020 William Fèvre Chablis")]
    public void One_word_siblings_are_rejected(string name, string producer, string category, string candidate)
    {
        var wine = Wine(name, producer) with { CategoryCode = category };

        var score = WineMatcher.Score(wine, Candidate(candidate));

        Assert.True(score is null or < 82, $"{candidate} scored {score}");
    }

    [Theory]
    [InlineData("Didier Dagueneau Silex 2021", "Didier Dagueneau", "Silex 2021", "Didier Dagueneau (Louis-Benjamin Dagueneau)")]
    [InlineData("Symington Altano Douro Tinto 2022", "Symington Family Est.", "2022 Symington Douro Altano", null)]
    [InlineData("Ch. La Gaffelière 2018", "Château La Gaffelière", "Château La Gaffelière Saint-Émilion Grand Cru (Premier Grand Cru Classé) 2018", null)]
    [InlineData("Ressia Gepù Langhe Nebbiolo 2021", null, "Ressia Gepu Langhe Nebbiolo", null)]
    public void Real_matches_still_pass(string name, string? producer, string candidate, string? candidateProducer)
    {
        var wine = Wine(name, producer) with { CategoryCode = "rødvin", District = "Bordeaux", SubDistrict = "Saint-Émilion" };

        var score = WineMatcher.Score(wine, Candidate(candidate, producer: candidateProducer));

        Assert.True(score >= 82, $"{candidate} scored {score}");
    }

    [Fact]
    public void Candidate_with_score_wins_ties()
    {
        var wine = Wine("Ressia Gepu Langhe Nebbiolo");
        var unscored = Candidate("Ressia Gepu Langhe Nebbiolo", score: null);
        var scored = Candidate("Ressia Gepu Langhe Nebbiolo", score: 3.9);

        Assert.Same(scored, WineMatcher.BestMatch(wine, [unscored, scored], 82)!.Candidate);
    }

    [Fact]
    public void Barrel_sample_ranks_below_the_finished_wine_review()
    {
        var wine = Wine("Ch. La Mission Haut-Brion 2018", "Château La Mission Haut-Brion");
        var barrel = Candidate("Château La Mission Haut-Brion 2018 Barrel Sample", score: 96);
        var finished = Candidate("Château La Mission Haut-Brion 2018", score: 94);

        Assert.Same(finished, WineMatcher.BestMatch(wine, [barrel, finished], 82)!.Candidate);
        Assert.NotNull(WineMatcher.BestMatch(wine, [barrel], 82));
    }
}

public class RatingParserTests
{
    [Fact]
    public void Yahoo_results_use_h3_title_and_unwrap_links()
    {
        const string html = """
            <div class="algo"><div class="compTitle">
              <a href="https://r.search.yahoo.com/_ylt=x/RU=https%3a%2f%2fwww.wineenthusiast.com%2fbuying-guide%2ffranco-conterno-2011%2f/RK=2/RS=y">
                <div><span><span>Wine Enthusiast</span>https://www.wineenthusiast.com › buying-guide › franco</span></div>
                <h3><span>Franco Conterno 2011 Bussia Riserva Nebbiolo (Barolo)</span></h3>
              </a></div>
              <div class="compText">Barolo, Piedmont, Italy Rating 92 Price $60 Winery Franco Conterno Variety Nebbiolo</div>
            </div>
            <div class="algo"><div class="compTitle"><a href="https://example.com/x"><h3>Other site</h3></a></div></div>
            """;

        var result = Assert.Single(WebSearch.ParseYahoo(html, "wineenthusiast.com"));

        Assert.Equal("Franco Conterno 2011 Bussia Riserva Nebbiolo (Barolo)", result.Title);
        Assert.Equal("https://www.wineenthusiast.com/buying-guide/franco-conterno-2011/", result.Url);

        var candidate = RatingParsers.ParseWineEnthusiastSnippet(result)!;
        Assert.Equal(92, candidate.Score);
        Assert.Equal("Franco Conterno", candidate.Producer);
        Assert.Equal("2011", candidate.Vintage);
    }

    [Fact]
    public void Results_page_detection_ignores_footer_consent_links()
    {
        // Real pages carry guce.yahoo.com in a meta tag and the footer; that must not count as a consent wall.
        const string results = """
            <meta name="oath:guce:consent-host" content="guce.yahoo.com"/>
            <div id="results"><ol class="searchCenterMiddle"></ol></div>
            <a href="https://guce.yahoo.com/privacy-policy">Privacy</a>
            """;
        const string consentWall = """<form class="consent-form" action="https://consent.yahoo.com/v2/collectConsent">Before you continue</form>""";

        Assert.True(WebSearch.IsResultsPage(results));
        Assert.False(WebSearch.IsResultsPage(consentWall));
    }

    [Fact]
    public async Task Search_with_no_hits_is_no_match_not_an_error()
    {
        var fetcher = new FakeFetcher().On("search.yahoo.com", """<div id="results"><ol class="searchCenterMiddle"></ol></div>""");
        var search = new WebSearch(fetcher, TimeSpan.Zero);

        var results = await search.SearchAsync("site:vivino.com nothing", "vivino.com", CancellationToken.None);

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public void Bing_results_unwrap_base64_links()
    {
        var target = "https://www.falstaff.com/en/wines/x-2021";
        var encoded = "a1" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(target)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var html = $"""<li class="b_algo"><h2><a href="https://www.bing.com/ck/a?!&&p=abc&u={encoded}&ntb=1">X 2021 - Producer</a></h2><div class="b_caption"><p>with 93 Falstaff points</p></div></li>""";

        var result = Assert.Single(WebSearch.ParseBing(html, "falstaff.com"));

        Assert.Equal(target, result.Url);
    }

    [Fact]
    public void CellarTracker_snippet_keeps_periods_inside_name_and_rejects_producer_pages()
    {
        var wine = RatingParsers.ParseCellarTrackerSnippet(new SearchResult("t", "https://www.cellartracker.com/wine.asp?iWine=1",
            "Sep 9, 2023 · Average of 88.4 points in 17 community wine reviews on 2001 Château Beauséjour (Puisseguin-St. Émilion) Les Hauts de Beauséjour , plus professional notes"))!;

        Assert.Equal("2001 Château Beauséjour (Puisseguin-St. Émilion) Les Hauts de Beauséjour", wine.Name);
        Assert.Equal(88.4, wine.Score);
        Assert.Equal(17, wine.RatingCount);
        Assert.Equal("2001", wine.Vintage);

        Assert.Null(RatingParsers.ParseCellarTrackerSnippet(new SearchResult("t", "u",
            "Average of 89.3 points in 224 community wine reviews on 283 wines from Franco Conterno, plus professional notes")));
    }

    [Fact]
    public void Falstaff_snippet_reads_score_and_producer()
    {
        var candidate = RatingParsers.ParseFalstaffSnippet(new SearchResult(
            "2021 Vigna \"La Casa\" Brunello di Montalcino DOCG - Caparzo",
            "https://www.falstaff.com/en/wines/caparzo-2021-vigna-la-casa-brunello-di-montalcino-docg",
            "The red wine Vigna \"La Casa\" Brunello di Montalcino DOCG, vintage 2021 from the winery Caparzo has been rated in 2026 by Othmar Kiem with 94 Falstaff points."))!;

        Assert.Equal(94, candidate.Score);
        Assert.Equal("Caparzo", candidate.Producer);
        Assert.Equal("2021", candidate.Vintage);
        Assert.Equal("2021 Vigna \"La Casa\" Brunello di Montalcino DOCG", candidate.Name);
    }

    [Fact]
    public void Falstaff_snippet_accepts_plus_scores()
    {
        var candidate = RatingParsers.ParseFalstaffSnippet(new SearchResult("2023 X - Producer", "https://www.falstaff.com/en/wines/x",
            "The red wine X, vintage 2023 from the winery Producer has been rated with 94+ Falstaff points."))!;

        Assert.Equal(94, candidate.Score);
    }

    [Fact]
    public void CellarTracker_title_without_score_still_gives_the_wine_link()
    {
        var candidate = RatingParsers.ParseCellarTrackerTitle(new SearchResult(
            "2023 Backhouse Chardonnay - CellarTracker", "https://www.cellartracker.com/wine.asp?iWine=123", "Wine details and recommendations"))!;

        Assert.Equal("2023 Backhouse Chardonnay", candidate.Name);
        Assert.Null(candidate.Score);
        Assert.Null(RatingParsers.ParseCellarTrackerTitle(new SearchResult(
            "Community Tasting Notes - 2001 Les Hauts de ...", "https://www.cellartracker.com/notes.asp?iWine=1", "")));
    }

    [Fact]
    public void Wine_Enthusiast_title_strips_truncated_suffix()
    {
        var candidate = RatingParsers.ParseWineEnthusiastSnippet(new SearchResult(
            "Altanza 2021 Crianza Tempranillo (Rioja) Rating and Re...", "https://www.wineenthusiast.com/buying-guide/altanza-2021/",
            "Rioja, Spain Rating 90 Price $18"))!;

        Assert.Equal("Altanza 2021 Crianza Tempranillo (Rioja)", candidate.Name);
    }

    [Fact]
    public void JsonLd_reads_100_point_reviews_and_skips_star_ratings()
    {
        const string html = """
            <script type="application/ld+json">
            {"@type":"Product","name":"Castello di Radda 2023 Chianti Classico DOCG","brand":{"name":"Castello di Radda"},
             "review":{"@type":"Review","reviewRating":{"ratingValue":"91","bestRating":"100"}},
             "aggregateRating":{"ratingValue":4.5,"bestRating":5,"ratingCount":12}}
            </script>
            """;

        var candidate = Assert.Single(RatingParsers.ParseJsonLd(html, "https://www.falstaff.com/en/wines/castello-di-radda-2023"));

        Assert.Equal(91, candidate.Score);
        Assert.Equal("Castello di Radda", candidate.Producer);
        Assert.Equal("2023", candidate.Vintage);
    }

    [Fact]
    public void JsonLd_decodes_html_entities_in_names()
    {
        const string html = """
            <script type="application/ld+json">
            {"@type":"Product","name":"Bader-Mimeur 2020 Chassagne-Montrachet &amp; Clos du Château",
             "review":{"@type":"Review","reviewRating":{"ratingValue":"92","bestRating":"100"}}}
            </script>
            """;

        var candidate = Assert.Single(RatingParsers.ParseJsonLd(html, "https://www.wineenthusiast.com/buying-guide/x/"));

        Assert.Equal("Bader-Mimeur 2020 Chassagne-Montrachet & Clos du Château", candidate.Name);
    }

    [Fact]
    public void Vivino_page_gives_vintage_rating_and_all_vintages_fallback()
    {
        const string props = """
            {"vintage":{"name":"Franco Conterno Barolo Bussia Munie Riserva 2011","year":2011,
              "statistics":{"ratings_average":4.4,"ratings_count":259},
              "wine":{"name":"Barolo Bussia Munie Riserva","winery":{"name":"Franco Conterno"},
                      "statistics":{"ratings_average":4.3,"ratings_count":2751}}}}
            """;
        var html = $"<div id=\"ssr-wine-page-top-section\" data-ssr-props='{props}'></div>";

        var candidates = RatingParsers.ParseVivinoPage(html, "https://www.vivino.com/w/1495157?year=2011");

        Assert.Equal(2, candidates.Count);
        Assert.Equal(("2011", 4.4, 259), (candidates[0].Vintage, candidates[0].Score!.Value, candidates[0].RatingCount!.Value));
        Assert.Equal("Franco Conterno", candidates[0].Producer);
        Assert.Null(candidates[1].Vintage);
        Assert.Equal(4.3, candidates[1].Score);
    }

    [Fact]
    public void Vivino_vintage_url_replaces_existing_year()
    {
        Assert.Equal("https://www.vivino.com/en/x/w/1?year=2019", VivinoSource.VintageUrl("https://www.vivino.com/en/x/w/1?year=2011", "2019"));
        Assert.Equal("https://www.vivino.com/en/x/w/1", VivinoSource.VintageUrl("https://www.vivino.com/en/x/w/1?year=2011", null));
    }

    [Theory]
    [InlineData(202, "", true)]
    [InlineData(403, "", true)]
    [InlineData(200, "<title>Just a moment...</title>", true)]
    [InlineData(200, "<html>wine page</html>", false)]
    public void Blocked_pages_are_detected(int status, string body, bool blocked) =>
        Assert.Equal(blocked, RatingParsers.IsBlocked(status, body));
}

public class RatingServiceTests
{
    private sealed class FakeSource(string id, Func<WineIdentity, SourceLookup> lookup) : IRatingSource
    {
        public int Calls { get; private set; }
        public string Id => id;
        public string Label => id;
        public double ToScore100(double score) => score <= 5 ? score * 20 : score;

        public RatingResult? LastHint { get; private set; }

        public Task<SourceLookup> LookupAsync(WineIdentity wine, double threshold, CancellationToken ct, RatingResult? hint = null)
        {
            Calls++;
            LastHint = hint;
            return Task.FromResult(lookup(wine));
        }
    }

    [Fact]
    public async Task Bottle_sizes_of_the_same_wine_share_ratings()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings(
        [
            Wines.Listing("75", "Aldo Conterno Barolo Bussia Cicala 2020"),
            Wines.Listing("150", "Aldo Conterno Barolo Bussia Cicala 2020") with { VolumeCl = 150 },
            Wines.Listing("5l", "Aldo Conterno Barolo Bussia Cicala 5l 2020") with { VolumeCl = 500 },
            Wines.Listing("2019", "Aldo Conterno Barolo Bussia Cicala 2019"),
        ], DateTimeOffset.UtcNow);
        var vivino = new FakeSource("vivino", _ => new SourceLookup([new RatingCandidate { Name = "Aldo Conterno Barolo Bussia Cicala 2020", Vintage = "2020", Score = 4.4 }], "q"));
        var service = new RatingService(db.Repository, [vivino]);

        await service.RateAsync("75", force: false, CancellationToken.None);

        // Rated once; both other sizes have it straight away, the other vintage does not.
        Assert.Equal(1, vivino.Calls);
        Assert.Equal(88, db.Repository.GetWine("150")!.BestScore);
        Assert.Equal(88, db.Repository.GetWine("5l")!.BestScore);
        Assert.Null(db.Repository.GetWine("2019")!.BestScore);
        Assert.StartsWith(RatingService.SharedPrefix, db.Repository.GetRatings("5l").Single().SearchQuery);

        // Rating a sibling later reuses the fresh result instead of searching.
        db.Repository.SaveRating(db.Repository.GetRatings("150").Single() with { FetchedAt = DateTimeOffset.UtcNow.AddDays(-200) });
        await service.RateAsync("150", force: false, CancellationToken.None);
        Assert.Equal(1, vivino.Calls);
    }

    [Fact]
    public void Same_name_from_another_country_or_without_a_known_vintage_is_not_a_sibling()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings(
        [
            // Found in the real catalogue: two different "Farmers Market" wines, Spanish and Italian.
            Wines.Listing("es", "Farmers Market") with { Country = "Spania" },
            Wines.Listing("it", "Farmers Market") with { Country = "Italia" },
            // Same name, no year in either listing: probably different vintages until details say otherwise.
            Wines.Listing("a", "Ch. Cantemerle") with { Country = "Frankrike" },
            Wines.Listing("b", "Ch. Cantemerle") with { Country = "Frankrike", VolumeCl = 150 },
        ], DateTimeOffset.UtcNow);

        Assert.Empty(db.Repository.GetSiblingCodes("es"));
        Assert.Empty(db.Repository.GetSiblingCodes("a"));

        // Product pages that could not be read say nothing about the vintage.
        db.Repository.MarkDetailsUnavailable("a", DateTimeOffset.UtcNow);
        db.Repository.MarkDetailsUnavailable("b", DateTimeOffset.UtcNow);
        Assert.Empty(db.Repository.GetSiblingCodes("a"));

        // Both product pages read and still no vintage: a non-vintage wine in two sizes.
        db.Repository.SaveDetails(new WineDetails { Code = "a" }, DateTimeOffset.UtcNow);
        db.Repository.SaveDetails(new WineDetails { Code = "b" }, DateTimeOffset.UtcNow);
        Assert.Equal(["b"], db.Repository.GetSiblingCodes("a"));
    }

    [Fact]
    public async Task Bottle_sizes_that_turn_out_to_be_different_vintages_share_nothing()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings(
        [
            Wines.Listing("75", "Ressia Gepu Langhe Nebbiolo"),
            Wines.Listing("150", "Ressia Gepu Langhe Nebbiolo") with { VolumeCl = 150 },
            Wines.Listing("300", "Ressia Gepu Langhe Nebbiolo") with { VolumeCl = 300 },
        ], DateTimeOffset.UtcNow);
        // The listing names had no year; the product pages give the magnum another vintage.
        db.Repository.SaveDetails(new WineDetails { Code = "75", Vintage = "2021" }, DateTimeOffset.UtcNow);
        db.Repository.SaveDetails(new WineDetails { Code = "150", Vintage = "2019" }, DateTimeOffset.UtcNow);
        db.Repository.SaveDetails(new WineDetails { Code = "300", Vintage = "2021" }, DateTimeOffset.UtcNow);
        var vivino = new FakeSource("vivino", _ => new SourceLookup(
            [new RatingCandidate { Name = "Ressia Gepu Langhe Nebbiolo 2021", Vintage = "2021", Score = 3.9 }], "q"));

        await new RatingService(db.Repository, [vivino]).RateAsync("75", force: false, CancellationToken.None);

        Assert.Equal(78, db.Repository.GetWine("300")!.BestScore);
        Assert.Empty(db.Repository.GetRatings("150"));
    }

    [Fact]
    public async Task Another_vintage_gets_the_remembered_match_as_a_hint()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings(
        [
            Wines.Listing("2020", "Aldo Conterno Barolo Bussia Cicala 2020"),
            Wines.Listing("2019", "Aldo Conterno Barolo Bussia Cicala 2019"),
        ], DateTimeOffset.UtcNow);
        db.Repository.SaveRating(new RatingResult
        {
            Code = "2020", Source = "vivino", Status = RatingStatus.Matched, Score = 4.4, Score100 = 88,
            MatchedName = "Aldo Conterno Barolo Bussia Cicala 2020", Url = "https://www.vivino.com/w/1234?year=2020",
        });
        var vivino = new FakeSource("vivino", _ => new SourceLookup([], "q"));

        await new RatingService(db.Repository, [vivino]).RateAsync("2019", force: false, CancellationToken.None);

        Assert.Equal("https://www.vivino.com/w/1234?year=2020", vivino.LastHint?.Url);
        Assert.Equal("Aldo Conterno Barolo Bussia Cicala 2020", vivino.LastHint?.MatchedName);
    }

    [Fact]
    public async Task Vivino_opens_the_remembered_page_for_the_new_vintage_without_searching()
    {
        const string props = """{"vintage":{"name":"Aldo Conterno Barolo Bussia Cicala 2019","year":2019,"statistics":{"ratings_average":4.3,"ratings_count":80},"wine":{"name":"Barolo Bussia Cicala","winery":{"name":"Aldo Conterno"}}}}""";
        var fetcher = new FakeFetcher()
            .On(url => url == "https://www.vivino.com/en/aldo-conterno-barolo-bussia-cicala/w/1234?year=2019",
                _ => new FetchResponse(200, $"<div id=\"ssr-wine-page-top-section\" data-ssr-props='{props}'></div>"));
        var source = new VivinoSource(new WebSearch(fetcher, TimeSpan.Zero), new RatingFetchers(fetcher, fetcher));
        var wine = new WineIdentity { Code = "1", Name = "Aldo Conterno Barolo Bussia Cicala 2019", Vintage = "2019", Producer = "Aldo Conterno" };
        var hint = new RatingResult
        {
            Code = "0", Source = "vivino", Status = RatingStatus.Matched,
            Url = "https://www.vivino.com/en/aldo-conterno-barolo-bussia-cicala/w/1234?year=2020", MatchedName = "Aldo Conterno Barolo Bussia Cicala 2020",
        };

        var lookup = await source.LookupAsync(wine, 82, CancellationToken.None, hint);

        Assert.Equal(4.3, WineMatcher.BestMatch(wine, lookup.Candidates, 82)!.Candidate.Score);
        Assert.DoesNotContain(fetcher.Requests, url => url.Contains("search.yahoo.com"));
    }

    [Fact]
    public async Task Rates_with_all_sources_normalises_and_reuses_fresh_results()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1", "Ressia Gepu Langhe Nebbiolo 2021")], DateTimeOffset.UtcNow);
        var vivino = new FakeSource("vivino", _ => new SourceLookup([new RatingCandidate { Name = "Ressia Gepu Langhe Nebbiolo", Score = 3.9, RatingCount = 338 }], "q"));
        var falstaff = new FakeSource("falstaff", _ => new SourceLookup([new RatingCandidate { Name = "Other Producer Langhe Nebbiolo 2021", Score = 90 }], "q"));
        var blocked = new FakeSource("wine_enthusiast", _ => new SourceLookup([], "q", Blocked: true));
        var service = new RatingService(db.Repository, [vivino, falstaff, blocked]);

        var results = await service.RateAsync("1", force: false, CancellationToken.None);

        var v = results.Single(r => r.Source == "vivino");
        Assert.Equal(RatingStatus.Matched, v.Status);
        Assert.Equal(78, v.Score100);
        Assert.Equal(RatingStatus.NoMatch, results.Single(r => r.Source == "falstaff").Status);
        Assert.Equal(RatingStatus.Blocked, results.Single(r => r.Source == "wine_enthusiast").Status);
        Assert.Equal(78, db.Repository.GetWine("1")!.BestScore);

        await service.RateAsync("1", force: false, CancellationToken.None);
        Assert.Equal(1, vivino.Calls);
        Assert.Equal(1, falstaff.Calls);
        // Blocked results are retried after a day, not immediately.
        Assert.Equal(1, blocked.Calls);

        await service.RateAsync("1", force: true, CancellationToken.None);
        Assert.Equal(2, vivino.Calls);
    }

    [Fact]
    public async Task A_failing_source_is_recorded_as_error_without_stopping_others()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1", "Ressia Gepu Langhe Nebbiolo")], DateTimeOffset.UtcNow);
        var broken = new FakeSource("falstaff", _ => throw new HttpRequestException("boom"));
        var vivino = new FakeSource("vivino", _ => new SourceLookup([new RatingCandidate { Name = "Ressia Gepu Langhe Nebbiolo", Score = 3.9 }], "q"));

        var results = await new RatingService(db.Repository, [broken, vivino]).RateAsync("1", false, CancellationToken.None);

        Assert.Equal(RatingStatus.Error, results.Single(r => r.Source == "falstaff").Status);
        Assert.Equal(RatingStatus.Matched, results.Single(r => r.Source == "vivino").Status);
    }

    [Fact]
    public async Task Transient_failure_keeps_an_existing_score()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1", "Ressia Gepu Langhe Nebbiolo")], DateTimeOffset.UtcNow);
        db.Repository.SaveRating(new RatingResult { Code = "1", Source = "vivino", Status = RatingStatus.Matched, Score = 3.9, Score100 = 78 });
        var failing = new FakeSource("vivino", _ => new SourceLookup([], "q", Error: "Web search failed"));

        await new RatingService(db.Repository, [failing]).RateAsync("1", force: true, CancellationToken.None);

        Assert.Equal(RatingStatus.Matched, db.Repository.GetRatings("1").Single().Status);
        Assert.Equal(78, db.Repository.GetWine("1")!.BestScore);
    }

    [Fact]
    public async Task Failed_search_is_an_error_not_a_month_long_no_match()
    {
        var fetcher = new FakeFetcher().On("search.yahoo.com", "", status: 500);
        var source = new CellarTrackerSource(new WebSearch(fetcher, TimeSpan.Zero));

        var lookup = await source.LookupAsync(new WineIdentity { Code = "1", Name = "Ressia Gepu 2021", Vintage = "2021" }, 82, CancellationToken.None);

        Assert.NotNull(lookup.Error);
    }

    [Fact]
    public async Task Missing_only_run_skips_sources_that_already_matched()
    {
        using var db = new TempDatabase();
        db.Repository.UpsertListings([Wines.Listing("1", "Ressia Gepu Langhe Nebbiolo")], DateTimeOffset.UtcNow);
        db.Repository.SaveRating(new RatingResult { Code = "1", Source = "vivino", Status = RatingStatus.Matched, Score = 3.9, Score100 = 78, FetchedAt = DateTimeOffset.UtcNow.AddDays(-200) });
        var vivino = new FakeSource("vivino", _ => new SourceLookup([], "q"));
        var falstaff = new FakeSource("falstaff", _ => new SourceLookup([], "q"));

        await new RatingService(db.Repository, [vivino, falstaff]).RunAsync(["1"], missingOnly: true, null, CancellationToken.None);

        Assert.Equal(0, vivino.Calls);
        Assert.Equal(1, falstaff.Calls);
    }

    [Fact]
    public async Task Vivino_source_fetches_the_vintage_page_found_by_search()
    {
        const string props = """{"vintage":{"name":"Ressia Gepu Langhe Nebbiolo 2021","year":2021,"statistics":{"ratings_average":3.9,"ratings_count":40},"wine":{"name":"Gepu Langhe Nebbiolo","winery":{"name":"Ressia"}}}}""";
        var yahoo = """
            <div id="results"><div class="algo"><div class="compTitle"><a href="https://r.search.yahoo.com/x/RU=https%3a%2f%2fwww.vivino.com%2fen%2fressia-gepu-langhe-nebbiolo%2fw%2f1762553/RK=0"><h3>Ressia Gepu Langhe Nebbiolo | Vivino</h3></a></div></div></div>
            """;
        var fetcher = new FakeFetcher()
            .On("search.yahoo.com", yahoo)
            .On(url => url == "https://www.vivino.com/en/ressia-gepu-langhe-nebbiolo/w/1762553?year=2021",
                _ => new FetchResponse(200, $"<div id=\"ssr-wine-page-top-section\" data-ssr-props='{props}'></div>"));
        var source = new VivinoSource(new WebSearch(fetcher, TimeSpan.Zero), new RatingFetchers(fetcher, fetcher));
        var wine = new WineIdentity { Code = "1", Name = "Ressia Gepù Langhe Nebbiolo 2021", Vintage = "2021" };

        var lookup = await source.LookupAsync(wine, 82, CancellationToken.None);

        var match = WineMatcher.BestMatch(wine, lookup.Candidates, 82)!;
        Assert.Equal(3.9, match.Candidate.Score);
        Assert.Equal("2021", match.Candidate.Vintage);
        Assert.StartsWith("site:vivino.com Ressia Gepu", lookup.Query);
    }
}
