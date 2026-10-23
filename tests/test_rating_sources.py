from __future__ import annotations

from pathlib import Path

from vinliste.rating_sources import (
    _ascii_query,
    build_query_variants,
    create_rating_clients,
    default_ratings,
    parse_cellartracker_snippet,
    parse_duckduckgo_results,
    parse_falstaff_snippet,
    parse_jsonld_review_candidates,
    parse_rating_text_candidates,
    parse_vivino_detail_candidates,
    parse_wine_enthusiast_snippet,
    parse_wine_review_online_candidates,
    parse_wine_review_online_snippet,
    parse_yahoo_results,
    _source_search_queries,
)


def test_create_rating_clients_adds_useful_sources(tmp_path: Path) -> None:
    clients = create_rating_clients(
        cache_dir=tmp_path,
        delay_seconds=0,
        match_threshold=82,
    )

    assert [client.source for client in clients] == [
        "vivino",
        "cellartracker",
        "falstaff",
        "wine_enthusiast",
    ]


def test_create_rating_clients_uses_bounded_query_budgets(tmp_path: Path) -> None:
    clients = create_rating_clients(
        cache_dir=tmp_path,
        delay_seconds=0,
        match_threshold=82,
        timeout_seconds=7,
    )

    by_source = {client.source: client for client in clients}

    assert by_source["vivino"].timeout_seconds == 7
    assert by_source["vivino"].max_query_variants == 2
    assert by_source["cellartracker"].max_query_variants == 2
    assert by_source["falstaff"].max_query_variants == 1
    assert by_source["wine_enthusiast"].max_query_variants == 1


def test_default_ratings_contains_all_sources() -> None:
    ratings = default_ratings()

    assert set(ratings) == {
        "vivino",
        "cellartracker",
        "falstaff",
        "wine_enthusiast",
    }
    assert all(rating["status"] == "not_requested" for rating in ratings.values())


def test_parse_rating_text_candidates_from_title_and_score() -> None:
    html = """
    <html>
      <head><title>Example Wine 2020</title></head>
      <body>Average rating: 92 points from 1,234 reviews</body>
    </html>
    """

    candidates = parse_rating_text_candidates(html, url="https://example.test/wine")

    assert candidates == [
        {
            "name": "Example Wine 2020",
            "vintage": None,
            "score": 92.0,
            "rating_count": 1234,
            "url": "https://example.test/wine",
        }
    ]


def test_parse_jsonld_review_candidates_from_review_rating() -> None:
    html = """
    <html>
      <head>
        <title>Castello di Radda 2023 Chianti Classico DOCG - Falstaff</title>
        <script type="application/ld+json">
        {
          "@type": "Review",
          "itemReviewed": {"@type": "Product", "name": "Castello di Radda 2023 Chianti Classico DOCG"},
          "reviewRating": {"@type": "Rating", "ratingValue": "91", "bestRating": "100"}
        }
        </script>
      </head>
    </html>
    """

    candidates = parse_jsonld_review_candidates(
        html,
        url="https://www.falstaff.com/en/wines/castello-di-radda-2023",
    )

    assert candidates == [
        {
            "name": "Castello di Radda 2023 Chianti Classico DOCG",
            "vintage": "2023",
            "score": 91.0,
            "rating_count": None,
            "url": "https://www.falstaff.com/en/wines/castello-di-radda-2023",
        }
    ]


def test_parse_jsonld_review_candidates_from_nested_wine_enthusiast_review() -> None:
    html = """
    <script type="application/ld+json">
    {
      "@type": "Product",
      "name": "Chateau Fourcas Dupre 2021 Listrac-Medoc",
      "review": {
        "@type": "Review",
        "reviewRating": {"ratingValue": 92, "bestRating": 100}
      }
    }
    </script>
    """

    assert parse_jsonld_review_candidates(
        html,
        url="https://www.wineenthusiast.com/buying-guide/chateau-fourcas-dupre-2021/",
    ) == [
        {
            "name": "Chateau Fourcas Dupre 2021 Listrac-Medoc",
            "vintage": "2021",
            "score": 92.0,
            "rating_count": None,
            "url": "https://www.wineenthusiast.com/buying-guide/chateau-fourcas-dupre-2021/",
        }
    ]


def test_parse_wine_review_online_candidates() -> None:
    html = """
    <html>
      <head><title>Castello di Radda, Chianti Classico, Tuscany, Italy, 2023 - Wine Review Online</title></head>
      <body><h2>Score</h2><p>93</p></body>
    </html>
    """

    assert parse_wine_review_online_candidates(
        html,
        url="https://winereviewonline.com/wine-review/castello-di-radda-chianti-classico-tuscany-italy-2023-20/",
    ) == [
        {
            "name": "Castello di Radda, Chianti Classico, Tuscany, Italy, 2023",
            "vintage": "2023",
            "score": 93.0,
            "rating_count": None,
            "url": "https://winereviewonline.com/wine-review/castello-di-radda-chianti-classico-tuscany-italy-2023-20/",
        }
    ]


def test_parse_falstaff_snippet_from_search_result() -> None:
    result = {
        "title": '2021 Vigna "La Casa" Brunello di Montalcino DOCG - Caparzo',
        "url": "https://www.falstaff.com/en/wines/caparzo-2021-vigna-la-casa-brunello-di-montalcino-docg",
        "snippet": (
            'The red wine Vigna "La Casa" Brunello di Montalcino DOCG, vintage 2021 '
            "from the winery Caparzo has been rated in 2026 by Othmar Kiem, "
            "Simon Staffler with 94 Falstaff points."
        ),
    }

    assert parse_falstaff_snippet(result) == [
        {
            "name": 'Caparzo 2021 Vigna "La Casa" Brunello di Montalcino DOCG',
            "vintage": "2021",
            "score": 94.0,
            "rating_count": None,
            "url": "https://www.falstaff.com/en/wines/caparzo-2021-vigna-la-casa-brunello-di-montalcino-docg",
        }
    ]


def test_parse_wine_enthusiast_snippet_from_search_result() -> None:
    result = {
        "title": "Château Potensac 2023 Bordeaux-Style Red Blend (Médoc)",
        "url": "https://www.wineenthusiast.com/buying-guide/chateau-potensac-2023-bordeaux-style-red-blend-medoc/",
        "snippet": "Médoc, Bordeaux, France Rating 94 Price $35 Winery Château Potensac",
    }

    assert parse_wine_enthusiast_snippet(result) == [
        {
            "name": "Château Potensac 2023 Bordeaux-Style Red Blend (Médoc)",
            "vintage": "2023",
            "score": 94.0,
            "rating_count": None,
            "url": "https://www.wineenthusiast.com/buying-guide/chateau-potensac-2023-bordeaux-style-red-blend-medoc/",
        }
    ]


def test_parse_wine_review_online_snippet_from_search_result() -> None:
    result = {
        "title": "Example Wine 2022 - Wine Review Online",
        "url": "https://winereviewonline.com/wine-review/example-wine-2022/",
        "snippet": "Score 91. A fresh and lively red wine.",
    }

    assert parse_wine_review_online_snippet(result) == [
        {
            "name": "Example Wine 2022",
            "vintage": "2022",
            "score": 91.0,
            "rating_count": None,
            "url": "https://winereviewonline.com/wine-review/example-wine-2022/",
        }
    ]


def test_source_search_queries_include_site_specific_forms() -> None:
    queries = _source_search_queries("wine_enthusiast", "Ch Potensac 2018")

    assert "site:wineenthusiast.com/buying-guide Ch Potensac 2018" in queries
    assert "Ch Potensac 2018 Wine Enthusiast Rating and Review" in queries


def test_build_query_variants_expands_chateau_abbreviation() -> None:
    wine = {
        "productLongName": "Ch. Fourcas Dupré 2021",
        "vintage": "2021",
        "country": "Frankrike",
        "region": "Bordeaux",
    }

    variants = build_query_variants(wine)

    assert "Chateau Fourcas Dupré 2021" in variants
    assert "Chateau Fourcas Dupré 2021 France Bordeaux" in variants


def test_ascii_query_removes_accents_for_search_fallback() -> None:
    assert _ascii_query("Château Fourcas Dupré") == "Chateau Fourcas Dupre"


def test_parse_cellartracker_snippet() -> None:
    result = {
        "title": "2021 Château Fourcas Dupré, France, Bordeaux - CellarTracker",
        "url": "https://www.cellartracker.com/wine.asp?iWine=4325816",
        "snippet": (
            "Average of 89.3 points in 11 community wine reviews on "
            "2021 Château Fourcas Dupré, plus professional notes."
        ),
    }

    assert parse_cellartracker_snippet(result) == [
        {
            "name": "2021 Château Fourcas Dupré",
            "vintage": "2021",
            "score": 89.3,
            "rating_count": 11,
            "url": "https://www.cellartracker.com/wine.asp?iWine=4325816",
        }
    ]


def test_parse_cellartracker_snippet_rejects_producer_page_summary() -> None:
    result = {
        "title": "Château Certan de May - CellarTracker",
        "url": "https://www.cellartracker.com/producer.asp?iProducer=915",
        "snippet": (
            "Average of 91.4 points in 66 community wine reviews on "
            "66 wines from Château Certan de May."
        ),
    }

    assert parse_cellartracker_snippet(result) == []


def test_parse_cellartracker_snippet_keeps_periods_inside_name() -> None:
    result = {
        "title": "2001 Château Beauséjour (Puisseguin-St. Émilion) Les Hauts de ...",
        "url": "https://www.cellartracker.com/wine.asp?iWine=4090332",
        "snippet": (
            "Sep 9, 2023 · Average of 88.4 points in 17 community wine reviews on "
            "2001 Château Beauséjour (Puisseguin-St. Émilion) Les Hauts de Beauséjour , "
            "plus professional notes, label images, wine details, and"
        ),
    }

    assert parse_cellartracker_snippet(result) == [
        {
            "name": "2001 Château Beauséjour (Puisseguin-St. Émilion) Les Hauts de Beauséjour",
            "vintage": "2001",
            "score": 88.4,
            "rating_count": 17,
            "url": "https://www.cellartracker.com/wine.asp?iWine=4090332",
        }
    ]


def test_parse_duckduckgo_results_unwraps_source_links() -> None:
    html = """
    <div class="result">
      <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.vivino.com%2Fw%2F123">Wine</a>
      <a class="result__snippet">A snippet</a>
    </div>
    """

    assert parse_duckduckgo_results(html, domain="vivino.com") == [
        {"title": "Wine", "url": "https://www.vivino.com/w/123", "snippet": "A snippet"}
    ]


def test_parse_yahoo_results_unwraps_source_links() -> None:
    html = """
    <div class="algo">
      <div class="compTitle">
        <a href="https://r.search.yahoo.com/x/RU=https%3a%2f%2fwww.cellartracker.com%2fwine.asp%3fiWine%3d4325816/RK=2">
          <h3>2021 Château Fourcas Dupré - CellarTracker</h3>
        </a>
      </div>
      <div class="compText">Average of 89.3 points in 11 community wine reviews on 2021 Château Fourcas Dupré.</div>
    </div>
    """

    assert parse_yahoo_results(html, domain="cellartracker.com") == [
        {
            "title": "2021 Château Fourcas Dupré - CellarTracker",
            "url": "https://www.cellartracker.com/wine.asp?iWine=4325816",
            "snippet": "Average of 89.3 points in 11 community wine reviews on 2021 Château Fourcas Dupré.",
        }
    ]


def test_parse_yahoo_results_ignores_site_breadcrumb_in_title() -> None:
    html = """
    <div class="algo">
      <div class="compTitle">
        <a href="https://r.search.yahoo.com/x/RU=https%3a%2f%2fwww.wineenthusiast.com%2fbuying-guide%2ffranco-conterno-2011-bussia-riserva-barolo%2f/RK=0">
          <div><span><span>Wine Enthusiast</span>https://www.wineenthusiast.com › buying-guide › franco</span></div>
          <h3><span>Franco Conterno 2011 Bussia Riserva Nebbiolo (Barolo)</span></h3>
        </a>
      </div>
      <div class="compText">Barolo, Piedmont, Italy Rating 92 Price $60</div>
    </div>
    """

    results = parse_yahoo_results(html, domain="wineenthusiast.com")

    assert results[0]["title"] == "Franco Conterno 2011 Bussia Riserva Nebbiolo (Barolo)"
    assert parse_wine_enthusiast_snippet(results[0])[0]["name"] == (
        "Franco Conterno 2011 Bussia Riserva Nebbiolo (Barolo)"
    )


def test_parse_vivino_detail_candidates() -> None:
    html = """
    <div id="ssr-wine-page-top-section" data-ssr-props='{
      "vintage": {
        "name": "Example Wine 2021",
        "statistics": {"ratings_average": 4.2, "ratings_count": 99}
      }
    }'></div>
    """

    assert parse_vivino_detail_candidates(html, url="https://vivino.test/wine") == [
        {
            "name": "Example Wine 2021",
            "vintage": "2021",
            "score": 4.2,
            "rating_count": 99,
            "url": "https://vivino.test/wine",
        }
    ]
