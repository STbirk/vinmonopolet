# Vinliste for Windows — design

Vinliste is a native Windows desktop app (C# / .NET 8 / WPF) that keeps a local
SQLite catalogue of Vinmonopolet's wines, keeps it up to date with one click,
and collects critic and community ratings for each wine.

## Solution layout

```
Vinliste.sln
src/Vinliste.Core/        net8.0 class library: models, database, Vinmonopolet sync, ratings
src/Vinliste.App/         net8.0-windows WPF app (MVVM, CommunityToolkit.Mvvm)
tests/Vinliste.Core.Tests xUnit tests for Core (no live network)
```

Core has no UI dependency. Everything that talks to the network goes through
`IHttpFetcher`, so tests use canned responses and the app can swap transports.

## Data sources (Vinmonopolet)

All endpoints below are public JSON on www.vinmonopolet.no and answer a plain
HTTP client with a browser User-Agent. Visit `https://www.vinmonopolet.no/`
first to obtain cookies.

| Purpose | Endpoint | Notes |
|---|---|---|
| Catalogue listing | `GET /vmpws/v2/vmp/products/search?searchType=product&q=:relevance:mainCategory:{cat}&pageSize=24&currentPage={n}` | Page size is capped at 24 server-side; `pagination.totalPages` is reliable. 429 → honour `Retry-After`. |
| Product details | `GET /vmpws/v3/vmp/products/{code}?fields=FULL` | Taste, smell, colour, method, grapes (`content.ingredients`), characteristics (fullness/freshness/tannins/sweetness/bitterness), food pairing (`content.isGoodFor`), storage potential, style, producer, wholesaler, distributor, cork, package type, allergens, eco/vegan/biodynamic/natural flags, images, litre price. |
| Stock per store | `GET /vmpws/v2/vmp/products/{code}/stock?pageSize=10&currentPage={n}&fields=BASIC&latitude={lat}&longitude={lon}` | Store id, name, address, geo point, `stockInfo.stockLevel`. Capped at 10 stores per page; the app pages through `pagination.totalPages`. |
| Products in a store | search with `:availableInStores:{storeId}` added to `q` | Presence only, no counts. |

Wine categories synced (search `mainCategory` codes): `rødvin`, `hvitvin`,
`rosévin`, `musserende_vin`, `perlende_vin`, `sterkvin`, `aromatisert_vin`,
`fruktvin`. Roughly 27,000 products, about 1,150 listing pages.

The official API (`apis.vinmonopolet.no`, `Ocp-Apim-Subscription-Key`) is not
used: the public endpoints above need no key and carry more (tasting notes, stock
per store), so an API key setting would only add configuration without adding data.

### Update strategy

- **Starter catalogue.** The app embeds a gzipped database built by
  `tools/Vinliste.SeedBuilder` (a full fast catalogue run, compacted). On start,
  `SeedDatabase.InstallIfNeeded` writes it to the data folder when there is no
  database or the database has no wines; an existing catalogue is never touched.
  The seed includes its completed `sync_runs` row, so "New arrivals" counts wines
  first seen after the seed and delisting has a previous run to compare with.
- **Background updates** (`BackgroundCatalogUpdater`, on by default, Settings →
  Keeping up to date): while the app is open, the normal resumable catalogue run
  goes on with a pause before every listing page (default 60 s; ~1,150 pages ≈ 19
  hours of app time per pass), and a new pass starts when one completes. Unfinished
  passes resume for up to 7 days. A failed pass waits 10 minutes. "Update now"
  stops the background loop, continues the same run at full speed, then restarts
  the loop.

- **Catalogue update** (one click, background, resumable, ~15–25 min for a full
  run, progress + cancel): page through every category listing (sorted by name
  for stable paging) and upsert products. Record a price-history row whenever
  the price changes and first/last-seen timestamps so "new wines" and "price
  drops" can be shown. An unfinished run resumes from its page cursor if it is
  less than 7 days old; older runs are closed and a new one starts.
- **Delisting** is deliberately cautious, because offset paging can skip a wine
  when the catalogue shifts during a run, and the site sometimes returns an empty
  page: a wine is delisted only when it was missing from this run *and* the
  previous completed one, only in categories this run paged to the end without
  an empty page, and only when the run saw at least 80% of the category's known
  wines. A delisted wine that reappears is restored.
- **Details** are fetched when a wine is opened (if missing or older than 30
  days), with **Get details** for the listed wines (up to 1,000, missing/stale
  first), and before bulk rating (ratings match better with the producer).
  *Not built:* an automatic background details job after each catalogue update.
- **Stock** is fetched on demand for the selected wine (every store, nearest
  first, the favourite store on top); results are stored with a timestamp.
  *Not built:* automatic stock checks and an "in stock at my store" list filter
  (the query supports it, but stored stock only covers wines you have checked).
- Requests are polite: one connection, small delay (default 300 ms), retry
  with backoff on 429/5xx.

## Database (SQLite, `%LOCALAPPDATA%\Vinliste\vinliste.db`)

Schema versioned with `PRAGMA user_version` and forward-only migrations.

- `wines` — `code` PK, name, category_code, category, sub_category, country,
  district, sub_district, producer, wholesaler, distributor, vintage, volume_cl,
  alcohol, price, litre_price, previous_price, price_changed_at, status,
  product_selection (assortment), buyable, expired, available_online,
  available_in_stores, availability_text, sustainable, eco, vegan, biodynamic,
  url, image_url, first_seen_at, last_seen_at, delisted_at, details_updated_at,
  ratings_updated_at, best_score and rating_sources (kept in step with
  `ratings` for fast sorting), is_favorite, note, search_text and
  details_search_text (lower-cased, accent-folded text for search, since SQLite
  LIKE only folds ASCII).
- `wine_details` — `code` PK/FK, colour, smell, taste, method, summary,
  storage_potential, style_name, style_description, cork, package_type,
  allergens, sugar, acid, fullness, freshness, tannins, sweetness, bitterness,
  fair_trade, kosher, gluten, environmental_packaging, raw_json.
- `wine_grapes` — code, grape, percent.
- `wine_food_pairings` — code, food_code, food_name.
- `price_history` — code, price, litre_price, observed_at.
- `stores` — store_id PK, name, address, town, postal_code, phone, latitude,
  longitude.
- `store_stock` — code, store_id, stock_level, observed_at (latest per pair).
- `ratings` — code, source, status, score (native scale), score_100
  (normalised 0–100), rating_count, matched_name, matched_vintage, url,
  match_confidence, search_query, error, fetched_at. PK (code, source).
- `sync_runs` — id, kind, started_at, finished_at, status, pages_done,
  pages_total, products_seen, products_new, price_changes, error, cursor_json
  (resume point).
- `settings` — key/value, reserved for data-related state.

The UI reads through `WineQuery` (text search, category, country, price range,
score range, in-stock-only, favourites, sort) that compiles to SQL — the grid
never loads all rows into memory unfiltered.

## Ratings

Sources: Vivino (community, 0–5 → ×20), CellarTracker (community, 0–100),
Falstaff (critic, 0–100), Wine Enthusiast (critic, 0–100). Stored per source;
"Best rating" in the UI is the average of matched `score_100` values plus the
count of sources.

Improvements over the Python version:

- **Browser-grade fetching.** Falstaff and Wine Enthusiast reject plain HTTP
  clients (TLS fingerprinting, JavaScript challenges). The app fetches their
  pages through a hidden WebView2 (Edge) instance behind `IHttpFetcher`; plain
  `HttpClient` over HTTP/2 is used where it works (Vinmonopolet, Yahoo search,
  Vivino). CellarTracker pages sit behind a challenge too, but its search
  snippets carry the community average, so its pages are never opened.
- **Search.** Yahoo, two phrasings per source (site-restricted first). Bing was
  dropped: it returned no on-domain results to automated requests. A failed
  search is recorded as an error (retried next day), never as "no match".
- **Better queries.** The Vinmonopolet name (it nearly always starts with the
  brand) with abbreviations expanded (Ch., Dom., Ten., Bod., Cant., Ris., Est. …),
  bottle sizes ("5l", "150cl", "Magnum") and the vintage removed. The product
  page's producer is often the owner (André Lurton for Château La Louvière), so
  it is only added when the name has no identifying word of its own.
- **Better matching** (`WineMatcher`): every producer word that appears in the
  name must appear in the candidate; vintages must agree when both are known
  (NV counts as a vintage); colours must agree (Vinmonopolet category vs
  rouge/blanc/rosé/rosso/bianco …; a sparkling wine never matches its house's
  rosé); numbered cuvées (Krug 171ème, Roederer Collection 244) must agree; a
  Riserva/Superiore/Crianza or Extra Brut/Brut Nature qualifier on one side only
  costs 25; second-wine words (Moulin, Chapelle, Pagodes, Connétable …) cost 30;
  every missing wine-specific word and every unexplained extra word costs 20, so
  a single cru or cuvée word (Gevrey-Chambertin vs … Les Cazetiers) drops a
  perfect name below the default threshold of 82. Extra words are explained by
  the wine's appellation, Vinmonopolet's producer, parenthesised text and a small
  list of English region names and sample notes. Classification wording (Grand
  Cru, 1er/Premier Cru, Brut) is ignored because sites write it inconsistently.
  Ties go to the wine's own vintage, then a scored candidate, then more ratings.
  This is deliberately conservative: a sibling that differs by one vineyard word
  (Bussia vs Bussia Munie) is rejected rather than risk a wrong score.
- **Caching and refresh.** Results are cached in the `ratings` table with
  `fetched_at`; a match is refreshed after 90 days, "no match" after 30 days,
  errors/blocked after 1 day. A transient error never overwrites a stored match.
- **Background ratings** (`BackgroundRatingUpdater`, on by default): one wine per
  interval (default 5 minutes) while the app is open. `WineRepository.NextWineForRating`
  picks available wines (or favourites) never checked or checked more than 30 days
  ago: favourites, then wines with details (opened by the user), then Basisutvalget,
  then Tilleggsutvalget. Details are fetched first when missing. Each attempt stamps
  `ratings_updated_at` (`TouchRatings`) so a wine whose lookup saved nothing is not
  picked again at once. When every source fails it waits 15 minutes.
- **Throughput.** Sources run in parallel per wine, two wines at a time, behind
  one shared, polite search-engine gate. Bulk rating runs in the background with
  progress and cancel and can target the selected rows or the listed wines
  ("missing only").

## Configuration

`%LOCALAPPDATA%\Vinliste\settings.json` (user-editable, never committed): theme,
favourite store, location for stock sorting, match strictness, enabled rating
sources. `VINLISTE_DATA_DIR` moves the whole data folder (database, settings,
WebView2 cache), which is how tests and verification runs avoid touching real data.

## UI (WPF)

Wine-themed, modern, light and dark:

- Palette: deep bordeaux (#5B1A2A) primary, wine red accent (#8E2C3F), rosé
  tint (#F3E3E6) surfaces, cork/oak neutrals (#C8A97E), parchment background
  (#FBF7F2) in light mode, near-black aubergine (#1B1116) in dark mode.
- Typography: Segoe UI Variable; serif display font for headings (Georgia).
- Layout: left navigation rail (All wines, Favourites, New arrivals, Price
  drops, Settings, plus a catalogue card with the update button); main area
  with a search bar + filter chips, virtualised wine grid (name, producer,
  vintage, type colour dot, origin, price and change, litre price, best score
  badge, availability), and a details pane with image, ratings per source with
  links, tasting notes, characteristics bars, grapes, food pairing, facts,
  price history line, stock per store and a personal note.
- Activity panel: a collapsible, always-dark "terminal" above the status bar fed by
  `ActivityLog` (Core, thread-safe ring buffer of 1,000 entries). The background loops
  write their steps and results; `LoggingFetcher` wraps every HTTP transport and logs
  each request as a detail line (shown when "Web requests" is ticked); foreground job
  messages are logged too. The status bar shows the latest line while it is closed.
- The list query runs off the UI thread; only the newest request is applied.
  At most 3,000 rows are shown; the footer asks to narrow the search beyond that.
- Status bar with background job progress (catalogue update, details, ratings)
  and cancel.
- Copy rows, open on vinmonopolet.no, export filtered view to CSV/JSON.

## Testing

- Core: unit tests for parsing (canned JSON/HTML fixtures from the live
  sites), matching, query building, database migrations and queries, and sync
  logic with a fake fetcher.
- App: view-model tests where practical; manual/automated smoke launch.
- Each implementation step is reviewed by a separate verification agent that
  builds, runs the tests and checks behaviour against this document.
