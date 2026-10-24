using Vinliste.Core.Vinmonopolet;

namespace Vinliste.Core.Tests;

public class VinmonopoletParserTests
{
    [Fact]
    public void ParseSearchPage_reads_listing_fields_and_pagination()
    {
        var page = VinmonopoletParser.ParseSearchPage(Fixtures.Read("vmp_search.json"));

        Assert.Equal(6780, page.TotalPages);
        Assert.Equal(13560, page.TotalResults);
        Assert.Equal(2, page.Wines.Count);

        var wine = page.Wines[0];
        Assert.Equal("11929001", wine.Code);
        Assert.Equal("Biondi Santi Rosso di Montalcino", wine.Name);
        Assert.Equal("rødvin", wine.CategoryCode);
        Assert.Equal("Italia", wine.Country);
        Assert.Equal("Toscana", wine.District);
        Assert.Equal(949.0, wine.Price);
        Assert.Equal(75.0, wine.VolumeCl);
        Assert.Equal(1265.33, wine.LitrePrice);
        Assert.Equal(13.5, wine.Alcohol);
        Assert.True(wine.Buyable);
        Assert.True(wine.AvailableOnline);
        Assert.Contains("Kan bestilles", wine.AvailabilityText);
        Assert.Equal("https://www.vinmonopolet.no/p/11929001", wine.Url);
        Assert.Contains("300x300", wine.ImageUrl);
    }

    [Fact]
    public void ParseProduct_reads_tasting_notes_characteristics_grapes_and_food()
    {
        var details = VinmonopoletParser.ParseProduct(Fixtures.Read("vmp_product_full.json"));

        Assert.Equal("11929001", details.Code);
        Assert.Equal("2022", details.Vintage);
        Assert.Equal("Biondi Santi Il Greppo", details.Producer);
        Assert.Equal("Signature Wines AS", details.Wholesaler);
        Assert.Equal("Skanlog AS", details.Distributor);
        Assert.Equal("Dyp mursteinsrød.", details.Colour);
        Assert.StartsWith("Fruktdrevet", details.Smell);
        Assert.StartsWith("Frisk og fruktdrevet", details.Taste);
        Assert.Contains("slavonske", details.Method);
        Assert.Equal("Drikkeklar nå, men kan også lagres", details.StoragePotential);
        Assert.Equal("Frisk og fruktig", details.StyleName);
        Assert.Equal("Naturkork", details.Cork);
        Assert.Equal("Glass", details.PackageType);
        Assert.Equal("Sulfitt", details.Allergens);
        Assert.Equal("Under 3 g/l", details.Sugar);
        Assert.Equal("5,4 g/l", details.Acid);
        Assert.Equal(7, details.Fullness);
        Assert.Equal(6, details.Freshness);
        Assert.NotNull(details.Tannins);
        Assert.Equal(1265.33, details.LitrePrice);
        Assert.Contains("515x515", details.ImageUrl);

        var grape = Assert.Single(details.Grapes);
        Assert.Equal("Brunello", grape.Name);
        Assert.Equal(100, grape.Percent);

        Assert.Equal(["Storfe", "Lam og sau", "Småvilt"], details.FoodPairings.Select(f => f.Name));
    }

    [Fact]
    public void ParseStock_reads_stores_and_levels()
    {
        var observed = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        var stock = VinmonopoletParser.ParseStock(Fixtures.Read("vmp_stock.json"), observed);

        Assert.Equal(8, stock.Count);
        var first = stock[0];
        Assert.Equal("178", first.Store.StoreId);
        Assert.Equal("Oslo, Kiellandsplass", first.Store.Name);
        Assert.Equal("Oslo", first.Store.Town);
        Assert.Equal(2, first.StockLevel);
        Assert.Equal("2,0 km", first.Distance);
        Assert.NotNull(first.Store.Latitude);
        Assert.Equal(observed, first.ObservedAt);
    }

    [Theory]
    [InlineData("Barolo 2019", "2019")]
    [InlineData("Château X 1998 Magnum", "1998")]
    [InlineData("Rosso di Montalcino", null)]
    [InlineData("Cuvée 3000", null)]
    public void ExtractVintage_finds_plausible_years(string name, string? expected) =>
        Assert.Equal(expected, VinmonopoletParser.ExtractVintage(name));

    [Fact]
    public void SearchUrl_sorts_by_name_for_stable_paging_and_encodes_category()
    {
        var url = VinmonopoletClient.SearchUrl("rosévin", 3);

        Assert.Contains("currentPage=3", url);
        Assert.Contains("pageSize=24", url);
        Assert.Contains(Uri.EscapeDataString(":name-asc:mainCategory:rosévin"), url);
    }
}
