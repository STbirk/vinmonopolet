# Vinliste

A Windows app for exploring Vinmonopolet's wine range with ratings from the web. It keeps
a local catalogue of every wine Vinmonopolet sells (red, white, rosé, sparkling,
semi-sparkling, fortified, aromatised and fruit wine), updates it with one click, and
collects scores from Vivino, CellarTracker, Falstaff and Wine Enthusiast.

![Vinliste](src/Vinliste.App/Assets/vinliste-256.png)

## What it does

- **Ready from the first start.** The app ships with a starter catalogue of Vinmonopolet's
  whole wine range, installed on first start, so there is no initial scan to wait for.
- **Keeps itself up to date.** While the app is open it reads Vinmonopolet's list slowly in the
  background (one page a minute by default, a full pass in about 19 hours of app time) and
  saves progress after every page. **Update now** does a full pass at full speed (15–25
  minutes) instead. Both record new wines, price changes (with history) and wines that
  disappear.
- **Wine details.** Tasting notes (colour, smell, taste), fullness/freshness/tannins/
  sweetness/bitterness, grapes, food pairings, storage potential, method, closure,
  packaging, producer, importer and certifications (organic, biodynamic, vegan …).
  Fetched when you open a wine, or in bulk with **Get details**.
- **Stock per store.** **Check stock** lists every store that has the wine, nearest first,
  with your favourite store on top.
- **Ratings in the background.** While the app is open it also looks up ratings for one wine
  at a time (every five minutes by default): favourites first, then wines you have opened, then
  the core range, re-checking each wine after 30 days. Scores appear in the list as they arrive.
- **Activity panel.** The **Activity** button in the status bar opens a small terminal that shows
  what the background work is doing, line by line: catalogue pages, each rating lookup and its
  result, and (optionally) every web request.
- **Ratings on demand.** **Find ratings** (one wine) or **Rate selected / Rate listed** (many) search
  each site and match carefully: producer, vintage, colour and wine-specific words such as
  the cuvée name, Riserva or a second-wine name must agree. Vivino scores are per vintage
  where available. **Best rating** is the average of the matched scores on a 0–100 scale.
  A wine rated once gets the same scores for its other bottle sizes, and a match found for one
  vintage is reused to look up the next vintage directly.
- **Browse.** Search by name, producer, grape, district or item number; filter by wine
  type, country, price and minimum score; sort by score, value for money, price, litre
  price, vintage, newest arrivals or biggest price drop. Favourites, notes, **New arrivals**
  and **Price drops** views. Export the list to CSV (Excel) or JSON.
- Light and dark themes.

## Requirements

- Windows 10 (1809) or later
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (included if
  you publish self-contained, see below)
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 11; used to read rating sites
  that block plain downloads)

## Build and run

With the .NET 8 SDK installed:

```powershell
dotnet build Vinliste.sln
dotnet test Vinliste.sln
dotnet run --project src/Vinliste.App
```

To make a single `Vinliste.exe` that runs without installing .NET:

```powershell
dotnet publish src/Vinliste.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

### Refreshing the starter catalogue

The bundled catalogue is `src/Vinliste.App/Seed/vinliste-seed.db.gz`. To rebuild it from the
live site (a full fast update, 15–25 minutes; `--details N` also fetches tasting notes for N
wines):

```powershell
dotnet run --project tools/Vinliste.SeedBuilder -- src/Vinliste.App/Seed/vinliste-seed.db.gz
```

## Where data is stored

Everything lives in `%LOCALAPPDATA%\Vinliste`: the database (`vinliste.db`), settings
(`settings.json`) and the WebView2 browser cache. **Settings → Open data folder** opens it.
Set the environment variable `VINLISTE_DATA_DIR` to use another folder (handy for testing).

No API key is needed: the app reads Vinmonopolet's public website data, which includes
more detail than the open API (tasting notes and stock per store).

## Project layout

```
src/Vinliste.Core/          Models, SQLite database, Vinmonopolet client and sync, rating engine
src/Vinliste.App/           WPF app (MVVM): views, view models, themes, WebView2 fetcher, starter catalogue
tools/Vinliste.SeedBuilder/ Builds the starter catalogue from the live site
tests/Vinliste.Core.Tests/  xUnit tests (no network access needed)
docs/design.md              Design notes: data sources, schema, update strategy, matching rules
```

## Notes

Rating lookups are best effort and go through web search, so they are polite and slow
(roughly 15–40 seconds per wine across all four sites). Results are cached: a matched
score is reused for 90 days, "not found" for 30 days, and failures are retried the next day.
