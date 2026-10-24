namespace Vinliste.Core.Models;

/// <summary>Listing data for one product, as returned by the Vinmonopolet search.</summary>
public sealed record WineListing
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? CategoryCode { get; init; }
    public string? Category { get; init; }
    public string? SubCategory { get; init; }
    public string? Country { get; init; }
    public string? District { get; init; }
    public string? SubDistrict { get; init; }
    public string? Vintage { get; init; }
    public double? VolumeCl { get; init; }
    public double? Alcohol { get; init; }
    public double? Price { get; init; }
    public double? LitrePrice { get; init; }
    public string? Status { get; init; }
    public string? ProductSelection { get; init; }
    public bool Buyable { get; init; }
    public bool Expired { get; init; }
    public bool AvailableOnline { get; init; }
    public bool AvailableInStores { get; init; }
    public string? AvailabilityText { get; init; }
    public bool Sustainable { get; init; }
    public string? Url { get; init; }
    public string? ImageUrl { get; init; }
}

/// <summary>Full product details from the Vinmonopolet product page.</summary>
public sealed record WineDetails
{
    public required string Code { get; init; }
    /// <summary>Vintage from the product page; listings often omit it from the name.</summary>
    public string? Vintage { get; init; }
    public double? LitrePrice { get; init; }
    public string? Producer { get; init; }
    public string? Wholesaler { get; init; }
    public string? Distributor { get; init; }
    public string? Colour { get; init; }
    public string? Smell { get; init; }
    public string? Taste { get; init; }
    public string? Method { get; init; }
    public string? Summary { get; init; }
    public string? StoragePotential { get; init; }
    public string? StyleName { get; init; }
    public string? StyleDescription { get; init; }
    public string? Cork { get; init; }
    public string? PackageType { get; init; }
    public string? Allergens { get; init; }
    public string? Sugar { get; init; }
    public string? Acid { get; init; }
    public int? Fullness { get; init; }
    public int? Freshness { get; init; }
    public int? Tannins { get; init; }
    public int? Sweetness { get; init; }
    public int? Bitterness { get; init; }
    public bool Eco { get; init; }
    public bool Vegan { get; init; }
    public bool Biodynamic { get; init; }
    public bool FairTrade { get; init; }
    public bool Kosher { get; init; }
    public bool Gluten { get; init; }
    public bool EnvironmentalPackaging { get; init; }
    public string? ImageUrl { get; init; }
    public IReadOnlyList<Grape> Grapes { get; init; } = [];
    public IReadOnlyList<FoodPairing> FoodPairings { get; init; } = [];
    public string? RawJson { get; init; }
}

public sealed record Grape(string Name, double? Percent);

public sealed record FoodPairing(string Code, string Name);

public sealed record Store
{
    public required string StoreId { get; init; }
    public required string Name { get; init; }
    public string? Address { get; init; }
    public string? Town { get; init; }
    public string? PostalCode { get; init; }
    public string? Phone { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

public sealed record StoreStock(Store Store, int StockLevel, string? Distance, DateTimeOffset ObservedAt);

public sealed record PricePoint(double Price, double? LitrePrice, DateTimeOffset ObservedAt);

/// <summary>A paged listing response.</summary>
public sealed record ListingPage(IReadOnlyList<WineListing> Wines, int CurrentPage, int TotalPages, int TotalResults);
