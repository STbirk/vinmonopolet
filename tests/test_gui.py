from __future__ import annotations

from vinliste.gui import _empty_rating, _needs_rating_update, _row_values, _sort_value


def test_row_values_extracts_table_columns() -> None:
    wine = {
        "productId": "9929801",
        "productLongName": "Ressia Barbera d'Alba Superiore 2021",
        "vintage": "2021",
        "country": "Italia",
        "region": "Piemonte",
        "price": {"salesPrice": 310.0},
        "ratings": {
            "vivino": {"score": 4.1, "rating_count": 123, "status": "matched"},
            "cellartracker": {"score": None, "rating_count": None, "status": "no_match"},
            "falstaff": {"score": 91, "rating_count": None, "status": "matched"},
            "wine_enthusiast": {"score": 92, "rating_count": None, "status": "matched"},
        },
    }

    assert _row_values(wine) == (
        "9929801",
        "Ressia Barbera d'Alba Superiore 2021",
        "2021",
        "Italia",
        "Piemonte",
        310.0,
        "",
        "82.0 (123)",
        "no_match",
        "91.0",
        "92.0",
        88.3,
        "matched: 3, no_match: 1",
        "Open",
    )


def test_empty_rating_is_not_requested() -> None:
    rating = _empty_rating()

    assert rating["source"] == "vivino"
    assert rating["status"] == "not_requested"
    assert rating["score"] is None


def test_sort_value_uses_numeric_rating_score() -> None:
    wine = {"ratings": {"vivino": {"score": 4.2}}}

    assert _sort_value(wine, "vivino") == (0, 84.0)


def test_missing_rating_update_skips_matched_scores() -> None:
    wine = {"ratings": {"vivino": {"score": 4.2, "status": "matched"}}}

    assert not _needs_rating_update(wine, "vivino")
    assert _needs_rating_update(wine, "cellartracker")
