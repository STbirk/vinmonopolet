using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Vinliste.Core.Data;

/// <summary>
/// The starter catalogue shipped with the app: a gzipped Vinliste database made by
/// tools/Vinliste.SeedBuilder. New users start from it instead of an empty list, and the
/// background updater brings it up to date.
/// </summary>
public static class SeedDatabase
{
    public const string SettingKey = "seed_created_at";

    /// <summary>
    /// Writes the seed to <paramref name="databasePath"/> when there is no catalogue yet (no file,
    /// or a database without wines). Returns true when the seed was installed.
    /// </summary>
    public static bool InstallIfNeeded(string databasePath, Func<Stream?> openSeed)
    {
        // Only a missing file or a database that is known to be empty gets the seed. Anything
        // unreadable (corrupt, locked) is left alone, so the user's favourites and notes are
        // never silently replaced; opening it reports the problem instead.
        if (File.Exists(databasePath) && !IsKnownEmpty(databasePath))
        {
            return false;
        }
        using var seed = openSeed();
        if (seed is null)
        {
            return false;
        }

        var temp = databasePath + ".seed";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
            using (var gzip = new GZipStream(seed, CompressionMode.Decompress))
            using (var file = File.Create(temp))
            {
                gzip.CopyTo(file);
            }

            // Replace an empty database (and its WAL files) only once the seed is fully written.
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                File.Delete(databasePath + suffix);
            }
            File.Move(temp, databasePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Another instance holds the files, or the disk is full: start without the seed.
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Compacts a finished database and writes it gzipped (used by the seed builder).</summary>
    public static void Export(VinlisteDatabase database, string gzipPath)
    {
        // Pooled connections keep the WAL open and make the journal-mode switch wait on them.
        SqliteConnection.ClearAllPools();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            // A standalone file: no WAL, no free pages.
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE; VACUUM;";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(gzipPath))!);
        using var source = File.OpenRead(database.Path);
        using var target = File.Create(gzipPath);
        using var gzip = new GZipStream(target, CompressionLevel.SmallestSize);
        source.CopyTo(gzip);
    }

    /// <summary>True only for a readable database with no wines (or no wines table yet).</summary>
    private static bool IsKnownEmpty(string databasePath)
    {
        if (new FileInfo(databasePath).Length == 0)
        {
            return true;
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM wines)";
            return Convert.ToInt64(command.ExecuteScalar()) == 0;
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        catch (SqliteException)
        {
            // Corrupt, not a database, or locked: not ours to overwrite.
            return false;
        }
    }
}
