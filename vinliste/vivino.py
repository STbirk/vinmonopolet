from __future__ import annotations

import json
import re
import time
from pathlib import Path
from typing import Any
from urllib.parse import quote_plus, urljoin

import requests
from bs4 import BeautifulSoup

from .matching import extract_vintage, select_best_match


VIVINO_SEARCH_URL = "https://www.vivino.com/search/wines?q={query}"


class VivinoRatingClient:
    def __init__(
        self,
        *,
        cache_path: Path,
        delay_seconds: float,
        match_threshold: float,
        timeout_seconds: int = 30,
        verbose: bool = False,
    ) -> None:
        self.cache_path = cache_path
        self.delay_seconds = delay_seconds
        self.match_threshold = match_threshold
        self.timeout_seconds = timeout_seconds
        self.verbose = verbose
        self._cache = self._load_cache()
        self._last_request_at = 0.0

    def rate_wine(self, wine: dict[str, Any]) -> dict[str, Any]:
        query = build_query(wine)
        cached = self._cache.get(query)
        if cached is None:
            cached = self._search(query)
            self._cache[query] = cached

        if cached.get("status") == "blocked":
            return empty_rating("vivino", "blocked", query, error=cached.get("error"))
        if cached.get("status") == "error":
            return empty_rating("vivino", "error", query, error=cached.get("error"))

        candidates = cached.get("candidates") or []
        match, confidence = select_best_match(
            wine,
            candidates,
            threshold=self.match_threshold,
        )
        if match is None:
            return {
                **empty_rating("vivino", "no_match", query),
                "match_confidence": round(confidence, 2) if confidence else None,
            }

        return {
            "source": "vivino",
            "status": "matched",
            "search_query": query,
            "matched_name": match.get("name"),
            "matched_vintage": match.get("vintage"),
            "score": match.get("score"),
            "rating_count": match.get("rating_count"),
            "url": match.get("url"),
            "match_confidence": round(confidence, 2),
        }

    def save_cache(self) -> None:
        self.cache_path.parent.mkdir(parents=True, exist_ok=True)
        self.cache_path.write_text(
            json.dumps(self._cache, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )

    def _load_cache(self) -> dict[str, Any]:
        if not self.cache_path.exists():
            return {}
        try:
            return json.loads(self.cache_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return {}

    def _search(self, query: str) -> dict[str, Any]:
        wait_seconds = self.delay_seconds - (time.monotonic() - self._last_request_at)
        if wait_seconds > 0:
            time.sleep(wait_seconds)

        url = VIVINO_SEARCH_URL.format(query=quote_plus(query))
        if self.verbose:
            print(f"Searching Vivino: {query}")

        try:
            response = requests.get(
                url,
                headers={
                    "Accept": "text/html,application/xhtml+xml",
                    "Accept-Language": "en-US,en;q=0.9",
                    "User-Agent": (
                        "Mozilla/5.0 (compatible; vinliste/0.1; "
                        "+https://www.vinmonopolet.no/)"
                    ),
                },
                timeout=self.timeout_seconds,
            )
            self._last_request_at = time.monotonic()
        except requests.RequestException as exc:
            return {"status": "error", "error": str(exc), "candidates": []}

        if is_blocked_response(response.status_code, response.text):
            return {
                "status": "blocked",
                "error": f"Vivino returned status {response.status_code} or block page",
                "candidates": [],
            }

        try:
            candidates = parse_candidates(response.text, base_url="https://www.vivino.com")
        except Exception as exc:  # pragma: no cover - defensive against page churn.
            return {"status": "error", "error": str(exc), "candidates": []}

        return {"status": "ok", "url": url, "candidates": candidates}


def build_query(wine: dict[str, Any]) -> str:
    name = wine.get("productLongName") or wine.get("productShortName")
    vintage = wine.get("vintage")
    if name and vintage and str(vintage) in str(name):
        vintage = None
    parts = [
        name,
        vintage,
        wine.get("country"),
        wine.get("region"),
    ]
    return " ".join(str(part).strip() for part in parts if part)


def empty_rating(
    source: str,
    status: str,
    query: str | None,
    *,
    error: str | None = None,
) -> dict[str, Any]:
    rating = {
        "source": source,
        "status": status,
        "search_query": query,
        "matched_name": None,
        "matched_vintage": None,
        "score": None,
        "rating_count": None,
        "url": None,
        "match_confidence": None,
    }
    if error:
        rating["error"] = error
    return rating


def is_blocked_response(status_code: int, text: str) -> bool:
    if status_code in {401, 403, 429}:
        return True
    lowered = text.lower()
    block_markers = [
        "captcha",
        "verify you are human",
        "access denied",
        "cloudflare",
        "login required",
    ]
    return any(marker in lowered for marker in block_markers)


def parse_candidates(html: str, *, base_url: str) -> list[dict[str, Any]]:
    soup = BeautifulSoup(html, "html.parser")
    candidates: list[dict[str, Any]] = []

    for script in soup.find_all("script", attrs={"type": "application/ld+json"}):
        if not script.string:
            continue
        for item in _json_items(script.string):
            candidates.extend(_candidate_from_structured_item(item, base_url=base_url))

    next_data = soup.find("script", id="__NEXT_DATA__")
    if next_data and next_data.string:
        for item in _json_items(next_data.string):
            candidates.extend(_candidate_from_blob(item, base_url=base_url))

    candidates.extend(_candidate_from_html_links(soup, base_url=base_url))
    return dedupe_candidates(candidates)


def dedupe_candidates(candidates: list[dict[str, Any]]) -> list[dict[str, Any]]:
    deduped: list[dict[str, Any]] = []
    seen: set[tuple[str | None, str | None]] = set()
    for candidate in candidates:
        name = candidate.get("name")
        if not name:
            continue
        key = (name, candidate.get("url"))
        if key in seen:
            continue
        seen.add(key)
        deduped.append(candidate)
    return deduped


def _json_items(raw: str) -> list[Any]:
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        return []
    return parsed if isinstance(parsed, list) else [parsed]


def _candidate_from_structured_item(item: Any, *, base_url: str) -> list[dict[str, Any]]:
    if not isinstance(item, dict):
        return []
    graph = item.get("@graph")
    if isinstance(graph, list):
        results: list[dict[str, Any]] = []
        for entry in graph:
            results.extend(_candidate_from_structured_item(entry, base_url=base_url))
        return results

    item_type = item.get("@type")
    if isinstance(item_type, list):
        type_values = {str(value).lower() for value in item_type}
    else:
        type_values = {str(item_type).lower()}
    if not ({"wine", "product"} & type_values):
        return []

    name = item.get("name")
    aggregate = item.get("aggregateRating") or {}
    return [
        {
            "name": name,
            "vintage": item.get("vintage") or extract_vintage(name),
            "score": _as_float(aggregate.get("ratingValue")),
            "rating_count": _as_int(
                aggregate.get("ratingCount") or aggregate.get("reviewCount")
            ),
            "url": urljoin(base_url, item.get("url") or ""),
        }
    ]


def _candidate_from_blob(item: Any, *, base_url: str) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []

    def walk(value: Any) -> None:
        if isinstance(value, dict):
            candidate = _candidate_from_possible_wine_dict(value, base_url=base_url)
            if candidate:
                results.append(candidate)
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)

    walk(item)
    return results


def _candidate_from_possible_wine_dict(
    item: dict[str, Any],
    *,
    base_url: str,
) -> dict[str, Any] | None:
    name = item.get("name") or item.get("wine_name") or item.get("seo_name")
    if not isinstance(name, str):
        return None

    score = (
        item.get("ratingValue")
        or item.get("ratings_average")
        or item.get("rating_average")
        or item.get("average_rating")
    )
    statistics = item.get("statistics") if isinstance(item.get("statistics"), dict) else {}
    rating_count = (
        item.get("ratingCount")
        or item.get("ratings_count")
        or item.get("review_count")
        or statistics.get("ratings_count")
    )
    url = item.get("url") or item.get("link") or item.get("seo_url")
    if score is None and rating_count is None and url is None:
        return None

    return {
        "name": name,
        "vintage": item.get("year") or item.get("vintage") or extract_vintage(name),
        "score": _as_float(score),
        "rating_count": _as_int(rating_count),
        "url": urljoin(base_url, str(url)) if url else None,
    }


def _candidate_from_html_links(soup: BeautifulSoup, *, base_url: str) -> list[dict[str, Any]]:
    candidates: list[dict[str, Any]] = []
    for link in soup.find_all("a", href=True):
        href = link["href"]
        if "/wines/" not in href and "/wine/" not in href:
            continue
        text = " ".join(link.get_text(" ", strip=True).split())
        if not text:
            continue
        rating_match = re.search(r"\b([1-5](?:\.\d)?)\b", text)
        count_match = re.search(r"([\d,.]+)\s+ratings?", text, flags=re.IGNORECASE)
        candidates.append(
            {
                "name": text,
                "vintage": extract_vintage(text),
                "score": _as_float(rating_match.group(1) if rating_match else None),
                "rating_count": _as_int(count_match.group(1) if count_match else None),
                "url": urljoin(base_url, href),
            }
        )
    return candidates


def _as_float(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(str(value).replace(",", "."))
    except ValueError:
        return None


def _as_int(value: Any) -> int | None:
    if value is None:
        return None
    digits = re.sub(r"[^\d]", "", str(value))
    if not digits:
        return None
    return int(digits)
