namespace ItemNet.Core.FileSystem;

/// <summary>
/// The game's view of Data: loose files override archives, later archives override earlier ones.
/// Paths are Data-relative and case-insensitive (e.g. "meshes\armor\iron\cuirass_1.nif").
/// </summary>
public interface IGameFileSystem
{
    bool Exists(string path);

    /// <summary>Reads a file, or returns null if it doesn't exist anywhere.</summary>
    byte[]? ReadAllBytes(string path);

    /// <summary>Where the winning copy of a file comes from ("loose: ModName" / "bsa: Foo.bsa").</summary>
    string? DescribeSource(string path);

    /// <summary>Physical path of a loose file (plugins, BSAs), or null if it only exists in an archive.</summary>
    string? GetLoosePath(string path);

    static string Normalize(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        if (p.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.ToLowerInvariant();
    }
}
