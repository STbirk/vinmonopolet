from __future__ import annotations

from vinliste.vivino import build_query, is_blocked_response, parse_candidates


def test_parse_candidates_from_json_ld() -> None:
    html = """
    <html><head>
      <script type="application/ld+json">
      {
        "@type": "Wine",
        "name": "Example Estate Pinot Noir 2020",
        "url": "/wines/example-pinot",
        "aggregateRating": {"ratingValue": "4.1", "ratingCount": "1234"}
      }
      </script>
    </head></html>
    """

    candidates = parse_candidates(html, base_url="https://www.vivino.com")

    assert candidates == [
        {
            "name": "Example Estate Pinot Noir 2020",
            "vintage": "2020",
            "score": 4.1,
            "rating_count": 1234,
            "url": "https://www.vivino.com/wines/example-pinot",
        }
    ]


def test_parse_candidates_from_next_data_blob() -> None:
    html = """
    <script id="__NEXT_DATA__" type="application/json">
    {
      "props": {
        "pageProps": {
          "results": [
            {
              "name": "Blob Wine 2018",
              "ratings_average": 3.9,
              "statistics": {"ratings_count": 44},
              "url": "/wines/blob-wine"
            }
          ]
        }
      }
    }
    </script>
    """

    candidates = parse_candidates(html, base_url="https://www.vivino.com")

    assert candidates[0]["name"] == "Blob Wine 2018"
    assert candidates[0]["score"] == 3.9
    assert candidates[0]["rating_count"] == 44


def test_blocked_response_detection() -> None:
    assert is_blocked_response(403, "")
    assert is_blocked_response(200, "Please verify you are human")


def test_build_query_does_not_duplicate_vintage_from_name() -> None:
    wine = {
        "productLongName": "Ressia Barbera d'Alba Superiore 2021",
        "vintage": "2021",
        "country": "Italia",
        "region": "Piemonte",
    }

    assert build_query(wine) == "Ressia Barbera d'Alba Superiore 2021 Italia Piemonte"
