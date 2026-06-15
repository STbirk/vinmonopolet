from __future__ import annotations

from vinliste.vinmonopolet import (
    VinmonopoletClient,
    current_price,
    is_red_wine,
    product_to_wine,
)


def test_is_red_wine_matches_norwegian_classification() -> None:
    product = {"classification": {"mainProductTypeName": "Rødvin"}}

    assert is_red_wine(product)


def test_is_red_wine_matches_public_search_category() -> None:
    product = {"main_category": {"code": "rødvin", "name": "Rødvin"}}

    assert is_red_wine(product)


def test_is_red_wine_rejects_white_wine() -> None:
    product = {"classification": {"mainProductTypeName": "Hvitvin"}}

    assert not is_red_wine(product)


def test_current_price_selects_latest_effective_price() -> None:
    prices = [
        {"priceValidFrom": "2024-01-01", "salesPrice": 100},
        {"priceValidFrom": "2025-01-01", "salesPrice": 120},
        {"priceValidFrom": "2999-01-01", "salesPrice": 999},
    ]

    assert current_price(prices)["salesPrice"] == 120


def test_product_to_wine_extracts_expected_fields() -> None:
    product = {
        "basic": {
            "productId": "123",
            "productShortName": "Short",
            "productLongName": "Long 2020",
            "vintage": "2020",
            "volume": 0.75,
            "alcoholContent": 13.5,
            "productStatusSaleName": "I salg",
        },
        "origins": {"origin": {"country": "Italia", "region": "Piemonte"}},
        "classification": {"mainProductTypeName": "Rødvin"},
        "ingredients": {"grapes": [{"grapeDesc": "Nebbiolo", "grapePct": "100"}]},
        "description": {
            "characteristics": {"taste": "Fruktig"},
            "tannins": "8",
            "recommendedFood": [{"foodDesc": "Storfe"}],
        },
        "assortment": {"assortment": "Basis"},
        "prices": [{"priceValidFrom": "2025-01-01", "salesPrice": 250}],
    }

    wine = product_to_wine(product)

    assert wine["productId"] == "123"
    assert wine["country"] == "Italia"
    assert wine["grapes"] == [{"name": "Nebbiolo", "percent": "100"}]
    assert wine["sensory"]["recommendedFood"] == ["Storfe"]
    assert wine["price"]["salesPrice"] == 250


def test_product_to_wine_extracts_public_search_product() -> None:
    product = {
        "code": "9929801",
        "name": "Ressia Barbera d'Alba Superiore 2021",
        "main_category": {"code": "rødvin", "name": "Rødvin"},
        "main_country": {"name": "Italia"},
        "district": {"name": "Piemonte"},
        "sub_District": {"name": "Barbera d'Alba"},
        "price": {"value": 310.0},
        "volume": {"value": 75.0, "formattedValue": "75 cl"},
        "alcohol": {"value": 14.5},
        "product_selection": "Bestillingsutvalget",
        "status": "aktiv",
        "url": "/Land/Italia/Piemonte/p/9929801",
    }

    wine = product_to_wine(product)

    assert wine["productId"] == "9929801"
    assert wine["vintage"] == "2021"
    assert wine["volume"] == 0.75
    assert wine["price"]["salesPrice"] == 310.0
    assert wine["price"]["salesPricePrLiter"] == 413.33
    assert wine["url"] == "https://www.vinmonopolet.no/Land/Italia/Piemonte/p/9929801"


def test_client_paginates_until_total_count(monkeypatch) -> None:
    calls = []

    class FakeResponse:
        def __init__(self, payload, total):
            self.payload = payload
            self.headers = {"x-total-count": str(total)}

        def raise_for_status(self):
            return None

        def json(self):
            return self.payload

    def fake_get(url, params, headers, timeout):
        calls.append(params["start"])
        if params["start"] == 0:
            return FakeResponse([{"basic": {"productId": "1"}}], total=2)
        return FakeResponse([{"basic": {"productId": "2"}}], total=2)

    monkeypatch.setattr("vinliste.vinmonopolet.requests.get", fake_get)
    client = VinmonopoletClient(api_key="test-key", page_size=1)

    products = list(client.iter_products())

    assert calls == [0, 1]
    assert [product["basic"]["productId"] for product in products] == ["1", "2"]
