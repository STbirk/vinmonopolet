from __future__ import annotations

from datetime import datetime
from collections.abc import Iterable
from typing import Any, Protocol

from .vinmonopolet import is_red_wine, product_to_wine


class ProductClient(Protocol):
    def iter_products(self) -> Iterable[dict[str, Any]]: ...


class RatingClient(Protocol):
    def rate_wine(self, wine: dict[str, Any]) -> dict[str, Any]: ...


def build_export(
    *,
    client: ProductClient,
    rating_client: RatingClient | None,
    rating_source: str,
    match_threshold: float,
    limit: int | None,
    generated_at: datetime,
    verbose: bool = False,
) -> dict[str, Any]:
    wines: list[dict[str, Any]] = []

    for product in client.iter_products():
        if not is_red_wine(product):
            continue

        wine = product_to_wine(product)
        if rating_client is None:
            wine["rating"] = {
                "source": rating_source,
                "status": "skipped",
                "search_query": None,
                "matched_name": None,
                "matched_vintage": None,
                "score": None,
                "rating_count": None,
                "url": None,
                "match_confidence": None,
            }
        else:
            wine["rating"] = rating_client.rate_wine(wine)

        wines.append(wine)
        if verbose and len(wines) % 25 == 0:
            print(f"Prepared {len(wines)} red wines...")

        if limit is not None and len(wines) >= limit:
            break

    return {
        "metadata": {
            "generated_at": generated_at.isoformat(),
            "source": "Vinmonopolet Product API",
            "rating_source": rating_source,
            "match_threshold": match_threshold,
            "count": len(wines),
        },
        "wines": wines,
    }
