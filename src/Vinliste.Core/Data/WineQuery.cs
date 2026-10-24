namespace Vinliste.Core.Data;

public enum WineSort
{
    Name,
    Price,
    LitrePrice,
    BestScore,
    Vintage,
    Country,
    FirstSeen,
    PriceChange,
    ValueForMoney,
}

/// <summary>Filter for the wine list; compiled to SQL so the grid never loads the whole catalogue.</summary>
public sealed record WineQuery
{
    public string? Text { get; init; }
    public IReadOnlyCollection<string>? CategoryCodes { get; init; }
    public string? Country { get; init; }
    public double? MinPrice { get; init; }
    public double? MaxPrice { get; init; }
    public double? MinScore { get; init; }
    public bool AvailableOnly { get; init; }
    public bool FavoritesOnly { get; init; }
    public bool IncludeDelisted { get; init; }
    public bool PriceDroppedOnly { get; init; }
    public int? NewWithinDays { get; init; }
    /// <summary>Only wines first seen after this moment (takes precedence over <see cref="NewWithinDays"/>).</summary>
    public DateTimeOffset? NewSince { get; init; }
    public string? InStockAtStoreId { get; init; }
    public IReadOnlyCollection<string>? Codes { get; init; }
    public WineSort Sort { get; init; } = WineSort.Name;
    public bool Descending { get; init; }
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}

/// <summary>One row in the wine list.</summary>
public sealed record WineRow
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? CategoryCode { get; init; }
    public string? Category { get; init; }
    public string? Country { get; init; }
    public string? District { get; init; }
    public string? SubDistrict { get; init; }
    public string? Producer { get; init; }
    public string? Vintage { get; init; }
    public double? VolumeCl { get; init; }
    public double? Alcohol { get; init; }
    public double? Price { get; init; }
    public double? LitrePrice { get; init; }
    public double? PreviousPrice { get; init; }
    public string? ProductSelection { get; init; }
    public bool Buyable { get; init; }
    public bool AvailableOnline { get; init; }
    public bool AvailableInStores { get; init; }
    public string? AvailabilityText { get; init; }
    public string? Url { get; init; }
    public string? ImageUrl { get; init; }
    public double? BestScore { get; init; }
    public int RatingSources { get; init; }
    public bool IsFavorite { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset? DelistedAt { get; init; }
    public DateTimeOffset? DetailsUpdatedAt { get; init; }
    public DateTimeOffset? RatingsUpdatedAt { get; init; }

    public double? PriceChange => Price is not null && PreviousPrice is not null ? Price - PreviousPrice : null;
}

public sealed record CatalogStats(int Total, int Active, int WithDetails, int WithRatings, DateTimeOffset? LastSync);
