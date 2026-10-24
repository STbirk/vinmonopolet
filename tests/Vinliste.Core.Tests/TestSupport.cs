using Vinliste.Core.Data;
using Vinliste.Core.Http;
using Vinliste.Core.Models;

namespace Vinliste.Core.Tests;

internal static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>A database in a temp file, deleted on dispose.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vinliste-tests", Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Database = new VinlisteDatabase(Path.Combine(_directory, "test.db"));
        Repository = new WineRepository(Database);
    }

    public VinlisteDatabase Database { get; }

    public WineRepository Repository { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Answers requests from a URL predicate table and records what was asked.</summary>
internal sealed class FakeFetcher : IHttpFetcher
{
    private readonly List<(Func<string, bool> Match, Func<string, FetchResponse> Respond)> _routes = [];

    public List<string> Requests { get; } = [];

    public FakeFetcher On(Func<string, bool> match, Func<string, FetchResponse> respond)
    {
        _routes.Add((match, respond));
        return this;
    }

    public FakeFetcher On(string contains, string body, int status = 200) =>
        On(url => url.Contains(contains, StringComparison.Ordinal), _ => new FetchResponse(status, body));

    public Task<FetchResponse> GetAsync(string url, string accept, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(url);
        foreach (var (match, respond) in _routes)
        {
            if (match(url))
            {
                return Task.FromResult(respond(url));
            }
        }
        return Task.FromResult(new FetchResponse(404, "not found"));
    }
}

internal static class Wines
{
    public static WineListing Listing(string code, string name = "Test Wine", double? price = 200, string category = "rødvin") => new()
    {
        Code = code,
        Name = name,
        CategoryCode = category,
        Category = category,
        Country = "Italia",
        District = "Toscana",
        Price = price,
        VolumeCl = 75,
        LitrePrice = price is null ? null : Math.Round(price.Value / 0.75, 2),
        Buyable = true,
        AvailableOnline = true,
        Url = $"https://www.vinmonopolet.no/p/{code}",
    };
}
