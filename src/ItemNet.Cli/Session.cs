using ItemNet.Core;
using ItemNet.Core.FileSystem;
using ItemNet.Core.Plugins;

namespace ItemNet.Cli;

/// <summary>An opened game source: file system plus load order.</summary>
public sealed class Session : IDisposable
{
    public required GameSource Source { get; init; }
    public IndexedFileSystem Files => Source.Files;
    public GameData Game => Source.Game;

    /// <summary>--game <folder> reads a game folder (as when launched from MO2); otherwise --mo2/--profile.</summary>
    public static Session Open(CliOptions opts, bool verbose = true)
    {
        var source = opts.Get("game") is { } game
            ? GameSource.FromGameFolder(game)
            : GameSource.FromMo2(opts.Get("mo2", @"G:\MO2Data\SkyrimSE"), opts.Get("profile"));
        if (verbose)
        {
            var fs = source.Files;
            Console.WriteLine(source.Description);
            Console.WriteLine($"Loose index: {fs.LooseCount:N0} files in {fs.LooseIndexTime.TotalSeconds:F1}s");
            Console.WriteLine($"Archive index: {fs.ArchivedCount:N0} files from {fs.ArchivesLoaded.Count} archives in {fs.ArchiveIndexTime.TotalSeconds:F1}s");
            Console.WriteLine($"Plugins: {source.Game.Plugins.Count} active ({source.Game.Plugins.Count(p => p.IsLight)} light) in {source.Game.LoadTime.TotalSeconds:F1}s");
            foreach (var m in source.Game.MissingPlugins) Console.WriteLine($"  missing: {m}");
        }
        return new Session { Source = source };
    }

    public void Dispose() => Source.Dispose();
}
