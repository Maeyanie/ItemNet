using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ItemNet.Render.Scene;
using ItemNet.Vision;

namespace ItemNet.App.Services;

public enum SourceMode { GameFolder, Mo2Profile }

/// <summary>Per-user settings in %LOCALAPPDATA%\ItemNet\settings.json (API keys included, so never beside the exe).</summary>
public sealed class AppSettings
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ItemNet");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SourceMode Mode { get; set; } = SourceMode.GameFolder;
    public string GamePath { get; set; } = "";
    public string Mo2Instance { get; set; } = "";
    public string Mo2Profile { get; set; } = "";

    public List<ProviderConfig> Providers { get; set; } = [];
    public List<VisionModelConfig> VisionModels { get; set; } = [];
    public int SelectedModel { get; set; }
    /// <summary>Null means the built-in default prompt.</summary>
    public string? PromptTemplate { get; set; }

    /// <summary>"Dark", "Light" or "System".</summary>
    public string Theme { get; set; } = "Dark";

    public MannequinStyle Mannequin { get; set; } = MannequinStyle.Textured;
    public float Weight { get; set; } = 0.5f;
    public int RenderSize { get; set; } = 512;
    public string CatalogFolder { get; set; } = Folder;
    /// <summary>File name of the SkyrimNet DB last chosen; empty means the newest.</summary>
    public string TargetDb { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.TraceWarning($"Settings unreadable, using defaults: {e.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>True when this process runs inside MO2's virtual file system.</summary>
    public static bool RunningUnderMo2() =>
        System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Any(m => m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase));

    /// <summary>First-run defaults: the game folder through MO2's VFS if launched from MO2, else the first MO2 instance found.</summary>
    public void ApplyFirstRunDefaults()
    {
        GamePath = Core.GameSource.DetectGamePath() ?? "";
        if (RunningUnderMo2() || !TryFindMo2Instance(out var ini, out var profile))
        {
            Mode = SourceMode.GameFolder;
            return;
        }
        Mode = SourceMode.Mo2Profile;
        Mo2Instance = ini;
        Mo2Profile = profile;
    }

    private static bool TryFindMo2Instance(out string ini, out string profile)
    {
        ini = profile = "";
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (!Directory.Exists(root)) return false;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var candidate = Path.Combine(dir, "ModOrganizer.ini");
            if (!File.Exists(candidate)) continue;
            try
            {
                var inst = Core.Mo2.Mo2Instance.Load(candidate);
                if (!File.Exists(Path.Combine(inst.GameDataPath, "Skyrim.esm"))) continue;
                ini = candidate;
                profile = inst.SelectedProfile;
                return true;
            }
            catch { /* not a Skyrim instance */ }
        }
        return false;
    }
}
