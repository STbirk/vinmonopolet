using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Vinliste.Core.Models;

namespace Vinliste.Core.Data;

public sealed record UpsertResult(int Seen, int New, int PriceChanges);

public sealed record SyncRun
{
    public long Id { get; init; }
    public required string Kind { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public required string Status { get; init; }
    public int PagesDone { get; init; }
    public int PagesTotal { get; init; }
    public int ProductsSeen { get; init; }
    public int ProductsNew { get; init; }
    public int PriceChanges { get; init; }
    public string? Error { get; init; }
    public string? CursorJson { get; init; }
}

public static class SyncRunStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>All reads and writes of the wine catalogue.</summary>
public sealed class WineRepository(VinlisteDatabase database)
{
    public VinlisteDatabase Database => database;

    // ---------- listings ----------

    public UpsertResult UpsertListings(IEnumerable<WineListing> listings, DateTimeOffset seenAt)
    {
        var seen = 0;
        var added = 0;
        var priceChanges = 0;
        var now = Iso(seenAt);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using var select = Command(connection, transaction, "SELECT price FROM wines WHERE code = $code");
        var selectCode = select.Parameters.Add("$code", SqliteType.Text);

        using var insert = Command(connection, transaction, """
            INSERT INTO wines (code, name, category_code, category, sub_category, country, district, sub_district,
                vintage, volume_cl, alcohol, price, litre_price, status, product_selection, buyable, expired,
                available_online, available_in_stores, availability_text, sustainable, url, image_url,
                first_seen_at, last_seen_at, search_text, cuvee_key)
            VALUES ($code, $name, $category_code, $category, $sub_category, $country, $district, $sub_district,
                $vintage, $volume_cl, $alcohol, $price, $litre_price, $status, $product_selection, $buyable, $expired,
                $available_online, $available_in_stores, $availability_text, $sustainable, $url, $image_url,
                $now, $now, $search_text, $cuvee_key)
            """);

        // Vintage, volume and image from the product page are better than the listing's;
        // only fill them from the listing when they are still empty.
        using var update = Command(connection, transaction, """
            UPDATE wines SET
                name = $name, category_code = $category_code, category = $category, sub_category = $sub_category,
                country = $country, district = $district, sub_district = $sub_district,
                vintage = CASE WHEN details_updated_at IS NULL THEN COALESCE($vintage, vintage) ELSE COALESCE(vintage, $vintage) END, volume_cl = COALESCE($volume_cl, volume_cl),
                alcohol = COALESCE($alcohol, alcohol),
                previous_price = CASE WHEN $price_changed THEN price ELSE previous_price END,
                price_changed_at = CASE WHEN $price_changed THEN $now ELSE price_changed_at END,
                price = COALESCE($price, price),
                -- The product page's litre price accounts for deposit; keep it once details exist.
                litre_price = CASE WHEN details_updated_at IS NULL OR $price_changed
                                   THEN COALESCE($litre_price, litre_price) ELSE litre_price END,
                search_text = $search_text, cuvee_key = $cuvee_key,
                status = $status, product_selection = $product_selection, buyable = $buyable, expired = $expired,
                available_online = $available_online, available_in_stores = $available_in_stores,
                availability_text = $availability_text, sustainable = $sustainable,
                url = COALESCE(url, $url), image_url = COALESCE(image_url, $image_url),
                last_seen_at = $now, delisted_at = NULL
            WHERE code = $code
            """);

        using var history = Command(connection, transaction,
            "INSERT INTO price_history (code, price, litre_price, observed_at) VALUES ($code, $price, $litre_price, $now)");

        foreach (var wine in listings)
        {
            seen++;
            selectCode.Value = wine.Code;
            var existing = select.ExecuteScalar();
            var isNew = existing is null;
            double? oldPrice = existing is null or DBNull ? null : Convert.ToDouble(existing, CultureInfo.InvariantCulture);
            var priceChanged = !isNew && wine.Price is not null && oldPrice is not null && Math.Abs(oldPrice.Value - wine.Price.Value) > 0.005;

            var command = isNew ? insert : update;
            BindListing(command, wine, now);
            if (!isNew)
            {
                command.Parameters.AddWithValue("$price_changed", priceChanged);
            }
            command.ExecuteNonQuery();
            command.Parameters.Clear();

            if (wine.Price is not null && (isNew || priceChanged || oldPrice is null))
            {
                history.Parameters.Clear();
                history.Parameters.AddWithValue("$code", wine.Code);
                history.Parameters.AddWithValue("$price", wine.Price);
                history.Parameters.AddWithValue("$litre_price", Db(wine.LitrePrice));
                history.Parameters.AddWithValue("$now", now);
                history.ExecuteNonQuery();
            }

            if (isNew)
            {
                added++;
            }
            if (priceChanged)
            {
                priceChanges++;
            }
        }

        transaction.Commit();
        return new UpsertResult(seen, added, priceChanges);
    }

    /// <summary>
    /// Marks wines in the given categories not seen since <paramref name="notSeenSince"/> as delisted.
    /// Callers pass the start of the previous completed run, so a wine has to be missing from two
    /// runs in a row: offset paging can skip a wine once when the catalogue shifts mid-run.
    /// </summary>
    public int MarkDelisted(IEnumerable<string> categoryCodes, DateTimeOffset notSeenSince)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        var codes = categoryCodes.ToList();
        var names = codes.Select((_, i) => $"$c{i}").ToList();
        command.CommandText = $"""
            UPDATE wines SET delisted_at = $now
            WHERE delisted_at IS NULL AND last_seen_at < $started AND category_code IN ({string.Join(", ", names)})
            """;
        for (var i = 0; i < codes.Count; i++)
        {
            command.Parameters.AddWithValue(names[i], codes[i]);
        }
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$started", Iso(notSeenSince));
        return command.ExecuteNonQuery();
    }

    /// <summary>Active wines per category, to sanity-check a run before delisting.</summary>
    public int CountActive(string categoryCode)
    {
        using var connection = database.Open();
        return Convert.ToInt32(QueryRows(connection,
            "SELECT COUNT(*) FROM wines WHERE category_code = $c AND delisted_at IS NULL", ("$c", categoryCode)).Single()[0],
            CultureInfo.InvariantCulture);
    }

    // ---------- details ----------

    public void SaveDetails(WineDetails details, DateTimeOffset fetchedAt)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var update = Command(connection, transaction, """
            UPDATE wines SET producer = $producer, wholesaler = $wholesaler, distributor = $distributor,
                vintage = COALESCE($vintage, vintage), litre_price = COALESCE($litre_price, litre_price),
                eco = $eco, vegan = $vegan, biodynamic = $biodynamic,
                image_url = COALESCE($image_url, image_url), details_updated_at = $now,
                details_search_text = $details_search_text
            WHERE code = $code
            """))
        {
            update.Parameters.AddWithValue("$details_search_text",
                SearchText([details.Producer, details.Wholesaler, .. details.Grapes.Select(g => g.Name)]));
            update.Parameters.AddWithValue("$code", details.Code);
            update.Parameters.AddWithValue("$producer", Db(details.Producer));
            update.Parameters.AddWithValue("$wholesaler", Db(details.Wholesaler));
            update.Parameters.AddWithValue("$distributor", Db(details.Distributor));
            update.Parameters.AddWithValue("$vintage", Db(details.Vintage));
            update.Parameters.AddWithValue("$litre_price", Db(details.LitrePrice));
            update.Parameters.AddWithValue("$eco", details.Eco);
            update.Parameters.AddWithValue("$vegan", details.Vegan);
            update.Parameters.AddWithValue("$biodynamic", details.Biodynamic);
            update.Parameters.AddWithValue("$image_url", Db(details.ImageUrl));
            update.Parameters.AddWithValue("$now", Iso(fetchedAt));
            if (update.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException($"Wine {details.Code} is not in the catalogue.");
            }
        }

        using (var upsert = Command(connection, transaction, """
            INSERT OR REPLACE INTO wine_details (code, colour, smell, taste, method, summary, storage_potential,
                style_name, style_description, cork, package_type, allergens, sugar, acid,
                fullness, freshness, tannins, sweetness, bitterness, fair_trade, kosher, gluten,
                environmental_packaging, raw_json)
            VALUES ($code, $colour, $smell, $taste, $method, $summary, $storage_potential,
                $style_name, $style_description, $cork, $package_type, $allergens, $sugar, $acid,
                $fullness, $freshness, $tannins, $sweetness, $bitterness, $fair_trade, $kosher, $gluten,
                $environmental_packaging, $raw_json)
            """))
        {
            upsert.Parameters.AddWithValue("$code", details.Code);
            upsert.Parameters.AddWithValue("$colour", Db(details.Colour));
            upsert.Parameters.AddWithValue("$smell", Db(details.Smell));
            upsert.Parameters.AddWithValue("$taste", Db(details.Taste));
            upsert.Parameters.AddWithValue("$method", Db(details.Method));
            upsert.Parameters.AddWithValue("$summary", Db(details.Summary));
            upsert.Parameters.AddWithValue("$storage_potential", Db(details.StoragePotential));
            upsert.Parameters.AddWithValue("$style_name", Db(details.StyleName));
            upsert.Parameters.AddWithValue("$style_description", Db(details.StyleDescription));
            upsert.Parameters.AddWithValue("$cork", Db(details.Cork));
            upsert.Parameters.AddWithValue("$package_type", Db(details.PackageType));
            upsert.Parameters.AddWithValue("$allergens", Db(details.Allergens));
            upsert.Parameters.AddWithValue("$sugar", Db(details.Sugar));
            upsert.Parameters.AddWithValue("$acid", Db(details.Acid));
            upsert.Parameters.AddWithValue("$fullness", Db(details.Fullness));
            upsert.Parameters.AddWithValue("$freshness", Db(details.Freshness));
            upsert.Parameters.AddWithValue("$tannins", Db(details.Tannins));
            upsert.Parameters.AddWithValue("$sweetness", Db(details.Sweetness));
            upsert.Parameters.AddWithValue("$bitterness", Db(details.Bitterness));
            upsert.Parameters.AddWithValue("$fair_trade", details.FairTrade);
            upsert.Parameters.AddWithValue("$kosher", details.Kosher);
            upsert.Parameters.AddWithValue("$gluten", details.Gluten);
            upsert.Parameters.AddWithValue("$environmental_packaging", details.EnvironmentalPackaging);
            upsert.Parameters.AddWithValue("$raw_json", Db(details.RawJson));
            upsert.ExecuteNonQuery();
        }

        Execute(connection, transaction, "DELETE FROM wine_grapes WHERE code = $code", ("$code", details.Code));
        foreach (var grape in details.Grapes)
        {
            Execute(connection, transaction, "INSERT INTO wine_grapes (code, grape, percent) VALUES ($code, $grape, $percent)",
                ("$code", details.Code), ("$grape", grape.Name), ("$percent", Db(grape.Percent)));
        }
        Execute(connection, transaction, "DELETE FROM wine_food_pairings WHERE code = $code", ("$code", details.Code));
        foreach (var food in details.FoodPairings)
        {
            Execute(connection, transaction, "INSERT INTO wine_food_pairings (code, food_code, food_name) VALUES ($code, $food_code, $food_name)",
                ("$code", details.Code), ("$food_code", food.Code), ("$food_name", food.Name));
        }

        transaction.Commit();
    }

    /// <summary>
    /// Records that the product page could not be read (404, unparseable), so the
    /// background details job moves on instead of retrying it first every time.
    /// </summary>
    public void MarkDetailsUnavailable(string code, DateTimeOffset at) =>
        ExecuteStandalone("UPDATE wines SET details_updated_at = $now WHERE code = $code", ("$code", code), ("$now", Iso(at)));

    public WineDetails? GetDetails(string code)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.*, w.vintage AS w_vintage, w.litre_price AS w_litre_price, w.producer, w.wholesaler,
                   w.distributor, w.eco, w.vegan, w.biodynamic, w.image_url
            FROM wine_details d JOIN wines w ON w.code = d.code WHERE d.code = $code
            """;
        command.Parameters.AddWithValue("$code", code);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var details = new WineDetails
        {
            Code = code,
            Vintage = Str(reader, "w_vintage"),
            LitrePrice = Dbl(reader, "w_litre_price"),
            Producer = Str(reader, "producer"),
            Wholesaler = Str(reader, "wholesaler"),
            Distributor = Str(reader, "distributor"),
            Colour = Str(reader, "colour"),
            Smell = Str(reader, "smell"),
            Taste = Str(reader, "taste"),
            Method = Str(reader, "method"),
            Summary = Str(reader, "summary"),
            StoragePotential = Str(reader, "storage_potential"),
            StyleName = Str(reader, "style_name"),
            StyleDescription = Str(reader, "style_description"),
            Cork = Str(reader, "cork"),
            PackageType = Str(reader, "package_type"),
            Allergens = Str(reader, "allergens"),
            Sugar = Str(reader, "sugar"),
            Acid = Str(reader, "acid"),
            Fullness = Int(reader, "fullness"),
            Freshness = Int(reader, "freshness"),
            Tannins = Int(reader, "tannins"),
            Sweetness = Int(reader, "sweetness"),
            Bitterness = Int(reader, "bitterness"),
            Eco = Bool(reader, "eco"),
            Vegan = Bool(reader, "vegan"),
            Biodynamic = Bool(reader, "biodynamic"),
            FairTrade = Bool(reader, "fair_trade"),
            Kosher = Bool(reader, "kosher"),
            Gluten = Bool(reader, "gluten"),
            EnvironmentalPackaging = Bool(reader, "environmental_packaging"),
            ImageUrl = Str(reader, "image_url"),
        };
        reader.Close();

        var grapes = new List<Grape>();
        foreach (var row in QueryRows(connection, "SELECT grape, percent FROM wine_grapes WHERE code = $code ORDER BY percent DESC", ("$code", code)))
        {
            grapes.Add(new Grape((string)row[0]!, row[1] is null ? null : Convert.ToDouble(row[1], CultureInfo.InvariantCulture)));
        }
        var food = new List<FoodPairing>();
        foreach (var row in QueryRows(connection, "SELECT food_code, food_name FROM wine_food_pairings WHERE code = $code", ("$code", code)))
        {
            food.Add(new FoodPairing((string?)row[0] ?? "", (string)row[1]!));
        }
        return details with { Grapes = grapes, FoodPairings = food };
    }

    /// <summary>Codes whose details are missing or older than <paramref name="maxAge"/>, filtered set first.</summary>
    public IReadOnlyList<string> CodesNeedingDetails(TimeSpan maxAge, int limit, IReadOnlyCollection<string>? preferCodes = null)
    {
        using var connection = database.Open();
        var cutoff = Iso(DateTimeOffset.UtcNow - maxAge);
        var result = new List<string>();
        if (preferCodes is { Count: > 0 })
        {
            foreach (var code in preferCodes)
            {
                var stale = QueryRows(connection,
                    "SELECT 1 FROM wines WHERE code = $code AND (details_updated_at IS NULL OR details_updated_at < $cutoff)",
                    ("$code", code), ("$cutoff", cutoff)).Any();
                if (stale)
                {
                    result.Add(code);
                }
                if (result.Count >= limit)
                {
                    return result;
                }
            }
        }
        foreach (var row in QueryRows(connection, """
            SELECT code FROM wines
            WHERE delisted_at IS NULL AND (details_updated_at IS NULL OR details_updated_at < $cutoff)
            ORDER BY is_favorite DESC, details_updated_at IS NOT NULL, buyable DESC, code
            LIMIT $limit
            """, ("$cutoff", cutoff), ("$limit", limit)))
        {
            var code = (string)row[0]!;
            if (!result.Contains(code))
            {
                result.Add(code);
            }
            if (result.Count >= limit)
            {
                break;
            }
        }
        return result;
    }

    // ---------- stock ----------

    public void SaveStock(string code, IReadOnlyList<StoreStock> stock, DateTimeOffset observedAt)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM store_stock WHERE code = $code", ("$code", code));
        foreach (var entry in stock)
        {
            UpsertStore(connection, transaction, entry.Store);
            Execute(connection, transaction, """
                INSERT OR REPLACE INTO store_stock (code, store_id, stock_level, distance, observed_at)
                VALUES ($code, $store, $level, $distance, $now)
                """,
                ("$code", code), ("$store", entry.Store.StoreId), ("$level", entry.StockLevel),
                ("$distance", Db(entry.Distance)), ("$now", Iso(observedAt)));
        }
        transaction.Commit();
    }

    public IReadOnlyList<StoreStock> GetStock(string code)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.store_id, s.name, s.address, s.town, s.postal_code, s.phone, s.latitude, s.longitude,
                   st.stock_level, st.distance, st.observed_at
            FROM store_stock st JOIN stores s ON s.store_id = st.store_id
            WHERE st.code = $code ORDER BY st.rowid
            """;
        command.Parameters.AddWithValue("$code", code);
        using var reader = command.ExecuteReader();
        var result = new List<StoreStock>();
        while (reader.Read())
        {
            var store = new Store
            {
                StoreId = reader.GetString(0),
                Name = reader.GetString(1),
                Address = Str(reader, "address"),
                Town = Str(reader, "town"),
                PostalCode = Str(reader, "postal_code"),
                Phone = Str(reader, "phone"),
                Latitude = Dbl(reader, "latitude"),
                Longitude = Dbl(reader, "longitude"),
            };
            result.Add(new StoreStock(store, reader.GetInt32(8), Str(reader, "distance"), ParseIso(reader.GetString(10))));
        }
        return result;
    }

    public IReadOnlyList<Store> GetStores()
    {
        using var connection = database.Open();
        return QueryRows(connection, "SELECT store_id, name, address, town, postal_code, latitude, longitude, phone FROM stores ORDER BY name")
            .Select(r => new Store
            {
                StoreId = (string)r[0]!,
                Name = (string)r[1]!,
                Address = (string?)r[2],
                Town = (string?)r[3],
                PostalCode = (string?)r[4],
                Latitude = r[5] is null ? null : Convert.ToDouble(r[5], CultureInfo.InvariantCulture),
                Longitude = r[6] is null ? null : Convert.ToDouble(r[6], CultureInfo.InvariantCulture),
                Phone = (string?)r[7],
            })
            .ToList();
    }

    // ---------- prices ----------

    public IReadOnlyList<PricePoint> GetPriceHistory(string code)
    {
        using var connection = database.Open();
        return QueryRows(connection, "SELECT price, litre_price, observed_at FROM price_history WHERE code = $code ORDER BY observed_at", ("$code", code))
            .Select(r => new PricePoint(
                Convert.ToDouble(r[0], CultureInfo.InvariantCulture),
                r[1] is null ? null : Convert.ToDouble(r[1], CultureInfo.InvariantCulture),
                ParseIso((string)r[2]!)))
            .ToList();
    }

    // ---------- ratings ----------

    public void SaveRating(RatingResult rating)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            INSERT OR REPLACE INTO ratings (code, source, status, score, score_100, rating_count, matched_name,
                matched_vintage, url, match_confidence, search_query, error, fetched_at)
            VALUES ($code, $source, $status, $score, $score_100, $rating_count, $matched_name,
                $matched_vintage, $url, $match_confidence, $search_query, $error, $fetched_at)
            """,
            ("$code", rating.Code), ("$source", rating.Source), ("$status", rating.Status),
            ("$score", Db(rating.Score)), ("$score_100", Db(rating.Score100)), ("$rating_count", Db(rating.RatingCount)),
            ("$matched_name", Db(rating.MatchedName)), ("$matched_vintage", Db(rating.MatchedVintage)),
            ("$url", Db(rating.Url)), ("$match_confidence", Db(rating.MatchConfidence)),
            ("$search_query", Db(rating.SearchQuery)), ("$error", Db(rating.Error)), ("$fetched_at", Iso(rating.FetchedAt)));

        // Keep the list's sort/filter columns in step with the per-source rows.
        Execute(connection, transaction, """
            UPDATE wines SET
                best_score = (SELECT ROUND(AVG(score_100), 1) FROM ratings
                              WHERE code = $code AND status = 'matched' AND score_100 IS NOT NULL),
                rating_sources = (SELECT COUNT(*) FROM ratings
                                  WHERE code = $code AND status = 'matched' AND score_100 IS NOT NULL),
                ratings_updated_at = $now
            WHERE code = $code
            """, ("$code", rating.Code), ("$now", Iso(rating.FetchedAt)));
        transaction.Commit();
    }

    /// <summary>
    /// The next wine for the background rater: never rated or not checked for
    /// <paramref name="recheckAfter"/>, available (or a favourite). Favourites first, then wines
    /// the user has opened (they have details), then the core range, so the ratings people are
    /// most likely to look at arrive first.
    /// </summary>
    public string? NextWineForRating(TimeSpan recheckAfter)
    {
        using var connection = database.Open();
        return QueryRows(connection, """
            SELECT code FROM wines
            WHERE delisted_at IS NULL
              AND (available_online = 1 OR available_in_stores = 1 OR is_favorite = 1)
              AND (ratings_updated_at IS NULL OR ratings_updated_at < $cutoff)
            ORDER BY is_favorite DESC,
                     ratings_updated_at IS NULL DESC,
                     details_updated_at IS NOT NULL DESC,
                     CASE product_selection WHEN 'Basisutvalget' THEN 0 WHEN 'Tilleggsutvalget' THEN 1 ELSE 2 END,
                     code
            LIMIT 1
            """, ("$cutoff", Iso(DateTimeOffset.UtcNow - recheckAfter))).FirstOrDefault()?[0] as string;
    }

    /// <summary>
    /// The same wine in other bottle sizes: same name (without volume), category and country, same
    /// vintage (so a vintage-specific score never reaches another vintage). Ratings are about the
    /// wine, not the bottle, so these share them. Without a vintage the products only count as the
    /// same when both have product details saved (which would show a year; a failed page fetch does
    /// not count): a missing year in the listing more often means "not stated" than "non-vintage".
    /// </summary>
    public IReadOnlyList<string> GetSiblingCodes(string code)
    {
        using var connection = database.Open();
        return QueryRows(connection, """
            SELECT s.code FROM wines w JOIN wines s
              ON s.cuvee_key = w.cuvee_key AND s.code <> w.code
             AND s.country IS w.country
             AND (s.vintage = w.vintage
                  OR (s.vintage IS NULL AND w.vintage IS NULL
                      AND s.details_search_text IS NOT NULL AND w.details_search_text IS NOT NULL))
            WHERE w.code = $code AND w.cuvee_key IS NOT NULL
            ORDER BY s.code
            """, ("$code", code)).Select(r => (string)r[0]!).ToList();
    }

    /// <summary>
    /// How a source last identified this wine in any vintage or bottle size (its page and its name
    /// for the wine), so another vintage can be looked up directly instead of guessed from a search.
    /// </summary>
    public RatingResult? FindRatingLink(string code, string source)
    {
        using var connection = database.Open();
        var row = QueryRows(connection, """
            SELECT r.url, r.matched_name, r.matched_vintage, r.code
            FROM wines w JOIN wines s ON s.cuvee_key = w.cuvee_key AND s.country IS w.country
            JOIN ratings r ON r.code = s.code AND r.source = $source
            WHERE w.code = $code AND w.cuvee_key IS NOT NULL AND s.code <> w.code
              AND r.status IN ('matched', 'matched_no_score') AND r.url IS NOT NULL AND r.matched_name IS NOT NULL
            ORDER BY r.fetched_at DESC
            LIMIT 1
            """, ("$code", code), ("$source", source)).FirstOrDefault();
        return row is null
            ? null
            : new RatingResult
            {
                Code = (string)row[3]!,
                Source = source,
                Status = RatingStatus.Matched,
                Url = (string)row[0]!,
                MatchedName = (string)row[1]!,
                MatchedVintage = row[2] as string,
            };
    }

    /// <summary>
    /// Marks a wine as checked now, even if no source result was saved (all fresh, or a transient
    /// failure kept an older match), so the background rater moves on to other wines.
    /// </summary>
    public void TouchRatings(string code, DateTimeOffset at) =>
        ExecuteStandalone("UPDATE wines SET ratings_updated_at = $now WHERE code = $code", ("$code", code), ("$now", Iso(at)));

    public IReadOnlyList<RatingResult> GetRatings(string code)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ratings WHERE code = $code ORDER BY source";
        command.Parameters.AddWithValue("$code", code);
        using var reader = command.ExecuteReader();
        var result = new List<RatingResult>();
        while (reader.Read())
        {
            result.Add(new RatingResult
            {
                Code = code,
                Source = reader.GetString(reader.GetOrdinal("source")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                Score = Dbl(reader, "score"),
                Score100 = Dbl(reader, "score_100"),
                RatingCount = Int(reader, "rating_count"),
                MatchedName = Str(reader, "matched_name"),
                MatchedVintage = Str(reader, "matched_vintage"),
                Url = Str(reader, "url"),
                MatchConfidence = Dbl(reader, "match_confidence"),
                SearchQuery = Str(reader, "search_query"),
                Error = Str(reader, "error"),
                FetchedAt = ParseIso(reader.GetString(reader.GetOrdinal("fetched_at"))),
            });
        }
        return result;
    }

    // ---------- list queries ----------

    public IReadOnlyList<WineRow> Query(WineQuery query)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        var where = BuildWhere(query, command);
        var order = query.Sort switch
        {
            WineSort.Price => "w.price",
            WineSort.LitrePrice => "w.litre_price",
            WineSort.BestScore => "w.best_score",
            WineSort.Vintage => "w.vintage",
            WineSort.Country => "w.country",
            WineSort.FirstSeen => "w.first_seen_at",
            WineSort.PriceChange => "(w.price - w.previous_price)",
            // Score points per 100 kr per litre: cheap and good floats up.
            WineSort.ValueForMoney => "(w.best_score * 100 / NULLIF(w.litre_price, 0))",
            _ => "w.name COLLATE NOCASE",
        };
        var direction = query.Descending ? "DESC" : "ASC";
        // Rows without a value sort last in either direction.
        command.CommandText = $"""
            SELECT w.* FROM wines w
            {where}
            ORDER BY ({order}) IS NULL, {order} {direction}, w.name COLLATE NOCASE, w.code
            LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);
        using var reader = command.ExecuteReader();
        var rows = new List<WineRow>();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }
        return rows;
    }

    public int Count(WineQuery query)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM wines w {BuildWhere(query, command)}";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public WineRow? GetWine(string code) => Query(new WineQuery { Codes = [code], IncludeDelisted = true, Limit = 1 }).FirstOrDefault();

    public IReadOnlyList<string> GetCountries()
    {
        using var connection = database.Open();
        return QueryRows(connection, "SELECT DISTINCT country FROM wines WHERE country IS NOT NULL AND delisted_at IS NULL ORDER BY country")
            .Select(r => (string)r[0]!)
            .ToList();
    }

    public CatalogStats GetStats()
    {
        using var connection = database.Open();
        var row = QueryRows(connection, """
            SELECT COUNT(*),
                   SUM(CASE WHEN delisted_at IS NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN details_updated_at IS NOT NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN rating_sources > 0 THEN 1 ELSE 0 END)
            FROM wines
            """).Single();
        var last = QueryRows(connection, "SELECT MAX(finished_at) FROM sync_runs WHERE kind = 'catalog' AND status = 'completed'").Single()[0] as string;
        return new CatalogStats(
            Convert.ToInt32(row[0] ?? 0, CultureInfo.InvariantCulture),
            Convert.ToInt32(row[1] ?? 0, CultureInfo.InvariantCulture),
            Convert.ToInt32(row[2] ?? 0, CultureInfo.InvariantCulture),
            Convert.ToInt32(row[3] ?? 0, CultureInfo.InvariantCulture),
            last is null ? null : ParseIso(last));
    }

    public void SetFavorite(string code, bool favorite) =>
        ExecuteStandalone("UPDATE wines SET is_favorite = $v WHERE code = $code", ("$v", favorite), ("$code", code));

    public void SetNote(string code, string? note) =>
        ExecuteStandalone("UPDATE wines SET note = $v WHERE code = $code", ("$v", Db(string.IsNullOrWhiteSpace(note) ? null : note.Trim())), ("$code", code));

    // ---------- sync runs ----------

    public SyncRun StartRun(string kind, int pagesTotal, string? cursorJson = null)
    {
        var started = DateTimeOffset.UtcNow;
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_runs (kind, started_at, status, pages_total, cursor_json)
            VALUES ($kind, $started, 'running', $total, $cursor);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$started", Iso(started));
        command.Parameters.AddWithValue("$total", pagesTotal);
        command.Parameters.AddWithValue("$cursor", Db(cursorJson));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new SyncRun { Id = id, Kind = kind, StartedAt = started, Status = SyncRunStatus.Running, PagesTotal = pagesTotal, CursorJson = cursorJson };
    }

    public void UpdateRun(SyncRun run) =>
        ExecuteStandalone("""
            UPDATE sync_runs SET status = $status, finished_at = $finished, pages_done = $done, pages_total = $total,
                products_seen = $seen, products_new = $new, price_changes = $changes, error = $error, cursor_json = $cursor
            WHERE id = $id
            """,
            ("$id", run.Id), ("$status", run.Status), ("$finished", Db(run.FinishedAt is null ? null : Iso(run.FinishedAt.Value))),
            ("$done", run.PagesDone), ("$total", run.PagesTotal), ("$seen", run.ProductsSeen), ("$new", run.ProductsNew),
            ("$changes", run.PriceChanges), ("$error", Db(run.Error)), ("$cursor", Db(run.CursorJson)));

    /// <summary>
    /// The most recent unfinished run of a kind, for resuming after a cancel or crash.
    /// Old runs are not resumed: the catalogue has shifted too much for their page cursor.
    /// </summary>
    public SyncRun? GetResumableRun(string kind, TimeSpan? maxAge = null)
    {
        var latest = GetRuns(kind, 1).FirstOrDefault();
        if (latest is null || latest.Status == SyncRunStatus.Completed)
        {
            return null;
        }
        return DateTimeOffset.UtcNow - latest.StartedAt <= (maxAge ?? TimeSpan.FromHours(6)) ? latest : null;
    }

    /// <summary>Start of the last completed run before <paramref name="beforeRunId"/>.</summary>
    public DateTimeOffset? PreviousCompletedRunStart(string kind, long beforeRunId) =>
        GetRuns(kind, 1, $"status = 'completed' AND id < {beforeRunId}").FirstOrDefault()?.StartedAt;

    public SyncRun? GetLastRun(string kind) => GetRuns(kind, 1).FirstOrDefault();

    /// <summary>When the very first completed run of a kind finished (wines from it are not "new").</summary>
    public DateTimeOffset? FirstCompletedRunFinishedAt(string kind)
    {
        using var connection = database.Open();
        var value = QueryRows(connection, "SELECT MIN(finished_at) FROM sync_runs WHERE kind = $kind AND status = 'completed'",
            ("$kind", kind)).Single()[0] as string;
        return value is null ? null : ParseIso(value);
    }

    private IEnumerable<SyncRun> GetRuns(string kind, int limit, string? extraWhere = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM sync_runs WHERE kind = $kind {(extraWhere is null ? "" : "AND " + extraWhere)} ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<SyncRun>();
        while (reader.Read())
        {
            var finished = Str(reader, "finished_at");
            runs.Add(new SyncRun
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Kind = reader.GetString(reader.GetOrdinal("kind")),
                StartedAt = ParseIso(reader.GetString(reader.GetOrdinal("started_at"))),
                FinishedAt = finished is null ? null : ParseIso(finished),
                Status = reader.GetString(reader.GetOrdinal("status")),
                PagesDone = Int(reader, "pages_done") ?? 0,
                PagesTotal = Int(reader, "pages_total") ?? 0,
                ProductsSeen = Int(reader, "products_seen") ?? 0,
                ProductsNew = Int(reader, "products_new") ?? 0,
                PriceChanges = Int(reader, "price_changes") ?? 0,
                Error = Str(reader, "error"),
                CursorJson = Str(reader, "cursor_json"),
            });
        }
        return runs;
    }

    // ---------- settings ----------

    public string? GetSetting(string key)
    {
        using var connection = database.Open();
        return QueryRows(connection, "SELECT value FROM settings WHERE key = $key", ("$key", key)).FirstOrDefault()?[0] as string;
    }

    public void SetSetting(string key, string? value) =>
        ExecuteStandalone("INSERT OR REPLACE INTO settings (key, value) VALUES ($key, $value)", ("$key", key), ("$value", Db(value)));

    // ---------- helpers ----------

    private static string BuildWhere(WineQuery query, SqliteCommand command)
    {
        var clauses = new List<string>();
        if (!query.IncludeDelisted)
        {
            clauses.Add("w.delisted_at IS NULL");
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            // Every word must appear in the name, code, producer, district, country, category or a grape.
            var words = Ratings.WineText.FoldDiacritics(query.Text).ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < words.Length; i++)
            {
                var p = $"$text{i}";
                clauses.Add($"(w.search_text LIKE {p} ESCAPE '\\' OR w.details_search_text LIKE {p} ESCAPE '\\')");
                command.Parameters.AddWithValue(p, $"%{EscapeLike(words[i])}%");
            }
        }
        AddIn(clauses, command, "w.category_code", "cat", query.CategoryCodes);
        AddIn(clauses, command, "w.code", "code", query.Codes);
        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            clauses.Add("w.country = $country");
            command.Parameters.AddWithValue("$country", query.Country);
        }
        if (query.MinPrice is not null)
        {
            clauses.Add("w.price >= $min_price");
            command.Parameters.AddWithValue("$min_price", query.MinPrice);
        }
        if (query.MaxPrice is not null)
        {
            clauses.Add("w.price <= $max_price");
            command.Parameters.AddWithValue("$max_price", query.MaxPrice);
        }
        if (query.MinScore is not null)
        {
            clauses.Add("w.best_score >= $min_score");
            command.Parameters.AddWithValue("$min_score", query.MinScore);
        }
        if (query.AvailableOnly)
        {
            clauses.Add("(w.available_online = 1 OR w.available_in_stores = 1)");
        }
        if (query.FavoritesOnly)
        {
            clauses.Add("w.is_favorite = 1");
        }
        if (query.PriceDroppedOnly)
        {
            clauses.Add("w.previous_price IS NOT NULL AND w.price < w.previous_price");
        }
        var newSince = query.NewSince ?? (query.NewWithinDays is { } days ? DateTimeOffset.UtcNow.AddDays(-days) : null);
        if (newSince is not null)
        {
            clauses.Add("w.first_seen_at > $new_since");
            command.Parameters.AddWithValue("$new_since", Iso(newSince.Value));
        }
        if (!string.IsNullOrWhiteSpace(query.InStockAtStoreId))
        {
            clauses.Add("EXISTS (SELECT 1 FROM store_stock s WHERE s.code = w.code AND s.store_id = $store AND s.stock_level > 0)");
            command.Parameters.AddWithValue("$store", query.InStockAtStoreId);
        }
        return clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
    }

    private static void AddIn(List<string> clauses, SqliteCommand command, string column, string prefix, IReadOnlyCollection<string>? values)
    {
        if (values is null)
        {
            return;
        }
        if (values.Count == 0)
        {
            clauses.Add("0");
            return;
        }
        var names = new StringBuilder();
        var i = 0;
        foreach (var value in values)
        {
            var name = $"${prefix}{i++}";
            names.Append(names.Length == 0 ? name : ", " + name);
            command.Parameters.AddWithValue(name, value);
        }
        clauses.Add($"{column} IN ({names})");
    }

    private static WineRow ReadRow(SqliteDataReader r)
    {
        var delisted = Str(r, "delisted_at");
        var details = Str(r, "details_updated_at");
        var ratings = Str(r, "ratings_updated_at");
        return new WineRow
        {
            Code = r.GetString(r.GetOrdinal("code")),
            Name = r.GetString(r.GetOrdinal("name")),
            CategoryCode = Str(r, "category_code"),
            Category = Str(r, "category"),
            Country = Str(r, "country"),
            District = Str(r, "district"),
            SubDistrict = Str(r, "sub_district"),
            Producer = Str(r, "producer"),
            Vintage = Str(r, "vintage"),
            VolumeCl = Dbl(r, "volume_cl"),
            Alcohol = Dbl(r, "alcohol"),
            Price = Dbl(r, "price"),
            LitrePrice = Dbl(r, "litre_price"),
            PreviousPrice = Dbl(r, "previous_price"),
            ProductSelection = Str(r, "product_selection"),
            Buyable = Bool(r, "buyable"),
            AvailableOnline = Bool(r, "available_online"),
            AvailableInStores = Bool(r, "available_in_stores"),
            AvailabilityText = Str(r, "availability_text"),
            Url = Str(r, "url"),
            ImageUrl = Str(r, "image_url"),
            BestScore = Dbl(r, "best_score"),
            RatingSources = Int(r, "rating_sources") ?? 0,
            IsFavorite = Bool(r, "is_favorite"),
            Note = Str(r, "note"),
            FirstSeenAt = ParseIso(r.GetString(r.GetOrdinal("first_seen_at"))),
            DelistedAt = delisted is null ? null : ParseIso(delisted),
            DetailsUpdatedAt = details is null ? null : ParseIso(details),
            RatingsUpdatedAt = ratings is null ? null : ParseIso(ratings),
        };
    }

    private static void BindListing(SqliteCommand command, WineListing wine, string now)
    {
        command.Parameters.AddWithValue("$code", wine.Code);
        command.Parameters.AddWithValue("$name", wine.Name);
        command.Parameters.AddWithValue("$category_code", Db(wine.CategoryCode));
        command.Parameters.AddWithValue("$category", Db(wine.Category));
        command.Parameters.AddWithValue("$sub_category", Db(wine.SubCategory));
        command.Parameters.AddWithValue("$country", Db(wine.Country));
        command.Parameters.AddWithValue("$district", Db(wine.District));
        command.Parameters.AddWithValue("$sub_district", Db(wine.SubDistrict));
        command.Parameters.AddWithValue("$vintage", Db(wine.Vintage));
        command.Parameters.AddWithValue("$volume_cl", Db(wine.VolumeCl));
        command.Parameters.AddWithValue("$alcohol", Db(wine.Alcohol));
        command.Parameters.AddWithValue("$price", Db(wine.Price));
        command.Parameters.AddWithValue("$litre_price", Db(wine.LitrePrice));
        command.Parameters.AddWithValue("$status", Db(wine.Status));
        command.Parameters.AddWithValue("$product_selection", Db(wine.ProductSelection));
        command.Parameters.AddWithValue("$buyable", wine.Buyable);
        command.Parameters.AddWithValue("$expired", wine.Expired);
        command.Parameters.AddWithValue("$available_online", wine.AvailableOnline);
        command.Parameters.AddWithValue("$available_in_stores", wine.AvailableInStores);
        command.Parameters.AddWithValue("$availability_text", Db(wine.AvailabilityText));
        command.Parameters.AddWithValue("$sustainable", wine.Sustainable);
        command.Parameters.AddWithValue("$url", Db(wine.Url));
        command.Parameters.AddWithValue("$image_url", Db(wine.ImageUrl));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$search_text", SearchText(wine.Name, wine.Code, wine.Country, wine.District, wine.SubDistrict, wine.Category));
        command.Parameters.AddWithValue("$cuvee_key", VinlisteDatabase.CuveeKey(wine.Name, wine.CategoryCode));
    }

    /// <summary>Lower-case, accent-folded text so "øko" finds "Øko" and "chateau" finds "Château".</summary>
    internal static string SearchText(params string?[] parts) =>
        " " + Ratings.WineText.FoldDiacritics(string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)))).ToLowerInvariant() + " ";

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void UpsertStore(SqliteConnection connection, SqliteTransaction transaction, Store store) =>
        Execute(connection, transaction, """
            INSERT INTO stores (store_id, name, address, town, postal_code, phone, latitude, longitude)
            VALUES ($id, $name, $address, $town, $postal, $phone, $lat, $lon)
            ON CONFLICT(store_id) DO UPDATE SET name = excluded.name, address = excluded.address, town = excluded.town,
                postal_code = excluded.postal_code, phone = excluded.phone,
                latitude = excluded.latitude, longitude = excluded.longitude
            """,
            ("$id", store.StoreId), ("$name", store.Name), ("$address", Db(store.Address)), ("$town", Db(store.Town)),
            ("$postal", Db(store.PostalCode)), ("$phone", Db(store.Phone)), ("$lat", Db(store.Latitude)), ("$lon", Db(store.Longitude)));

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private void ExecuteStandalone(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        Execute(connection, null, sql, parameters);
    }

    private static List<object?[]> QueryRows(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    internal static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string? Str(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static double? Dbl(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    private static int? Int(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    private static bool Bool(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return !r.IsDBNull(i) && r.GetInt64(i) != 0;
    }
}
