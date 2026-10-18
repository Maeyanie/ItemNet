using System.Globalization;
using System.Text.Json;
using ItemNet.Core.SkyrimNet;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda.Plugins;

namespace ItemNet.Core.Catalog;

public enum EntryStatus { Draft, Approved }

/// <summary>
/// One reviewed description. Keyed by FormKey and gender, never by runtime FormID, so it outlives
/// load-order changes and can be synced into any playthrough DB.
/// </summary>
public sealed record CatalogEntry(
    FormKey Key,
    ItemGender Gender,
    string GameName,
    string EditorId,
    string SuggestedName,
    string Description,
    string Model,
    bool Edited,
    EntryStatus Status,
    IReadOnlyList<string> Images,
    string RenderSettings,
    DateTime Updated);

/// <summary>ItemNet's own store and source of truth (ItemNet.db plus an images folder).</summary>
public sealed class CatalogStore : IDisposable
{
    private readonly SqliteConnection _conn;
    public string Directory { get; }
    public string ImagesDirectory => Path.Combine(Directory, "images");

    public CatalogStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "ItemNet.db") }.ToString());
        _conn.Open();
        Exec("""
            CREATE TABLE IF NOT EXISTS entries (
                plugin TEXT NOT NULL,
                local_id INTEGER NOT NULL,
                gender TEXT NOT NULL,
                game_name TEXT NOT NULL DEFAULT '',
                editor_id TEXT NOT NULL DEFAULT '',
                suggested_name TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                model TEXT NOT NULL DEFAULT '',
                edited INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'draft',
                images TEXT NOT NULL DEFAULT '[]',
                render_settings TEXT NOT NULL DEFAULT '',
                updated TEXT NOT NULL,
                PRIMARY KEY (plugin, local_id, gender)
            );
            CREATE TABLE IF NOT EXISTS syncs (
                plugin TEXT NOT NULL,
                local_id INTEGER NOT NULL,
                gender TEXT NOT NULL,
                db_file TEXT NOT NULL,
                runtime_id INTEGER NOT NULL,
                synced TEXT NOT NULL,
                PRIMARY KEY (plugin, local_id, gender, db_file)
            );
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string G(ItemGender g) => g == ItemGender.Female ? "female" : "male";

    /// <summary>Where an entry's images live: images/&lt;plugin&gt;/&lt;local id&gt;_&lt;gender&gt;_&lt;view&gt;.png.</summary>
    public string ImagePath(FormKey key, ItemGender gender, int view) =>
        Path.Combine(ImagesDirectory, key.ModKey.FileName, $"{key.ID:X6}_{G(gender)}_{view}.png");

    public void Upsert(CatalogEntry e)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries (plugin, local_id, gender, game_name, editor_id, suggested_name, description, model, edited, status, images, render_settings, updated)
            VALUES ($p, $id, $g, $gn, $edid, $sn, $d, $m, $ed, $st, $img, $rs, $u)
            ON CONFLICT (plugin, local_id, gender) DO UPDATE SET
                game_name = $gn, editor_id = $edid, suggested_name = $sn, description = $d, model = $m, edited = $ed,
                status = $st, images = $img, render_settings = $rs, updated = $u
            """;
        cmd.Parameters.AddWithValue("$p", e.Key.ModKey.FileName.String);
        cmd.Parameters.AddWithValue("$id", (long)e.Key.ID);
        cmd.Parameters.AddWithValue("$g", G(e.Gender));
        cmd.Parameters.AddWithValue("$gn", e.GameName);
        cmd.Parameters.AddWithValue("$edid", e.EditorId);
        cmd.Parameters.AddWithValue("$sn", e.SuggestedName);
        cmd.Parameters.AddWithValue("$d", e.Description);
        cmd.Parameters.AddWithValue("$m", e.Model);
        cmd.Parameters.AddWithValue("$ed", e.Edited ? 1 : 0);
        cmd.Parameters.AddWithValue("$st", e.Status == EntryStatus.Approved ? "approved" : "draft");
        cmd.Parameters.AddWithValue("$img", JsonSerializer.Serialize(e.Images));
        cmd.Parameters.AddWithValue("$rs", e.RenderSettings);
        cmd.Parameters.AddWithValue("$u", e.Updated.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void Delete(FormKey key, ItemGender gender)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE plugin = $p AND local_id = $id AND gender = $g";
        cmd.Parameters.AddWithValue("$p", key.ModKey.FileName.String);
        cmd.Parameters.AddWithValue("$id", (long)key.ID);
        cmd.Parameters.AddWithValue("$g", G(gender));
        cmd.ExecuteNonQuery();
    }

    public void RecordSync(FormKey key, ItemGender gender, string dbFile, uint runtimeId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO syncs (plugin, local_id, gender, db_file, runtime_id, synced) VALUES ($p, $id, $g, $db, $rt, $t)
            ON CONFLICT (plugin, local_id, gender, db_file) DO UPDATE SET runtime_id = $rt, synced = $t
            """;
        cmd.Parameters.AddWithValue("$p", key.ModKey.FileName.String);
        cmd.Parameters.AddWithValue("$id", (long)key.ID);
        cmd.Parameters.AddWithValue("$g", G(gender));
        cmd.Parameters.AddWithValue("$db", Path.GetFileName(dbFile));
        cmd.Parameters.AddWithValue("$rt", (long)runtimeId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public List<CatalogEntry> All()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT plugin, local_id, gender, game_name, editor_id, suggested_name, description, model, edited, status, images, render_settings, updated FROM entries";
        using var r = cmd.ExecuteReader();
        var list = new List<CatalogEntry>();
        while (r.Read())
        {
            if (!ModKey.TryFromFileName(r.GetString(0), out var mod)) continue;
            list.Add(new CatalogEntry(
                new FormKey(mod, (uint)r.GetInt64(1)),
                r.GetString(2) == "female" ? ItemGender.Female : ItemGender.Male,
                r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8) != 0,
                r.GetString(9) == "approved" ? EntryStatus.Approved : EntryStatus.Draft,
                JsonSerializer.Deserialize<List<string>>(r.GetString(10)) ?? [],
                r.GetString(11),
                DateTime.Parse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return list;
    }

    public void Dispose() => _conn.Dispose();
}
