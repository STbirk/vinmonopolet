from __future__ import annotations

from vinliste.matching import has_identifier_overlap, normalize_text, select_best_match


def test_normalize_text_removes_accents_years_and_volume() -> None:
    assert normalize_text("Rødvin Château 2020 75 cl!") == "rodvin chateau"


def test_select_best_match_prefers_exact_vintage() -> None:
    wine = {"productLongName": "Example Estate Pinot Noir", "vintage": "2020"}
    candidates = [
        {"name": "Example Estate Pinot Noir 2019", "vintage": "2019"},
        {"name": "Example Estate Pinot Noir 2020", "vintage": "2020"},
    ]

    match, confidence = select_best_match(wine, candidates, threshold=82)

    assert match["vintage"] == "2020"
    assert confidence >= 82


def test_select_best_match_returns_none_below_threshold() -> None:
    wine = {"productLongName": "Example Estate Pinot Noir", "vintage": "2020"}
    candidates = [{"name": "Completely Different Wine", "vintage": "2020"}]

    match, confidence = select_best_match(wine, candidates, threshold=95)

    assert match is None
    assert confidence < 95


def test_identifier_overlap_rejects_same_region_wrong_producer() -> None:
    assert not has_identifier_overlap(
        "Castello di Radda Chianti Classico 2023",
        "Castello di Ama Chianti Classico 2023",
    )


def test_identifier_overlap_accepts_abbreviated_chateau_match() -> None:
    assert has_identifier_overlap(
        "Ch. Fourcas Dupré 2021",
        "Chateau Fourcas Dupré 2021",
    )
