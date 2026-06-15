from __future__ import annotations

import json
import re
import time
import unicodedata
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, quote_plus, unquote, urlparse

from bs4 import BeautifulSoup
from curl_cffi import requests as curl_requests
from curl_cffi.requests.errors import RequestsError

from .matching import select_best_match
from .vivino import build_query, empty_rating, is_blocked_response, parse_candidates


CACHE_VERSION = 6

SOURCE_LABELS = {
    "vivino": "Vivino",
    "cellartracker": "CellarTracker",
    "falstaff": "Falstaff",
    "wine_enthusiast": "Wine Enthusiast",
}

_DISCOVERY_FIRST_SOURCES = {
    "cellartracker",
    "falstaff",
    "wine_enthusiast",
}
_SEARCH_ONLY_SOURCES = {
    "cellartracker",
    "falstaff",
    "wine_enthusiast",
}


class WebRatingClient:
    def __init__(
        self,
        *,
        source: str,
        search_url_template: str,
        cache_path: Path,
        delay_seconds: float,
        match_threshold: float,
        timeout_seconds: int = 30,
        max_query_variants: int | None = None,
        verbose: bool = False,
    ) -> None:
        self.source = source
        self.search_url_template = search_url_template
        self.cache_path = cache_path
        self.delay_seconds = delay_seconds
        self.match_threshold = match_threshold
        self.timeout_seconds = timeout_seconds
        self.max_query_variants = max_query_variants
        self.verbose = verbose
        self._cache = self._load_cache()
        self._last_request_at = 0.0
        self._session = curl_requests.Session(impersonate="chrome")

    def rate_wine(self, wine: dict[str, Any]) -> dict[str, Any]:
        blocked_or_error: dict[str, Any] | None = None
        best_no_match = empty_rating(self.source, "no_match", None)
        best_no_match["match_confidence"] = None

        for query in build_query_variants(wine)[: self.max_query_variants]:
            cache_key = f"{self.source}:{query}"
            cached = self._cache.get(cache_key)
            if cached is None or cached.get("parser_version") != CACHE_VERSION:
                cached = self._search(query)
                self._cache[cache_key] = cached

            if cached.get("status") == "blocked":
                blocked_or_error = empty_rating(
                    self.source,
                    "blocked",
                    query,
                    error=cached.get("error"),
                )
                continue
            if cached.get("status") == "error":
                blocked_or_error = empty_rating(
                    self.source,
                    "error",
                    query,
                    error=cached.get("error"),
                )
                continue

            match, confidence = select_best_match(
                wine,
                cached.get("candidates") or [],
                threshold=self.match_threshold,
            )
            if match is not None:
                status = "matched" if match.get("score") is not None else "matched_no_score"
                return {
                    "source": self.source,
                    "status": status,
                    "search_query": query,
                    "matched_name": match.get("name"),
                    "matched_vintage": match.get("vintage"),
                    "score": match.get("score"),
                    "rating_count": match.get("rating_count"),
                    "url": match.get("url"),
                    "match_confidence": round(confidence, 2),
                }

            if confidence and (
                best_no_match.get("match_confidence") is None
                or confidence > best_no_match["match_confidence"]
            ):
                best_no_match = {
                    **empty_rating(self.source, "no_match", query),
                    "match_confidence": round(confidence, 2),
                }

        return best_no_match if best_no_match.get("search_query") else (
            blocked_or_error or empty_rating(self.source, "no_match", None)
        )

    def save_cache(self) -> None:
        self.cache_path.parent.mkdir(parents=True, exist_ok=True)
        self.cache_path.write_text(
            json.dumps(self._cache, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )

    def _search(self, query: str) -> dict[str, Any]:
        wait_seconds = self.delay_seconds - (time.monotonic() - self._last_request_at)
        if wait_seconds > 0:
            time.sleep(wait_seconds)

        if self.source in _DISCOVERY_FIRST_SOURCES:
            candidates = self._search_web(query)
            if candidates:
                return {
                    "status": "ok",
                    "url": None,
                    "candidates": candidates,
                    "parser_version": CACHE_VERSION,
                }
            if self.source in _SEARCH_ONLY_SOURCES:
                return {
                    "status": "ok",
                    "url": None,
                    "candidates": [],
                    "parser_version": CACHE_VERSION,
                }

        url = self.search_url_template.format(query=quote_plus(query))
        if self.verbose:
            print(f"Searching {SOURCE_LABELS.get(self.source, self.source)}: {query}")

        try:
            response = self._session.get(
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
        except RequestsError as exc:
            return {
                "status": "error",
                "error": str(exc),
                "candidates": [],
                "parser_version": CACHE_VERSION,
            }

        if self._is_blocked_response(response.status_code, response.text):
            label = SOURCE_LABELS.get(self.source, self.source)
            web_candidates = self._search_web(query)
            if web_candidates:
                return {
                    "status": "ok",
                    "url": url,
                    "candidates": web_candidates,
                    "parser_version": CACHE_VERSION,
                }
            return {
                "status": "blocked",
                "error": f"{label} returned status {response.status_code} or block page",
                "candidates": [],
                "parser_version": CACHE_VERSION,
            }

        try:
            candidates = self._parse_candidates(response.text, url=url)
            if not candidates:
                candidates = self._search_web(query)
        except Exception as exc:  # pragma: no cover - defensive against page churn.
            return {
                "status": "error",
                "error": str(exc),
                "candidates": [],
                "parser_version": CACHE_VERSION,
            }

        return {
            "status": "ok",
            "url": url,
            "candidates": candidates,
            "parser_version": CACHE_VERSION,
        }

    def _parse_candidates(self, html: str, *, url: str) -> list[dict[str, Any]]:
        candidates: list[dict[str, Any]] = []
        if self.source == "vivino":
            candidates.extend(parse_vivino_explore_candidates(html))
            candidates.extend(parse_vivino_detail_candidates(html, url=url))
            candidates.extend(parse_candidates(html, base_url=self._base_url()))
        elif self.source == "cellartracker":
            return []
        elif self.source in {"falstaff", "wine_enthusiast"}:
            candidates.extend(parse_jsonld_review_candidates(html, url=url))
        elif self.source == "wine_review_online":
            candidates.extend(parse_wine_review_online_candidates(html, url=url))
        else:
            candidates.extend(parse_rating_text_candidates(html, url=url))
        return _dedupe_candidates(candidates)

    def _search_web(self, query: str) -> list[dict[str, Any]]:
        domain = _source_domain(self.source)
        if not domain:
            return []

        for search_query in _source_search_queries(self.source, query):
            yahoo_candidates = self._search_yahoo(search_query, domain=domain)
            if yahoo_candidates:
                return yahoo_candidates

            duckduckgo_candidates = self._search_duckduckgo(search_query, domain=domain)
            if duckduckgo_candidates:
                return duckduckgo_candidates

        return []

    def _search_duckduckgo(self, search_query: str, *, domain: str) -> list[dict[str, Any]]:
        search_url = "https://duckduckgo.com/html/"
        try:
            response = self._session.get(
                f"{search_url}?q={quote_plus(search_query)}",
                headers={
                    "Accept": "text/html,application/xhtml+xml",
                    "Accept-Language": "en-US,en;q=0.9",
                },
                timeout=self.timeout_seconds,
            )
        except RequestsError:
            return []
        if response.status_code != 200:
            return []

        results = parse_duckduckgo_results(response.text, domain=domain)
        candidates: list[dict[str, Any]] = []
        for result in results[:3]:
            if self.source == "cellartracker":
                candidates.extend(parse_cellartracker_snippet(result))
            elif self.source == "vivino":
                candidates.extend(self._parse_vivino_result_page(result))
            else:
                snippet_candidates = parse_source_snippet(result, source=self.source)
                if snippet_candidates:
                    candidates.extend(snippet_candidates)
                elif self.source != "wine_review_online":
                    candidates.extend(self._parse_result_page(result))
        return _dedupe_candidates(candidates)

    def _search_yahoo(self, query: str, *, domain: str) -> list[dict[str, Any]]:
        try:
            response = self._session.get(
                f"https://search.yahoo.com/search?p={quote_plus(query)}",
                headers={
                    "Accept": "text/html,application/xhtml+xml",
                    "Accept-Language": "en-US,en;q=0.9",
                },
                timeout=self.timeout_seconds,
            )
        except RequestsError:
            return []
        if response.status_code != 200:
            return []

        results = parse_yahoo_results(response.text, domain=domain)
        candidates: list[dict[str, Any]] = []
        for result in results[:3]:
            if self.source == "cellartracker":
                candidates.extend(parse_cellartracker_snippet(result))
            elif self.source == "vivino":
                candidates.extend(self._parse_vivino_result_page(result))
            else:
                snippet_candidates = parse_source_snippet(result, source=self.source)
                if snippet_candidates:
                    candidates.extend(snippet_candidates)
                elif self.source != "wine_review_online":
                    candidates.extend(self._parse_result_page(result))
        return _dedupe_candidates(candidates)

    def _parse_result_page(self, result: dict[str, str]) -> list[dict[str, Any]]:
        url = result.get("url")
        if not url:
            return []
        try:
            response = self._session.get(
                url,
                headers={
                    "Accept": "text/html,application/xhtml+xml",
                    "Accept-Language": "en-US,en;q=0.9",
                },
                timeout=self.timeout_seconds,
            )
        except RequestsError:
            return []
        if response.status_code != 200 or self._is_blocked_response(response.status_code, response.text):
            return []
        return self._parse_candidates(response.text, url=url)

    def _parse_vivino_result_page(self, result: dict[str, str]) -> list[dict[str, Any]]:
        url = result.get("url")
        if not url:
            return []
        try:
            response = self._session.get(
                url,
                headers={
                    "Accept": "text/html,application/xhtml+xml",
                    "Accept-Language": "en-US,en;q=0.9",
                },
                timeout=self.timeout_seconds,
            )
        except RequestsError:
            return []
        if response.status_code != 200 or self._is_blocked_response(response.status_code, response.text):
            return []
        candidates = parse_vivino_detail_candidates(response.text, url=url)
        if not candidates:
            candidates = parse_vivino_explore_candidates(response.text)
        return candidates

    def _is_blocked_response(self, status_code: int, text: str) -> bool:
        if self.source == "vivino":
            return is_blocked_response(status_code, text)
        if status_code in {401, 403, 429}:
            return True
        lowered = text[:12000].lower()
        block_markers = [
            "verify you are human",
            "access denied",
            "temporarily blocked",
            "cf-chl-",
            "challenge-platform",
            "just a moment...",
        ]
        return any(marker in lowered for marker in block_markers)

    def _base_url(self) -> str:
        match = re.match(r"^(https?://[^/]+)", self.search_url_template)
        return match.group(1) if match else ""

    def _load_cache(self) -> dict[str, Any]:
        if not self.cache_path.exists():
            return {}
        try:
            return json.loads(self.cache_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return {}


def create_rating_clients(
    *,
    cache_dir: Path,
    delay_seconds: float,
    match_threshold: float,
    timeout_seconds: int = 12,
    verbose: bool = False,
) -> list[WebRatingClient]:
    return [
        WebRatingClient(
            source="vivino",
            search_url_template="https://www.vivino.com/search/wines?q={query}",
            cache_path=cache_dir / "vivino_ratings.json",
            delay_seconds=delay_seconds,
            match_threshold=match_threshold,
            timeout_seconds=timeout_seconds,
            max_query_variants=2,
            verbose=verbose,
        ),
        WebRatingClient(
            source="cellartracker",
            search_url_template="https://www.cellartracker.com/list.asp?Table=List&szSearch={query}",
            cache_path=cache_dir / "cellartracker_ratings.json",
            delay_seconds=delay_seconds,
            match_threshold=match_threshold,
            timeout_seconds=timeout_seconds,
            max_query_variants=2,
            verbose=verbose,
        ),
        WebRatingClient(
            source="falstaff",
            search_url_template="https://www.falstaff.com/en/search?search={query}",
            cache_path=cache_dir / "falstaff_ratings.json",
            delay_seconds=delay_seconds,
            match_threshold=match_threshold,
            timeout_seconds=timeout_seconds,
            max_query_variants=1,
            verbose=verbose,
        ),
        WebRatingClient(
            source="wine_enthusiast",
            search_url_template="https://www.wineenthusiast.com/?s={query}",
            cache_path=cache_dir / "wine_enthusiast_ratings.json",
            delay_seconds=delay_seconds,
            match_threshold=match_threshold,
            timeout_seconds=timeout_seconds,
            max_query_variants=1,
            verbose=verbose,
        ),
    ]


def build_query_variants(wine: dict[str, Any]) -> list[str]:
    name = str(wine.get("productLongName") or wine.get("productShortName") or "").strip()
    if not name:
        return []

    no_vintage = re.sub(r"\b(?:19|20)\d{2}\b", " ", name)
    no_vintage = re.sub(r"\s+", " ", no_vintage).strip()
    expanded_name = _expand_abbreviations(name)
    expanded_no_vintage = _expand_abbreviations(no_vintage)
    country = _english_country(wine.get("country"))
    region = wine.get("region")

    variants = [
        name,
        expanded_name,
        no_vintage,
        expanded_no_vintage,
        " ".join(part for part in [name, country] if part),
        " ".join(part for part in [expanded_name, country] if part),
        " ".join(part for part in [no_vintage, country] if part),
        build_query(wine),
        " ".join(str(part).strip() for part in [name, country, region] if part),
        " ".join(str(part).strip() for part in [expanded_name, country, region] if part),
    ]

    deduped: list[str] = []
    for variant in variants:
        normalized = re.sub(r"\s+", " ", variant).strip()
        if normalized and normalized not in deduped:
            deduped.append(normalized)
    return deduped


def parse_vivino_explore_candidates(html: str) -> list[dict[str, Any]]:
    soup = BeautifulSoup(html, "html.parser")
    container = soup.find("div", id="ssr-explore-page")
    if not container:
        return []

    raw = container.get("data-ssr-props")
    if not raw:
        return []

    props = json.loads(raw)
    results = props.get("initialExploreResults") or {}
    candidates: list[dict[str, Any]] = []

    for item in results.get("matches") or []:
        vintage = item.get("vintage") or {}
        wine = vintage.get("wine") or {}
        stats = vintage.get("statistics") or {}
        name = vintage.get("name") or " ".join(
            part for part in [wine.get("winery", {}).get("name"), wine.get("name")] if part
        )
        if not name:
            continue

        candidates.append(
            {
                "name": name,
                "vintage": _extract_year(vintage.get("year") or vintage.get("name")),
                "score": _as_float(
                    stats.get("ratings_average") or stats.get("wine_ratings_average")
                ),
                "rating_count": _as_int(
                    stats.get("ratings_count") or stats.get("wine_ratings_count")
                ),
                "url": _vivino_url(vintage, wine),
            }
        )

    return candidates


def parse_vivino_detail_candidates(html: str, *, url: str) -> list[dict[str, Any]]:
    soup = BeautifulSoup(html, "html.parser")
    candidates: list[dict[str, Any]] = []
    for container_id in ("ssr-wine-page-top-section", "ssr-vintage-page-lower-section"):
        container = soup.find("div", id=container_id)
        if not container:
            continue
        raw = container.get("data-ssr-props")
        if not raw:
            continue
        props = json.loads(raw)
        vintage = props.get("vintage") or (
            props.get("vintagePageInformation") or {}
        ).get("vintage")
        if not isinstance(vintage, dict):
            continue
        stats = vintage.get("statistics") or {}
        score = _as_float(stats.get("ratings_average"))
        if score == 0:
            score = None
        candidates.append(
            {
                "name": vintage.get("name"),
                "vintage": _extract_year(vintage.get("name")),
                "score": score,
                "rating_count": _as_int(stats.get("ratings_count")),
                "url": url,
            }
        )
    return _dedupe_candidates(candidates)


def parse_duckduckgo_results(html: str, *, domain: str) -> list[dict[str, str]]:
    soup = BeautifulSoup(html, "html.parser")
    results: list[dict[str, str]] = []
    for result in soup.select(".result"):
        link = result.select_one(".result__a")
        snippet = result.select_one(".result__snippet")
        if not link:
            continue
        url = _unwrap_duckduckgo_url(link.get("href") or "")
        if domain not in urlparse(url).netloc:
            continue
        results.append(
            {
                "title": link.get_text(" ", strip=True),
                "url": url,
                "snippet": snippet.get_text(" ", strip=True) if snippet else "",
            }
        )
    return results


def parse_yahoo_results(html: str, *, domain: str) -> list[dict[str, str]]:
    soup = BeautifulSoup(html, "html.parser")
    results: list[dict[str, str]] = []
    for result in soup.select("div.algo"):
        link = result.select_one(".compTitle a[href]")
        snippet = result.select_one(".compText")
        if not link:
            continue
        url = _unwrap_yahoo_url(link.get("href") or "")
        if domain not in urlparse(url).netloc:
            continue
        results.append(
            {
                "title": link.get_text(" ", strip=True),
                "url": url,
                "snippet": snippet.get_text(" ", strip=True) if snippet else "",
            }
        )
    return results


def parse_cellartracker_snippet(result: dict[str, str]) -> list[dict[str, Any]]:
    text = result.get("snippet") or ""
    match = re.search(
        r"Average of\s+(\d+(?:[.,]\d+)?)\s+points\s+in\s+([\d,]+)\s+community wine reviews?\s+on\s+(.+?)(?:,\s+plus|\.|$)",
        text,
        flags=re.IGNORECASE,
    )
    if not match:
        return []
    name = match.group(3).strip()
    if re.match(r"\d+\s+wines\s+from\b", name, flags=re.IGNORECASE):
        return []
    return [
        {
            "name": name,
            "vintage": _extract_year(name),
            "score": _as_float(match.group(1)),
            "rating_count": _as_int(match.group(2)),
            "url": result.get("url"),
        }
    ]


def parse_source_snippet(result: dict[str, str], *, source: str) -> list[dict[str, Any]]:
    if source == "falstaff":
        return parse_falstaff_snippet(result)
    if source == "wine_enthusiast":
        return parse_wine_enthusiast_snippet(result)
    if source == "wine_review_online":
        return parse_wine_review_online_snippet(result)

    text = " ".join([result.get("title") or "", result.get("snippet") or ""])
    candidates = parse_rating_text_candidates(text, url=result.get("url") or "")
    for candidate in candidates:
        candidate["name"] = result.get("title") or candidate.get("name")
        candidate["vintage"] = _extract_year(candidate.get("name"))
    return candidates


def parse_falstaff_snippet(result: dict[str, str]) -> list[dict[str, Any]]:
    text = " ".join([result.get("title") or "", result.get("snippet") or ""])
    score_match = re.search(r"\bwith\s+(\d{2,3}(?:[.,]\d+)?)\s+Falstaff points?\b", text, re.I)
    if not score_match:
        score_match = re.search(r"\b(\d{2,3}(?:[.,]\d+)?)\s+Falstaff points?\b", text, re.I)
    if not score_match:
        return []

    name = _falstaff_name_from_result(result)
    if not name:
        return []

    return [
        {
            "name": name,
            "vintage": _extract_year(name) or _extract_year(result.get("url")),
            "score": _as_float(score_match.group(1)),
            "rating_count": None,
            "url": result.get("url"),
        }
    ]


def parse_wine_enthusiast_snippet(result: dict[str, str]) -> list[dict[str, Any]]:
    text = " ".join([result.get("title") or "", result.get("snippet") or ""])
    score_match = re.search(r"\bRating\s+(\d{2,3}(?:[.,]\d+)?)\b", text, re.I)
    if not score_match:
        return []

    name = _clean_search_title(result.get("title") or "", source="wine_enthusiast")
    if not name:
        return []

    return [
        {
            "name": name,
            "vintage": _extract_year(name) or _extract_year(result.get("url")),
            "score": _as_float(score_match.group(1)),
            "rating_count": None,
            "url": result.get("url"),
        }
    ]


def parse_wine_review_online_snippet(result: dict[str, str]) -> list[dict[str, Any]]:
    text = " ".join([result.get("title") or "", result.get("snippet") or ""])
    score_match = re.search(r"\bScore\s+(\d{2,3}(?:[.,]\d+)?)\b", text, re.I)
    if not score_match:
        return []

    name = _clean_search_title(result.get("title") or "", source="wine_review_online")
    if not name:
        return []

    return [
        {
            "name": name,
            "vintage": _extract_year(name) or _extract_year(result.get("url")),
            "score": _as_float(score_match.group(1)),
            "rating_count": None,
            "url": result.get("url"),
        }
    ]


def parse_jsonld_review_candidates(html: str, *, url: str) -> list[dict[str, Any]]:
    soup = BeautifulSoup(html, "html.parser")
    candidates: list[dict[str, Any]] = []
    fallback_name = _page_title(soup)

    for script in soup.find_all("script", attrs={"type": re.compile("ld\\+json", re.I)}):
        raw = script.string or script.get_text()
        if not raw:
            continue
        try:
            data = json.loads(raw)
        except json.JSONDecodeError:
            continue
        for item, rating, inherited_name in _iter_jsonld_ratings(data, fallback_name):
            score = _as_float(rating.get("ratingValue") or rating.get("value"))
            if score is None:
                continue
            name = _jsonld_name(item, inherited_name, fallback_name)
            candidates.append(
                {
                    "name": name,
                    "vintage": _extract_year(name) or _extract_year(url),
                    "score": score,
                    "rating_count": _as_int(
                        item.get("reviewCount")
                        or item.get("ratingCount")
                        or item.get("rating_count")
                        or rating.get("ratingCount")
                    ),
                    "url": url,
                }
            )

    return _dedupe_candidates(candidates)


def parse_wine_review_online_candidates(html: str, *, url: str) -> list[dict[str, Any]]:
    soup = BeautifulSoup(html, "html.parser")
    name = _page_title(soup)
    text = soup.get_text(" ", strip=True)
    score_match = re.search(
        r"\bScore\s+(\d{2,3}(?:[.,]\d+)?)\b",
        text,
        flags=re.IGNORECASE,
    )
    if not name or not score_match:
        return []

    return [
        {
            "name": name,
            "vintage": _extract_year(name) or _extract_year(url),
            "score": _as_float(score_match.group(1)),
            "rating_count": None,
            "url": url,
        }
    ]


def parse_rating_text_candidates(html: str, *, url: str) -> list[dict[str, Any]]:
    title_match = re.search(r"<title[^>]*>(.*?)</title>", html, flags=re.IGNORECASE | re.DOTALL)
    if not title_match:
        return []

    title = re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", title_match.group(1))).strip()
    if not title:
        return []

    text = re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", html))
    score_match = re.search(
        r"\b(?:rating|score|points?)\D{0,20}(\d{1,3}(?:[.,]\d+)?)\b",
        text,
        flags=re.IGNORECASE,
    )
    count_match = re.search(r"([\d,.]+)\s+(?:ratings?|reviews?|votes?)", text, flags=re.IGNORECASE)
    if not score_match and not count_match:
        return []

    return [
        {
            "name": title,
            "vintage": None,
            "score": _as_float(score_match.group(1) if score_match else None),
            "rating_count": _as_int(count_match.group(1) if count_match else None),
            "url": url,
        }
    ]


def _dedupe_candidates(candidates: list[dict[str, Any]]) -> list[dict[str, Any]]:
    deduped: list[dict[str, Any]] = []
    seen: set[tuple[str | None, str | None]] = set()
    for candidate in candidates:
        key = (candidate.get("name"), candidate.get("url"))
        if not key[0] or key in seen:
            continue
        seen.add(key)
        deduped.append(candidate)
    return deduped


def _source_domain(source: str) -> str | None:
    return {
        "vivino": "vivino.com",
        "cellartracker": "cellartracker.com",
        "falstaff": "falstaff.com",
        "wine_enthusiast": "wineenthusiast.com",
        "wine_review_online": "winereviewonline.com",
    }.get(source)


def _source_search_queries(source: str, query: str) -> list[str]:
    domain = _source_domain(source)
    label = SOURCE_LABELS.get(source, source)
    if source == "cellartracker":
        raw_queries = [
            f"{query} CellarTracker rating",
            f"site:cellartracker.com/wine.asp {query}",
        ]
    elif source == "falstaff":
        raw_queries = [
            f"{query} Falstaff rating",
            f"site:falstaff.com/en/wines {query}",
        ]
    elif source == "wine_enthusiast":
        raw_queries = [
            f"{query} Wine Enthusiast Rating and Review",
            f"site:wineenthusiast.com/buying-guide {query}",
        ]
    elif source == "wine_review_online":
        raw_queries = [
            f"site:winereviewonline.com/wine-review {query}",
            f"{query} Wine Review Online Score",
        ]
    else:
        raw_queries = [
            f"{query} {label} rating",
            f"site:{domain} {query} wine rating" if domain else "",
        ]

    deduped: list[str] = []
    for raw_query in raw_queries:
        normalized = _ascii_query(re.sub(r"\s+", " ", raw_query).strip())
        if normalized and normalized not in deduped:
            deduped.append(normalized)
    return deduped


def _falstaff_name_from_result(result: dict[str, str]) -> str | None:
    title = _clean_search_title(result.get("title") or "", source="falstaff")
    if not title:
        return None

    parts = [part.strip() for part in re.split(r"\s+-\s+", title) if part.strip()]
    if len(parts) >= 2:
        wine_name = parts[0]
        producer = parts[-1]
        if producer.casefold() not in wine_name.casefold():
            return f"{producer} {wine_name}".strip()
    return title


def _clean_search_title(title: str, *, source: str) -> str | None:
    title = re.sub(r"\s+", " ", title).strip()
    if not title:
        return None

    title = re.sub(r"^.+?\s+https?://[^ ]+\s+›\s+", "", title)
    if " › " in title:
        title = title.split(" › ")[-1].strip()
    if source == "falstaff":
        title = re.sub(r"\s+-\s+Falstaff\s*$", "", title, flags=re.I)
    elif source == "wine_enthusiast":
        title = re.sub(r"\s+Rating and Review\s*(?:\|\s*Wine Enthusiast)?\s*$", "", title, flags=re.I)
        title = re.sub(r"\s+-\s+Wine Enthusiast\s*$", "", title, flags=re.I)
    elif source == "wine_review_online":
        title = re.sub(r"\s+-\s+Wine Review Online\s*$", "", title, flags=re.I)
    return title.strip(" -|") or None


def _iter_jsonld_ratings(
    value: Any,
    inherited_name: str | None = None,
) -> list[tuple[dict[str, Any], dict[str, Any], str | None]]:
    ratings: list[tuple[dict[str, Any], dict[str, Any], str | None]] = []
    if isinstance(value, list):
        for item in value:
            ratings.extend(_iter_jsonld_ratings(item, inherited_name))
        return ratings
    if not isinstance(value, dict):
        return ratings

    value_type = value.get("@type")
    value_types = value_type if isinstance(value_type, list) else [value_type]
    if inherited_name and any(str(item).casefold() == "review" for item in value_types):
        current_name = inherited_name
    else:
        current_name = str(value.get("name") or inherited_name or "").strip() or inherited_name
    brand = value.get("brand")
    brand_name = None
    if isinstance(brand, dict):
        brand_name = brand.get("name")
    elif isinstance(brand, str):
        brand_name = brand
    if current_name and brand_name and str(brand_name).casefold() not in current_name.casefold():
        current_name = f"{brand_name} {current_name}"
    item_reviewed = value.get("itemReviewed")
    if isinstance(item_reviewed, dict) and item_reviewed.get("name"):
        current_name = str(item_reviewed["name"]).strip()

    for key in ("reviewRating", "aggregateRating"):
        rating = value.get(key)
        if isinstance(rating, dict):
            ratings.append((value, rating, current_name))

    for key, nested in value.items():
        if key in {"reviewRating", "aggregateRating"}:
            continue
        if isinstance(nested, (dict, list)):
            ratings.extend(_iter_jsonld_ratings(nested, current_name))
    return ratings


def _jsonld_name(
    item: dict[str, Any],
    inherited_name: str | None,
    fallback_name: str | None,
) -> str | None:
    item_reviewed = item.get("itemReviewed")
    if isinstance(item_reviewed, dict) and item_reviewed.get("name"):
        return str(item_reviewed["name"]).strip()
    item_type = item.get("@type")
    item_types = item_type if isinstance(item_type, list) else [item_type]
    if inherited_name and any(str(value).casefold() == "review" for value in item_types):
        return inherited_name
    brand = item.get("brand")
    brand_name = brand.get("name") if isinstance(brand, dict) else brand
    if item.get("name") and brand_name and str(brand_name).casefold() not in str(item["name"]).casefold():
        return f"{brand_name} {item['name']}".strip()
    for key in ("name", "headline"):
        if item.get(key):
            return str(item[key]).strip()
    return inherited_name or fallback_name


def _page_title(soup: BeautifulSoup) -> str | None:
    heading = soup.find("h1")
    if heading:
        text = heading.get_text(" ", strip=True)
        if text:
            return text
    if soup.title:
        text = soup.title.get_text(" ", strip=True)
        text = re.sub(
            r"\s+[-|]\s+(?:Falstaff|Wine Enthusiast|Wine Review Online|Winereviewonline).*$",
            "",
            text,
            flags=re.IGNORECASE,
        ).strip()
        return text or None
    return None


def _unwrap_duckduckgo_url(url: str) -> str:
    if url.startswith("//"):
        url = f"https:{url}"
    parsed = urlparse(url)
    wrapped = parse_qs(parsed.query).get("uddg")
    return unquote(wrapped[0]) if wrapped else url


def _unwrap_yahoo_url(url: str) -> str:
    parsed = urlparse(url)
    marker = "/RU="
    if marker in parsed.path:
        encoded = parsed.path.split(marker, 1)[1].split("/", 1)[0]
        return unquote(encoded)
    return url


def _ascii_query(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value)
    ascii_value = "".join(ch for ch in normalized if not unicodedata.combining(ch))
    return ascii_value.encode("ascii", "ignore").decode("ascii")


def _english_country(country: Any) -> str | None:
    if not country:
        return None
    translations = {
        "frankrike": "France",
        "italia": "Italy",
        "spania": "Spain",
        "tyskland": "Germany",
        "østerrike": "Austria",
        "sør-afrika": "South Africa",
        "hellas": "Greece",
        "libanon": "Lebanon",
    }
    text = str(country)
    return translations.get(text.casefold(), text)


def _expand_abbreviations(value: str) -> str:
    value = re.sub(r"\bCh\.\s*", "Chateau ", value, flags=re.IGNORECASE)
    value = re.sub(r"\bDom\.\s*", "Domaine ", value, flags=re.IGNORECASE)
    return re.sub(r"\s+", " ", value).strip()


def _extract_year(value: Any) -> str | None:
    if value is None:
        return None
    match = re.search(r"\b(19\d{2}|20\d{2})\b", str(value))
    return match.group(1) if match else None


def _vivino_url(vintage: dict[str, Any], wine: dict[str, Any]) -> str | None:
    if vintage.get("seo_name"):
        return f"https://www.vivino.com/wines/{vintage['seo_name']}"
    if wine.get("id"):
        return f"https://www.vivino.com/w/{wine['id']}"
    return None


def default_ratings() -> dict[str, dict[str, Any]]:
    return {source: empty_rating(source, "not_requested", None) for source in SOURCE_LABELS}


def best_rating(wine: dict[str, Any]) -> dict[str, Any]:
    ratings = wine.get("ratings") or {}
    normalized_scores = [
        score
        for rating in ratings.values()
        if isinstance(rating, dict)
        and rating.get("status") == "matched"
        for score in [normalized_rating_score(rating)]
        if score is not None
    ]
    if normalized_scores:
        return {
            **empty_rating("average", "matched", None),
            "score": round(sum(normalized_scores) / len(normalized_scores), 1),
            "rating_count": len(normalized_scores),
        }
    return wine.get("rating") or empty_rating("vivino", "not_requested", None)


def normalized_rating_score(rating: dict[str, Any] | None) -> float | None:
    if not rating or rating.get("score") is None:
        return None
    score = _as_float(rating.get("score"))
    if score is None:
        return None
    source = rating.get("source")
    if source == "vivino" or score <= 5:
        return round(score * 20, 1)
    return round(score, 1)


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
