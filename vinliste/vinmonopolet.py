from __future__ import annotations

import time
from collections.abc import Iterable
from datetime import date
from typing import Any

from curl_cffi import requests as curl_requests
import requests

from .matching import extract_vintage, normalize_text


BASE_URL = "https://apis.vinmonopolet.no/products/v0"


class VinmonopoletClient:
    def __init__(
        self,
        *,
        api_key: str,
        base_url: str = BASE_URL,
        page_size: int = 1000,
        timeout_seconds: int = 30,
        verbose: bool = False,
    ) -> None:
        self.api_key = api_key
        self.base_url = base_url.rstrip("/")
        self.page_size = page_size
        self.timeout_seconds = timeout_seconds
        self.verbose = verbose

    def iter_products(self) -> list[dict[str, Any]]:
        products: list[dict[str, Any]] = []
        start = 0
        total_count: int | None = None

        while True:
            batch, headers = self._fetch_page(start=start, page_size=self.page_size)

            products.extend(batch)
            if self.verbose:
                print(f"Fetched {len(products)} products...")

            header_total = headers.get("x-total-count")
            if header_total and header_total.isdigit():
                total_count = int(header_total)

            if not batch:
                break
            start += len(batch)
            if total_count is not None and start >= total_count:
                break
            if len(batch) < self.page_size:
                break

        return products

    def has_enriched_data(self) -> bool:
        batch, _headers = self._fetch_page(start=0, page_size=50)
        return any(
            any(key in product for key in ("classification", "prices", "origins"))
            for product in batch
        )

    def _fetch_page(
        self,
        *,
        start: int,
        page_size: int,
    ) -> tuple[list[dict[str, Any]], dict[str, str]]:
        response = requests.get(
            f"{self.base_url}/details-normal",
            params={"maxResults": page_size, "start": start},
            headers={
                "Accept": "application/json",
                "Ocp-Apim-Subscription-Key": self.api_key,
            },
            timeout=self.timeout_seconds,
        )
        response.raise_for_status()
        batch = response.json()
        if not isinstance(batch, list):
            raise ValueError("Vinmonopolet details-normal returned non-list JSON")
        return batch, dict(response.headers)


class AutoVinmonopoletClient:
    def __init__(self, *, api_key: str, verbose: bool = False) -> None:
        self.api_key = api_key
        self.verbose = verbose

    def iter_products(self) -> Iterable[dict[str, Any]]:
        api_client = VinmonopoletClient(api_key=self.api_key, verbose=self.verbose)
        if api_client.has_enriched_data():
            if self.verbose:
                print("Using Vinmonopolet Product API details-normal data.")
            yield from api_client.iter_products()
            return

        if self.verbose:
            print(
                "Product API subscription returned sparse records; "
                "using public Vinmonopolet search data."
            )
        yield from VinmonopoletShopClient(verbose=self.verbose).iter_products()


class VinmonopoletShopClient:
    def __init__(
        self,
        *,
        page_size: int = 100,
        timeout_seconds: int = 30,
        request_delay_seconds: float = 0.5,
        verbose: bool = False,
    ) -> None:
        self.page_size = page_size
        self.timeout_seconds = timeout_seconds
        self.request_delay_seconds = request_delay_seconds
        self.verbose = verbose
        self.session = curl_requests.Session(impersonate="chrome")

    def iter_products(self) -> Iterable[dict[str, Any]]:
        self.session.get("https://www.vinmonopolet.no/", timeout=self.timeout_seconds)

        current_page = 0
        total_pages: int | None = None
        fetched_count = 0

        while True:
            response = self._fetch_public_page(current_page)
            payload = response.json()
            batch = payload.get("products") or []
            for product in batch:
                product["_source"] = "vinmonopolet_public_search"

            pagination = payload.get("pagination") or {}
            total_pages = pagination.get("totalPages") or total_pages
            fetched_count += len(batch)
            if self.verbose:
                total_results = pagination.get("totalResults")
                print(
                    f"Fetched public search page {current_page + 1}"
                    + (f"/{total_pages}" if total_pages else "")
                    + (f" ({fetched_count}/{total_results})" if total_results else "")
                )

            for product in batch:
                yield product

            current_page += 1
            if not batch:
                break
            if total_pages is not None and current_page >= int(total_pages):
                break
            time.sleep(self.request_delay_seconds)

    def _fetch_public_page(self, current_page: int):
        for attempt in range(4):
            response = self.session.get(
                "https://www.vinmonopolet.no/vmpws/v2/vmp/products/search",
                params={
                    "searchType": "product",
                    "q": ":relevance:mainCategory:rødvin",
                    "pageSize": self.page_size,
                    "currentPage": current_page,
                },
                headers={"Accept": "application/json, text/plain, */*"},
                timeout=self.timeout_seconds,
            )
            if response.status_code != 429:
                response.raise_for_status()
                return response
            wait_seconds = _retry_after_seconds(response.headers.get("retry-after"))
            if wait_seconds is None:
                wait_seconds = min(60.0, 5.0 * (attempt + 1))
            if self.verbose:
                print(f"Rate limited by public search; waiting {wait_seconds:.0f}s...")
            time.sleep(wait_seconds)
        response.raise_for_status()
        return response


def is_red_wine(product: dict[str, Any]) -> bool:
    public_category = product.get("main_category") or {}
    if normalize_text(public_category.get("code")) == "rodvin":
        return True
    if normalize_text(public_category.get("name")) == "rodvin":
        return True

    classification = product.get("classification") or {}
    values = [
        classification.get("mainProductTypeName"),
        classification.get("subProductTypeName"),
        classification.get("productTypeName"),
        classification.get("productGroupName"),
    ]
    normalized = {normalize_text(str(value)) for value in values if value}
    return any(value in {"rodvin", "red wine"} for value in normalized)


def product_to_wine(product: dict[str, Any]) -> dict[str, Any]:
    if "code" in product and "main_category" in product:
        return public_search_product_to_wine(product)

    basic = product.get("basic") or {}
    origins = product.get("origins") or {}
    origin = origins.get("origin") or {}
    production = origins.get("production") or {}
    classification = product.get("classification") or {}
    ingredients = product.get("ingredients") or {}
    description = product.get("description") or {}
    assortment = product.get("assortment") or {}

    return {
        "productId": basic.get("productId"),
        "productShortName": basic.get("productShortName"),
        "productLongName": basic.get("productLongName"),
        "vintage": basic.get("vintage"),
        "volume": basic.get("volume"),
        "alcoholContent": basic.get("alcoholContent"),
        "ageLimit": basic.get("ageLimit"),
        "country": origin.get("country") or production.get("country"),
        "region": origin.get("region") or production.get("region"),
        "subRegion": origin.get("subRegion"),
        "classification": {
            "mainProductTypeName": classification.get("mainProductTypeName"),
            "subProductTypeName": classification.get("subProductTypeName"),
            "productTypeName": classification.get("productTypeName"),
            "productGroupName": classification.get("productGroupName"),
        },
        "grapes": [
            {
                "name": grape.get("grapeDesc"),
                "percent": grape.get("grapePct"),
            }
            for grape in (ingredients.get("grapes") or [])
        ],
        "sugar": ingredients.get("sugar"),
        "acid": ingredients.get("acid"),
        "sensory": {
            "colour": ((description.get("characteristics") or {}).get("colour")),
            "odour": ((description.get("characteristics") or {}).get("odour")),
            "taste": ((description.get("characteristics") or {}).get("taste")),
            "freshness": description.get("freshness"),
            "fullness": description.get("fullness"),
            "bitterness": description.get("bitterness"),
            "sweetness": description.get("sweetness"),
            "tannins": description.get("tannins"),
            "recommendedFood": [
                food.get("foodDesc")
                for food in (description.get("recommendedFood") or [])
                if food.get("foodDesc")
            ],
        },
        "assortment": {
            "assortment": assortment.get("assortment"),
            "validFrom": assortment.get("validFrom"),
            "listedFrom": assortment.get("listedFrom"),
        },
        "saleStatus": {
            "name": basic.get("productStatusSaleName"),
            "validFrom": basic.get("productStatusSaleValidFrom"),
        },
        "availability": {
            "inStock": _is_official_product_available(product),
            "summary": basic.get("productStatusSaleName"),
        },
        "url": (
            f"https://www.vinmonopolet.no/p/{basic.get('productId')}"
            if basic.get("productId")
            else None
        ),
        "price": current_price(product.get("prices") or []),
    }


def public_search_product_to_wine(product: dict[str, Any]) -> dict[str, Any]:
    name = product.get("name")
    country = product.get("main_country") or {}
    district = product.get("district") or {}
    sub_district = product.get("sub_District") or {}
    category = product.get("main_category") or {}
    price = product.get("price") or {}
    volume = product.get("volume") or {}
    alcohol = product.get("alcohol") or {}
    volume_liters = _centiliters_to_liters(volume.get("value"))
    sales_price = price.get("value")

    return {
        "productId": product.get("code"),
        "productShortName": name,
        "productLongName": name,
        "vintage": extract_vintage(name),
        "volume": volume_liters,
        "volumeFormatted": volume.get("formattedValue"),
        "alcoholContent": alcohol.get("value"),
        "ageLimit": "18",
        "country": country.get("name"),
        "region": district.get("name"),
        "subRegion": sub_district.get("name"),
        "classification": {
            "mainProductTypeName": category.get("name"),
            "subProductTypeName": (product.get("main_sub_category") or {}).get("name"),
            "productTypeName": category.get("name"),
            "productGroupName": category.get("name"),
        },
        "grapes": [],
        "sugar": None,
        "acid": None,
        "sensory": {
            "colour": None,
            "odour": None,
            "taste": None,
            "freshness": None,
            "fullness": None,
            "bitterness": None,
            "sweetness": None,
            "tannins": None,
            "recommendedFood": [],
        },
        "assortment": {
            "assortment": product.get("product_selection"),
            "validFrom": None,
            "listedFrom": None,
        },
        "saleStatus": {
            "name": product.get("status"),
            "validFrom": None,
        },
        "availability": public_product_availability(product),
        "price": {
            "priceValidFrom": None,
            "salesPrice": sales_price,
            "salesPricePrLiter": (
                round(sales_price / volume_liters, 2)
                if sales_price is not None and volume_liters
                else None
            ),
            "bottleReturnValue": None,
        },
        "url": (
            f"https://www.vinmonopolet.no{product.get('url')}"
            if product.get("url")
            else None
        ),
        "source": product.get("_source", "vinmonopolet_public_search"),
    }


def public_product_availability(product: dict[str, Any]) -> dict[str, Any]:
    availability = product.get("productAvailability") or {}
    delivery = availability.get("deliveryAvailability") or {}
    stores = availability.get("storesAvailability") or {}
    in_stock = bool(
        product.get("buyable")
        or delivery.get("availableForPurchase")
        or stores.get("availableForPurchase")
    )
    summaries: list[str] = []
    for section in (delivery, stores):
        for info in section.get("infos") or []:
            text = info.get("readableValue") or info.get("availability")
            if text and text not in summaries:
                summaries.append(text)
    return {
        "inStock": in_stock,
        "summary": "; ".join(summaries) or product.get("status"),
    }


def _is_official_product_available(product: dict[str, Any]) -> bool | None:
    basic = product.get("basic") or {}
    status = normalize_text(basic.get("productStatusSaleName"))
    if not status:
        return None
    return status in {"aktiv", "active", "kan bestilles", "i salg"}


def _centiliters_to_liters(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return round(float(value) / 100, 4)
    except (TypeError, ValueError):
        return None


def _retry_after_seconds(value: str | None) -> float | None:
    if not value:
        return None
    try:
        return max(1.0, min(600.0, float(value) + 1.0))
    except ValueError:
        return None


def current_price(prices: list[dict[str, Any]]) -> dict[str, Any] | None:
    if not prices:
        return None

    today = date.today()

    def valid_from(price: dict[str, Any]) -> date:
        raw = price.get("priceValidFrom")
        if not raw:
            return date.min
        try:
            return date.fromisoformat(str(raw)[:10])
        except ValueError:
            return date.min

    eligible = [price for price in prices if valid_from(price) <= today] or prices
    selected = max(eligible, key=valid_from)
    return {
        "priceValidFrom": selected.get("priceValidFrom"),
        "salesPrice": selected.get("salesPrice"),
        "salesPricePrLiter": selected.get("salesPricePrLiter"),
        "bottleReturnValue": selected.get("bottleReturnValue"),
    }
