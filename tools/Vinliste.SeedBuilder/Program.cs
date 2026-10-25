using System.Diagnostics;
using Vinliste.Core.Data;
using Vinliste.Core.Http;
using Vinliste.Core.Sync;
using Vinliste.Core.Vinmonopolet;

// Builds the starter catalogue shipped with the app (src/Vinliste.App/Seed/vinliste-seed.db.gz).
//
//   dotnet run --project tools/Vinliste.SeedBuilder -- [output.db.gz] [--details N] [--work DIR]
//
// Runs a full catalogue update into a fresh database (about 1,150 listing pages, 20–40
// minutes), optionally fetches product details for N wines, then compacts and gzips it.
// Vinmonopolet rate-limits bursts; a failed run waits and resumes from its saved cursor.
// Pass --work with the folder of an interrupted build to continue it.

Console.OutputEncoding = System.Text.Encoding.UTF8;
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var output = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !args[i - 1].StartsWith("--", StringComparison.Ordinal)))
    .FirstOrDefault() ?? Path.Combine("src", "Vinliste.App", "Seed", "vinliste-seed.db.gz");
var detailsCount = Option("--details") is { } details ? int.Parse(details) : 0;

var workDirectory = Option("--work") ?? Path.Combine(Path.GetTempPath(), "vinliste-seed-" + Guid.NewGuid().ToString("N"));
Console.WriteLine($"Working database: {workDirectory}");
var database = new VinlisteDatabase(Path.Combine(workDirectory, "vinliste.db"));
var repository = new WineRepository(database);
using var http = new HttpClientFetcher();
// Slower than the app's interactive pace: Vinmonopolet rate-limits long bursts.
var client = new VinmonopoletClient(http, TimeSpan.FromSeconds(1));
var clock = Stopwatch.StartNew();

var progress = new Progress<SyncProgress>(p =>
{
    if (p.Stage != "Updating" || p.Done % 25 == 0)
    {
        Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {p.Message}");
    }
});

var catalog = new CatalogSyncService(repository, client);
CatalogSyncResult result;
var attempt = 0;
while (true)
{
    result = await catalog.RunAsync(null, progress, CancellationToken.None);
    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] Catalogue: {result.Status}, {result.ProductsSeen:N0} wines seen this attempt.");
    if (result.Status == SyncRunStatus.Completed)
    {
        break;
    }
    if (++attempt >= 15)
    {
        Console.Error.WriteLine($"Catalogue update did not complete: {result.Error}");
        Console.Error.WriteLine($"Continue later with: --work \"{workDirectory}\"");
        return 1;
    }
    // Rate limited (429) or a network error: wait, then resume from the saved page.
    var wait = TimeSpan.FromMinutes(Math.Min(10, 2 * attempt));
    Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {result.Error?.Split('\n')[0]} — resuming in {wait.TotalMinutes:0} min (attempt {attempt + 1}).");
    await Task.Delay(wait);
}

if (detailsCount > 0)
{
    var saved = await new DetailsSyncService(repository, client).RunAsync(null, detailsCount, progress, CancellationToken.None);
    Console.WriteLine($"Details: {saved:N0} wines.");
}

repository.SetSetting(SeedDatabase.SettingKey, DateTimeOffset.UtcNow.ToString("O"));
SeedDatabase.Export(database, output);
var stats = repository.GetStats();
Console.WriteLine($"Wrote {output}: {stats.Active:N0} wines, {stats.WithDetails:N0} with details, " +
                  $"{new FileInfo(output).Length / 1024.0 / 1024.0:F1} MB, in {clock.Elapsed:hh\\:mm\\:ss}.");
// Pooled connections keep the work database open; release them before cleaning up.
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
try
{
    Directory.Delete(workDirectory, recursive: true);
}
catch (IOException ex)
{
    Console.WriteLine($"Could not remove {workDirectory}: {ex.Message}");
}
return 0;
