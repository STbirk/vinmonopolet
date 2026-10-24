using Microsoft.Data.Sqlite;

namespace Vinliste.Core.Data;

/// <summary>Owns the SQLite file and its schema. Migrations are forward-only, tracked by PRAGMA user_version.</summary>
public sealed class VinlisteDatabase
{
    private static readonly string[] Migrations =
    [
        // 1: initial schema
        """
        CREATE TABLE wines (
            code TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            category_code TEXT,
            category TEXT,
            sub_category TEXT,
            country TEXT,
            district TEXT,
            sub_district TEXT,
            producer TEXT,
            wholesaler TEXT,
            distributor TEXT,
            vintage TEXT,
            volume_cl REAL,
            alcohol REAL,
            price REAL,
            litre_price REAL,
            previous_price REAL,
            price_changed_at TEXT,
            status TEXT,
            product_selection TEXT,
            buyable INTEGER NOT NULL DEFAULT 0,
            expired INTEGER NOT NULL DEFAULT 0,
            available_online INTEGER NOT NULL DEFAULT 0,
            available_in_stores INTEGER NOT NULL DEFAULT 0,
            availability_text TEXT,
            sustainable INTEGER NOT NULL DEFAULT 0,
            eco INTEGER NOT NULL DEFAULT 0,
            vegan INTEGER NOT NULL DEFAULT 0,
            biodynamic INTEGER NOT NULL DEFAULT 0,
            url TEXT,
            image_url TEXT,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            delisted_at TEXT,
            details_updated_at TEXT,
            ratings_updated_at TEXT,
            best_score REAL,
            rating_sources INTEGER NOT NULL DEFAULT 0,
            is_favorite INTEGER NOT NULL DEFAULT 0,
            note TEXT,
            -- Lower-cased, accent-folded text for search (SQLite LIKE only folds ASCII case).
            search_text TEXT,
            details_search_text TEXT
        );
        CREATE INDEX ix_wines_category ON wines(category_code);
        CREATE INDEX ix_wines_country ON wines(country);
        CREATE INDEX ix_wines_price ON wines(price);
        CREATE INDEX ix_wines_best_score ON wines(best_score);

        CREATE TABLE wine_details (
            code TEXT PRIMARY KEY REFERENCES wines(code) ON DELETE CASCADE,
            colour TEXT, smell TEXT, taste TEXT, method TEXT, summary TEXT,
            storage_potential TEXT, style_name TEXT, style_description TEXT,
            cork TEXT, package_type TEXT, allergens TEXT, sugar TEXT, acid TEXT,
            fullness INTEGER, freshness INTEGER, tannins INTEGER, sweetness INTEGER, bitterness INTEGER,
            fair_trade INTEGER NOT NULL DEFAULT 0, kosher INTEGER NOT NULL DEFAULT 0,
            gluten INTEGER NOT NULL DEFAULT 0, environmental_packaging INTEGER NOT NULL DEFAULT 0,
            raw_json TEXT
        );

        CREATE TABLE wine_grapes (
            code TEXT NOT NULL REFERENCES wines(code) ON DELETE CASCADE,
            grape TEXT NOT NULL,
            percent REAL
        );
        CREATE INDEX ix_wine_grapes_code ON wine_grapes(code);
        CREATE INDEX ix_wine_grapes_grape ON wine_grapes(grape);

        CREATE TABLE wine_food_pairings (
            code TEXT NOT NULL REFERENCES wines(code) ON DELETE CASCADE,
            food_code TEXT,
            food_name TEXT NOT NULL
        );
        CREATE INDEX ix_wine_food_code ON wine_food_pairings(code);

        CREATE TABLE price_history (
            code TEXT NOT NULL REFERENCES wines(code) ON DELETE CASCADE,
            price REAL NOT NULL,
            litre_price REAL,
            observed_at TEXT NOT NULL
        );
        CREATE INDEX ix_price_history_code ON price_history(code, observed_at);

        CREATE TABLE stores (
            store_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            address TEXT, town TEXT, postal_code TEXT, phone TEXT,
            latitude REAL, longitude REAL
        );

        CREATE TABLE store_stock (
            code TEXT NOT NULL REFERENCES wines(code) ON DELETE CASCADE,
            store_id TEXT NOT NULL REFERENCES stores(store_id),
            stock_level INTEGER NOT NULL,
            distance TEXT,
            observed_at TEXT NOT NULL,
            PRIMARY KEY (code, store_id)
        );

        CREATE TABLE ratings (
            code TEXT NOT NULL REFERENCES wines(code) ON DELETE CASCADE,
            source TEXT NOT NULL,
            status TEXT NOT NULL,
            score REAL,
            score_100 REAL,
            rating_count INTEGER,
            matched_name TEXT,
            matched_vintage TEXT,
            url TEXT,
            match_confidence REAL,
            search_query TEXT,
            error TEXT,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY (code, source)
        );

        CREATE TABLE sync_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL,
            started_at TEXT NOT NULL,
            finished_at TEXT,
            status TEXT NOT NULL,
            pages_done INTEGER NOT NULL DEFAULT 0,
            pages_total INTEGER NOT NULL DEFAULT 0,
            products_seen INTEGER NOT NULL DEFAULT 0,
            products_new INTEGER NOT NULL DEFAULT 0,
            price_changes INTEGER NOT NULL DEFAULT 0,
            error TEXT,
            cursor_json TEXT
        );

        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT
        );
        """,
    ];

    public VinlisteDatabase(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache: with a shared cache, readers block behind every write
            // transaction; WAL + busy_timeout already handle concurrency.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        }.ToString();
        Migrate();
    }

    public string Path { get; }

    public string ConnectionString { get; }

    public static int LatestVersion => Migrations.Length;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public int GetSchemaVersion()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void Migrate()
    {
        using var connection = Open();
        var version = GetSchemaVersion();
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"Database {Path} has schema version {version}, newer than this app supports ({Migrations.Length}).");
        }
        for (var next = version; next < Migrations.Length; next++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Migrations[next] + $"\nPRAGMA user_version = {next + 1};";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
