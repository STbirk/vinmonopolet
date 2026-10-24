namespace Vinliste.Core.Vinmonopolet;

public sealed record WineCategory(string Code, string Name);

public static class WineCategories
{
    /// <summary>The search endpoint caps page size at 24 regardless of what is asked for.</summary>
    public const int PageSize = 24;

    public static readonly IReadOnlyList<WineCategory> All =
    [
        new("rødvin", "Red"),
        new("hvitvin", "White"),
        new("rosévin", "Rosé"),
        new("musserende_vin", "Sparkling"),
        new("perlende_vin", "Semi-sparkling"),
        new("sterkvin", "Fortified"),
        new("aromatisert_vin", "Aromatised"),
        new("fruktvin", "Fruit wine"),
    ];
}
