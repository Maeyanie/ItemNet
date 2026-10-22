using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ItemNet.Core.SkyrimNet;

public enum ItemGender { Female, Male }

public sealed record OmniSightItemRow(
    long Id,
    uint RuntimeFormId,
    ItemGender Gender,
    string? SubjectName,
    string? SuggestedName,
    string? Description,
    string? DescriptionModel,
    bool Locked,
    string? ContentHash,
    string? FilePathJson,
    string? CaptureTimeLocal);

/// <summary>Reads and writes OmniSight item rows in one SkyrimNet playthrough DB.</summary>
public sealed class OmniSightDb : IDisposable
{
    /// <summary>
    /// Schema versions this code has been checked against. 31 (SkyrimNet beta 28) only adds
    /// omnisight_screenshots.manual_capture (DEFAULT 0), set for shots the player takes with the
    /// capture hotkey so automatic captures leave them alone. Inserts leave it at its default, as
    /// SkyrimNet's own item rows have it; updates keep whatever the row had.
    /// </summary>
    public static readonly int[] KnownSchemaVersions = [30, 31];

    private readonly SqliteConnection _conn;
    public string Path { get; }

    public OmniSightDb(string path, bool readOnly = true)
    {
        Path = path;
        var mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite;
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Copies the DB beside itself with SQLite's online backup API, which stays consistent even
    /// while SkyrimNet has it open (a plain file copy can tear mid-write). Returns the backup path.
    /// </summary>
    public static string Backup(string dbPath)
    {
        var target = $"{dbPath}.itemnet-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
        return target;
    }

    public int SchemaVersion()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_migrations";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static bool TryParseIdentifier(string identifier, out uint formId, out ItemGender gender)
    {
        formId = 0;
        gender = ItemGender.Female;
        var us = identifier.IndexOf('_');
        if (us != 8) return false;
        if (!uint.TryParse(identifier.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId)) return false;
        switch (identifier[(us + 1)..])
        {
            case "female": gender = ItemGender.Female; return true;
            case "male": gender = ItemGender.Male; return true;
            default: return false;
        }
    }

    public static string MakeIdentifier(uint formId, ItemGender gender) =>
        $"{formId:X8}_{(gender == ItemGender.Female ? "female" : "male")}";

    public List<OmniSightItemRow> ReadItems()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, subject_identifier, subject_name, suggested_name, description, description_model,
                   COALESCE(description_locked, 0), content_hash, file_path, capture_time_local
            FROM omnisight_screenshots WHERE subject_type = 'item'
            """;
        using var r = cmd.ExecuteReader();
        var rows = new List<OmniSightItemRow>();
        while (r.Read())
        {
            if (!TryParseIdentifier(r.GetString(1), out var formId, out var gender)) continue;
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            rows.Add(new OmniSightItemRow(r.GetInt64(0), formId, gender, S(2), S(3), S(4), S(5),
                r.GetInt64(6) != 0, S(7), S(8), S(9)));
        }
        return rows;
    }

    public OmniSightItemRow? GetItem(uint runtimeFormId, ItemGender gender)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, subject_identifier, subject_name, suggested_name, description, description_model,
                   COALESCE(description_locked, 0), content_hash, file_path, capture_time_local
            FROM omnisight_screenshots WHERE subject_type = 'item' AND subject_identifier = $id
            """;
        cmd.Parameters.AddWithValue("$id", MakeIdentifier(runtimeFormId, gender));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new OmniSightItemRow(r.GetInt64(0), runtimeFormId, gender, S(2), S(3), S(4), S(5), r.GetInt64(6) != 0, S(7), S(8), S(9));
    }

    /// <summary>Moves an item row to another subject_identifier (a temporary name, or a runtime ID).</summary>
    public bool Rename(string fromIdentifier, string toIdentifier)
    {
        if (!KnownSchemaVersions.Contains(SchemaVersion()))
            throw new InvalidOperationException($"Unknown SkyrimNet schema version {SchemaVersion()}; refusing to write");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE omnisight_screenshots SET subject_identifier = $to WHERE subject_type = 'item' AND subject_identifier = $from";
        cmd.Parameters.AddWithValue("$from", fromIdentifier);
        cmd.Parameters.AddWithValue("$to", toIdentifier);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Deletes one item row, so SkyrimNet describes whatever item has that ID afresh.</summary>
    public bool Delete(uint runtimeFormId, ItemGender gender)
    {
        if (!KnownSchemaVersions.Contains(SchemaVersion()))
            throw new InvalidOperationException($"Unknown SkyrimNet schema version {SchemaVersion()}; refusing to write");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM omnisight_screenshots WHERE subject_type = 'item' AND subject_identifier = $id";
        cmd.Parameters.AddWithValue("$id", MakeIdentifier(runtimeFormId, gender));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Inserts or updates one item row as a locked, ItemNet-authored description. An existing row
    /// keeps its content_hash (SkyrimNet computed it); a new row gets <see cref="OmniSightWrite.ContentHash"/>.
    /// Callers confirm before replacing a 'user-edited' row.
    /// </summary>
    public WriteOutcome Upsert(OmniSightWrite w)
    {
        if (!KnownSchemaVersions.Contains(SchemaVersion()))
            throw new InvalidOperationException($"Unknown SkyrimNet schema version {SchemaVersion()}; refusing to write");

        var identifier = MakeIdentifier(w.RuntimeFormId, w.Gender);
        var existing = GetItem(w.RuntimeFormId, w.Gender);
        var now = DateTimeOffset.UtcNow;
        var filePaths = System.Text.Json.JsonSerializer.Serialize(w.ImagePaths);

        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$type", "item");
        cmd.Parameters.AddWithValue("$id", identifier);
        cmd.Parameters.AddWithValue("$name", w.SubjectName);
        cmd.Parameters.AddWithValue("$suggested", w.SuggestedName);
        cmd.Parameters.AddWithValue("$desc", w.Description);
        cmd.Parameters.AddWithValue("$model", w.Model);
        cmd.Parameters.AddWithValue("$files", filePaths);
        cmd.Parameters.AddWithValue("$w", w.Width);
        cmd.Parameters.AddWithValue("$h", w.Height);
        cmd.Parameters.AddWithValue("$bytes", w.ImageBytes);
        cmd.Parameters.AddWithValue("$timeIso", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$ts", now.ToUnixTimeMilliseconds() / 1000.0);
        cmd.Parameters.AddWithValue("$hash", (object?)w.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$replaceHash", w.ReplaceContentHash && w.ContentHash is not null ? 1 : 0);

        if (existing is null)
        {
            // SkyrimNet never leaves these NULL ('' / 0.0); mirror it so its readers never meet a NULL text column.
            cmd.CommandText = """
                INSERT INTO omnisight_screenshots
                    (subject_type, subject_identifier, subject_name, location_name, location_cell,
                     position_x, position_y, position_z, capture_time_local, capture_time_game,
                     capture_timestamp_local, capture_timestamp_game,
                     file_path, image_format, image_width, image_height, image_size_bytes,
                     description, description_model, description_prompt, description_timestamp,
                     quality_score, line_of_sight_quality, distance_to_subject, is_indoor, weather_type, time_of_day,
                     is_valid, needs_refresh, generation_error, version, content_hash, suggested_name, description_locked)
                VALUES ($type, $id, $name, '', '', 0.0, 0.0, 0.0, $timeIso, '', $ts, 0.0,
                        $files, 'png', $w, $h, $bytes,
                        $desc, $model, 'item', $ts,
                        0.0, 0.0, 0.0, 0, '', '',
                        1, 0, '', 1, $hash, $suggested, 1)
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE omnisight_screenshots SET
                    subject_name = $name, capture_time_local = $timeIso, capture_timestamp_local = $ts,
                    file_path = $files, image_format = 'png', image_width = $w, image_height = $h, image_size_bytes = $bytes,
                    description = $desc, description_model = $model, description_timestamp = $ts,
                    is_valid = 1, needs_refresh = 0, generation_error = '',
                    content_hash = CASE WHEN $replaceHash = 1 THEN $hash ELSE COALESCE(content_hash, $hash) END,
                    suggested_name = $suggested, description_locked = 1
                WHERE subject_type = $type AND subject_identifier = $id
                """;
        }
        cmd.ExecuteNonQuery();
        tx.Commit();

        if (existing is null) return WriteOutcome.Inserted;
        return existing.DescriptionModel == "user-edited" ? WriteOutcome.ReplacedUserEdit : WriteOutcome.Updated;
    }

    public void Dispose() => _conn.Dispose();
}

public enum WriteOutcome { Inserted, Updated, ReplacedUserEdit }

public sealed record OmniSightWrite(
    uint RuntimeFormId,
    ItemGender Gender,
    string SubjectName,
    string SuggestedName,
    string Description,
    /// <summary>description_model, e.g. "ItemNet:nvidia/nemotron-3.5-super-vl" (+ "+edited").</summary>
    string Model,
    /// <summary>Paths as the game sees them (Data\SKSE\Plugins\SkyrimNet\screenshots\omnisight\...).</summary>
    IReadOnlyList<string> ImagePaths,
    int Width,
    int Height,
    long ImageBytes,
    string? ContentHash,
    /// <summary>
    /// Overwrite an existing row's content_hash too. A sync onto a slot that held another item
    /// (after a load-order change) must not keep that item's hash.
    /// </summary>
    bool ReplaceContentHash = false);
