from __future__ import annotations

import re
import unicodedata
from difflib import SequenceMatcher
from typing import Any

try:
    from rapidfuzz import fuzz
except ImportError:  # pragma: no cover - dependency should be installed.
    fuzz = None


YEAR_RE = re.compile(r"\b(19\d{2}|20\d{2})\b")
VOLUME_RE = re.compile(r"\b\d+(?:[,.]\d+)?\s*(?:cl|ml|l|liter|litre)\b")
IDENTIFIER_STOPWORDS = {
    "a",
    "al",
    "and",
    "ch",
    "chateau",
    "chianti",
    "classico",
    "cotes",
    "cuvee",
    "d",
    "de",
    "del",
    "della",
    "des",
    "di",
    "do",
    "dom",
    "domaine",
    "du",
    "la",
    "le",
    "les",
    "red",
    "reserve",
    "reserva",
    "riserva",
    "rouge",
    "superieur",
    "superiore",
    "ten",
    "tenuta",
    "the",
    "villa",
    "vin",
    "wine",
}
VARIETY_STOPWORDS = {
    "alba",
    "asti",
    "barbera",
    "bordeaux",
    "cabernet",
    "franc",
    "merlot",
    "nebbiolo",
    "noir",
    "pinot",
    "sangiovese",
    "sauvignon",
    "syrah",
    "tinto",
}


def normalize_text(value: str | None) -> str:
    if not value:
        return ""
    value = value.translate(str.maketrans({"ø": "o", "Ø": "O", "æ": "ae", "Æ": "AE"}))
    value = unicodedata.normalize("NFKD", value)
    value = "".join(ch for ch in value if not unicodedata.combining(ch))
    value = value.lower()
    value = VOLUME_RE.sub(" ", value)
    value = YEAR_RE.sub(" ", value)
    value = re.sub(r"[^a-z0-9]+", " ", value)
    return re.sub(r"\s+", " ", value).strip()


def extract_vintage(value: str | None) -> str | None:
    if not value:
        return None
    match = YEAR_RE.search(str(value))
    return match.group(1) if match else None


def score_names(left: str, right: str) -> float:
    left_norm = normalize_text(left)
    right_norm = normalize_text(right)
    if not left_norm or not right_norm:
        return 0.0
    if fuzz is not None:
        return float(fuzz.WRatio(left_norm, right_norm))
    return SequenceMatcher(None, left_norm, right_norm).ratio() * 100


def select_best_match(
    wine: dict[str, Any],
    candidates: list[dict[str, Any]],
    *,
    threshold: float,
) -> tuple[dict[str, Any] | None, float]:
    best: dict[str, Any] | None = None
    best_score = 0.0
    wine_name = wine.get("productLongName") or wine.get("productShortName") or ""
    wine_vintage = str(wine.get("vintage") or "").strip() or None

    for candidate in candidates:
        candidate_vintage = candidate.get("vintage")
        if wine_vintage and candidate_vintage and str(candidate_vintage) != wine_vintage:
            continue
        if not has_identifier_overlap(wine_name, candidate.get("name") or ""):
            continue

        score = score_names(wine_name, candidate.get("name") or "")
        if wine_vintage and candidate_vintage == wine_vintage:
            score = min(100.0, score + 5.0)
        if score > best_score:
            best = candidate
            best_score = score

    if best is None or best_score < threshold:
        return None, best_score
    return best, best_score


def has_identifier_overlap(left: str, right: str) -> bool:
    required = distinctive_tokens(left)
    if not required:
        return True

    candidate_tokens = set(normalize_text(right).split())
    if required[0] not in candidate_tokens:
        return False
    if len(required) == 1:
        return True
    overlap = sum(1 for token in required[:3] if token in candidate_tokens)
    return overlap >= 2


def distinctive_tokens(value: str) -> list[str]:
    tokens = normalize_text(value).split()
    return [
        token
        for token in tokens
        if len(token) > 2
        and token not in IDENTIFIER_STOPWORDS
        and token not in VARIETY_STOPWORDS
        and not token.isdigit()
    ]
