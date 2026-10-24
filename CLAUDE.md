# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Vinliste is a Windows desktop app (C# / .NET 8 / WPF) that keeps a local SQLite catalogue of
Vinmonopolet's wines and collects ratings from Vivino, CellarTracker, Falstaff and Wine Enthusiast.
`docs/design.md` is the authoritative design note (data sources, schema, update and matching rules);
keep it in step with behaviour changes. The `master` branch still holds the earlier Python version.

## Commands

The SDK lives at `C:\Program Files\dotnet\dotnet.exe` (it may not be on PATH in every shell).

```powershell
dotnet build Vinliste.sln
dotnet test Vinliste.sln
dotnet test tests/Vinliste.Core.Tests --filter "FullyQualifiedName~WineMatcherTests"   # one class
dotnet test tests/Vinliste.Core.Tests --filter "DisplayName~Riserva"                   # one test by name
dotnet run --project src/Vinliste.App
dotnet publish src/Vinliste.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

When another process (or a running Vinliste.exe) holds the build output, build with a separate
`--artifacts-path <dir>` to avoid file locks. Tests need no network: Vinmonopolet responses are
canned JSON in `tests/Vinliste.Core.Tests/Fixtures/`.

## Architecture

- `src/Vinliste.Core` (net8.0, no UI): models, SQLite (`Data/`), Vinmonopolet client and parser
  (`Vinmonopolet/`), catalogue/details/stock services (`Sync/`), rating engine (`Ratings/`), settings
  (`Config/`).
- `src/Vinliste.App` (WPF, MVVM with CommunityToolkit.Mvvm): `AppServices` wires everything once at
  startup; view models in `ViewModels/`, palettes and control styles in `Themes/`.
- `tests/Vinliste.Core.Tests`: xUnit tests for Core only. `TestSupport.cs` has `TempDatabase` and
  `FakeFetcher` (URL-predicate routing) for network-free tests.

### Network goes through `IHttpFetcher`
All HTTP in Core uses `Http/IHttpFetcher`. Production transports:
- `HttpClientFetcher` (HTTP/2, cookies, retry on 0/429/5xx) for Vinmonopolet, Yahoo search and Vivino.
  Yahoo returns errors to HTTP/1.1 clients, so keep HTTP/2.
- `WebView2Fetcher` (App) routes Falstaff and Wine Enthusiast page loads through a hidden Edge window,
  because those sites reject plain clients by TLS fingerprint and JS challenges.
- A separate low-retry `HttpClientFetcher` for search (`RatingFetchers.Search`), so search failures
  don't stall every source.

### Vinmonopolet data
Public endpoints on www.vinmonopolet.no, no API key (see the table in `docs/design.md`). The search
endpoint caps pages at 24 and stock at 10 stores per page, whatever `pageSize` asks for. Listings are
sorted `name-asc` for stable offset paging. `VinmonopoletClient` warms up cookies from the home page
and serialises requests with a delay.

### Database
`VinlisteDatabase` owns the file and forward-only migrations tracked by `PRAGMA user_version` (append a
new migration string; never edit a shipped one). Private cache + WAL; each repository call opens its own
connection, so `WineRepository` is safe to call from background threads. `wines.best_score` and
`rating_sources` are denormalised from `ratings` inside `SaveRating`. Search uses `search_text` /
`details_search_text` columns (accent-folded, lower-cased in C#) because SQLite `LIKE` only folds ASCII.
Timestamps are stored as fixed-format UTC ISO strings and compared as strings.

### Catalogue sync (`CatalogSyncService`)
Counts pages per category, then pages through them, persisting a JSON cursor after every page so a
cancelled or failed run resumes (only if under 6 h old). Delisting is deliberately cautious: only wines
missing from this run and the previous completed one, only in categories paged to the end without an
empty page, and only if at least 80% of the category was seen. One run at a time per process.

### Rating engine (`Ratings/`)
`WebSearchRatingSource` runs site-restricted Yahoo searches (a second phrasing only if the first gives
no scored match), reads scores from snippets where sites put them (CellarTracker, Falstaff, WE) and opens
at most 3 result pages otherwise. `RatingService` runs sources in parallel per wine, caches results by
status (matched 90 d, no match 30 d, error/blocked 1 d) and never lets a transient error overwrite a
stored match.

`WineMatcher` decides "same wine". It is intentionally strict, and `RatingTests.cs` pins real-world
cases (second wines, Riserva vs normale, cru/cuvée siblings, owner-as-producer, colour, NV). Key rules:
all producer words that appear in the Vinmonopolet name must appear; vintage, colour and cuvée numbers
must agree; each missing or unexplained extra distinctive word costs 20, so a one-word sibling falls
below the default threshold of 82. Word lists (stopwords, qualifiers, second-wine markers, noise words)
live in `WineText.cs`. When changing scoring, run the matcher tests and check both directions: new false
matches and lost true matches.

### App
`MainViewModel.RefreshAsync` queries off the UI thread and applies only the newest result.
`_swappingRows` guards selection while the grid's rows are replaced, so the detail pane and any running
lookup survive refreshes; `ReplaceRow` updates one row in place. Long work goes through `JobViewModel`
(one job at a time, progress and cancel). The theme swaps the first merged dictionary in `App.xaml`
(keep the palette first); converters that resolve brushes are re-evaluated on `ThemeManager.ThemeChanged`.

## Testing the running app

- `VINLISTE_DATA_DIR` moves the whole data folder (database, settings, WebView2 cache). Use it for any
  manual or automated run so the user's real data in `%LOCALAPPDATA%\Vinliste` is untouched.
- Screen capture of the WPF window can come back blank or black (locked or remote desktop). Instead,
  use the built-in render hook in `MainWindow.xaml.cs`: `VINLISTE_SNAPSHOT=<png>` renders the window
  after `VINLISTE_SNAPSHOT_DELAY` seconds; `VINLISTE_SNAPSHOT_SEARCH`, `VINLISTE_SNAPSHOT_SELECT=first`,
  `VINLISTE_SNAPSHOT_PAGE=<AllWines|Favorites|NewArrivals|PriceDrops|Settings>`,
  `VINLISTE_SNAPSHOT_RATE=1` and `VINLISTE_SNAPSHOT_EXIT=1` drive it. The theme comes from
  `settings.json` in the data folder.
