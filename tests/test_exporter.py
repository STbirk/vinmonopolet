from __future__ import annotations

from datetime import datetime, timezone

from vinliste.exporter import build_export


class FakeProductClient:
    def iter_products(self):
        return [
            {
                "basic": {"productId": "1", "productShortName": "Red"},
                "classification": {"mainProductTypeName": "Rødvin"},
                "prices": [{"priceValidFrom": "2025-01-01", "salesPrice": 100}],
            },
            {
                "basic": {"productId": "2", "productShortName": "White"},
                "classification": {"mainProductTypeName": "Hvitvin"},
            },
        ]


class FakeRatingClient:
    def rate_wine(self, wine):
        return {
            "source": "vivino",
            "status": "matched",
            "search_query": "Red",
            "matched_name": "Red",
            "matched_vintage": None,
            "score": 4.0,
            "rating_count": 10,
            "url": "https://example.test",
            "match_confidence": 99,
        }


def test_build_export_filters_red_wines_and_adds_rating() -> None:
    export = build_export(
        client=FakeProductClient(),
        rating_client=FakeRatingClient(),
        rating_source="vivino",
        match_threshold=82,
        limit=None,
        generated_at=datetime(2026, 6, 15, tzinfo=timezone.utc),
    )

    assert export["metadata"]["count"] == 1
    assert export["wines"][0]["productId"] == "1"
    assert export["wines"][0]["rating"]["status"] == "matched"
