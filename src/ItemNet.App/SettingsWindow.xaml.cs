using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ItemNet.App.Services;
using ItemNet.Core.Mo2;
using ItemNet.Vision;
using Microsoft.Win32;

namespace ItemNet.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string? _skyrimNetConfig;
    private readonly ObservableCollection<ProviderRow> _providers = [];
    private readonly ObservableCollection<ModelRow> _models = [];

    public AppSettings Result => _settings;

    public SettingsWindow(AppSettings settings, string? skyrimNetConfigDir)
    {
        InitializeComponent();
        _settings = settings;
        _skyrimNetConfig = skyrimNetConfigDir;

        ModeGame.IsChecked = settings.Mode == SourceMode.GameFolder;
        ModeMo2.IsChecked = settings.Mode == SourceMode.Mo2Profile;
        GamePath.Text = settings.GamePath;
        Mo2Path.Text = settings.Mo2Instance;
        LoadProfiles();
        Mo2Profile.Text = settings.Mo2Profile;

        foreach (var p in settings.Providers) _providers.Add(ProviderRow.From(p));
        foreach (var m in settings.VisionModels) _models.Add(ModelRow.From(m));
        ProvidersGrid.ItemsSource = _providers;
        ModelsGrid.ItemsSource = _models;

        PromptBox.Text = settings.PromptTemplate ?? PromptTemplate.Default;
        RenderSizeBox.Text = settings.RenderSize.ToString(CultureInfo.InvariantCulture);
        ThemeBox.Text = settings.Theme;
        CatalogFolder.Text = settings.CatalogFolder;
        ProjectLink.NavigateUri = new Uri(VisionClient.ProjectUrl);
    }

    private void LoadProfiles()
    {
        Mo2Profile.Items.Clear();
        try
        {
            if (Mo2Instance.FindIni(Mo2Path.Text) is not { } ini) return;
            var inst = Mo2Instance.Load(ini);
            foreach (var dir in Directory.EnumerateDirectories(inst.ProfilesDirectory).Select(Path.GetFileName).Order())
                Mo2Profile.Items.Add(dir);
            if (string.IsNullOrEmpty(Mo2Profile.Text)) Mo2Profile.Text = inst.SelectedProfile;
        }
        catch { /* not an instance (yet) */ }
    }

    private void OnMo2PathChanged(object sender, RoutedEventArgs e) => LoadProfiles();

    private void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Skyrim Special Edition folder (the one containing Data)" };
        if (dlg.ShowDialog(this) == true) GamePath.Text = dlg.FolderName;
    }

    private void OnBrowseMo2(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "MO2 instance folder" };
        if (dlg.ShowDialog(this) != true) return;
        Mo2Path.Text = dlg.FolderName;
        Mo2Profile.Text = "";
        LoadProfiles();
    }

    private void OnBrowseCatalog(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "ItemNet catalog folder" };
        if (dlg.ShowDialog(this) == true) CatalogFolder.Text = dlg.FolderName;
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dir = _skyrimNetConfig;
        if (dir is null)
        {
            var dlg = new OpenFolderDialog { Title = "SkyrimNet config folder (SKSE\\Plugins\\SkyrimNet\\config)" };
            if (dlg.ShowDialog(this) != true) return;
            dir = dlg.FolderName;
        }
        SkyrimNetImport.Result r;
        try { r = SkyrimNetImport.Import(dir); }
        catch (Exception ex) { MessageBox.Show(this, "Import failed: " + ex.Message, "ItemNet"); return; }

        foreach (var p in r.Providers)
        {
            var row = ProviderRow.From(p);
            var existing = _providers.FirstOrDefault(x => x.Id == p.Id);
            if (existing is not null) _providers[_providers.IndexOf(existing)] = row;
            else _providers.Add(row);
        }
        foreach (var m in r.VisionModels)
            if (!_models.Any(x => x.Model == m.Model && x.ProviderId == m.ProviderId))
                _models.Add(ModelRow.From(m));
        MessageBox.Show(this, "Imported from " + dir + ":\n\n" + string.Join("\n", r.Notes), "ItemNet");
    }

    private void OnAddProvider(object sender, RoutedEventArgs e) =>
        _providers.Add(new ProviderRow { Id = "custom", Name = "Custom", Endpoint = "http://localhost:1234/v1/chat/completions" });

    private void OnRemoveProvider(object sender, RoutedEventArgs e)
    {
        if (ProvidersGrid.SelectedItem is ProviderRow r) _providers.Remove(r);
    }

    private void OnAddModel(object sender, RoutedEventArgs e) =>
        _models.Add(new ModelRow { ProviderId = _providers.FirstOrDefault()?.Id ?? "", MaxTokens = "4096", Temperature = "0.7", Timeout = "300" });

    private void OnRemoveModel(object sender, RoutedEventArgs e)
    {
        if (ModelsGrid.SelectedItem is ModelRow r) _models.Remove(r);
    }

    private void OnResetPrompt(object sender, RoutedEventArgs e) => PromptBox.Text = PromptTemplate.Default;

    private void OnProjectLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ProvidersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ModelsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            _settings.Mode = ModeMo2.IsChecked == true ? SourceMode.Mo2Profile : SourceMode.GameFolder;
            _settings.GamePath = GamePath.Text.Trim();
            _settings.Mo2Instance = Mo2Path.Text.Trim();
            _settings.Mo2Profile = Mo2Profile.Text.Trim();
            _settings.Providers = _providers.Select(p => p.ToConfig()).ToList();
            _settings.VisionModels = _models.Select(m => m.ToConfig()).ToList();
            _settings.PromptTemplate = PromptBox.Text.Trim() == PromptTemplate.Default.Trim() ? null : PromptBox.Text;
            if (int.TryParse(RenderSizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) _settings.RenderSize = Math.Clamp(size, 128, 2048);
            _settings.CatalogFolder = CatalogFolder.Text.Trim();
            _settings.Theme = string.IsNullOrEmpty(ThemeBox.Text) ? "Dark" : ThemeBox.Text;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Check settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_settings.PromptTemplate is not null)
        {
            try { PromptTemplate.Render(_settings.PromptTemplate, new ItemFacts { Views = ["front"] }); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Prompt template", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        DialogResult = true;
    }
}

public abstract class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected static double? Dbl(string? s) => string.IsNullOrWhiteSpace(s) ? null
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"'{s}' isn't a number.");
    protected static int? Int(string? s) => string.IsNullOrWhiteSpace(s) ? null
        : int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"'{s}' isn't a whole number.");
    protected static string F(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
}

public sealed class ProviderRow : Row
{
    private string _id = "", _name = "", _endpoint = "", _apiKey = "", _extraJson = "";
    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string ApiKey { get => _apiKey; set { Set(ref _apiKey, value); Raise(nameof(MaskedKey)); } }
    public string ExtraJson { get => _extraJson; set => Set(ref _extraJson, value); }
    public string Source { get; set; } = "manual";
    public string MaskedKey => ApiKey.Length == 0 ? "(none)" : ApiKey.Length <= 8 ? "••••" : "••••" + ApiKey[^4..];

    public static ProviderRow From(ProviderConfig p) => new()
    {
        Id = p.Id, Name = p.Name, Endpoint = p.Endpoint, ApiKey = p.ApiKey, ExtraJson = p.ExtraBody?.ToJsonString() ?? "", Source = p.Source,
    };

    public ProviderConfig ToConfig()
    {
        JsonObject? extra = null;
        if (!string.IsNullOrWhiteSpace(ExtraJson))
            extra = JsonNode.Parse(ExtraJson) as JsonObject ?? throw new FormatException($"Provider '{Id}': extra JSON must be an object.");
        return new ProviderConfig { Id = Id.Trim(), Name = Name.Trim(), Endpoint = Endpoint.Trim(), ApiKey = ApiKey.Trim(), ExtraBody = extra, Source = Source };
    }
}

public sealed class ModelRow : Row
{
    private string _model = "", _providerId = "", _maxTokens = "", _temperature = "", _topP = "", _topK = "", _reasoning = "", _timeout = "", _extraJson = "";
    public string Model { get => _model; set => Set(ref _model, value); }
    public string ProviderId { get => _providerId; set => Set(ref _providerId, value); }
    public string MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }
    public string Temperature { get => _temperature; set => Set(ref _temperature, value); }
    public string TopP { get => _topP; set => Set(ref _topP, value); }
    public string TopK { get => _topK; set => Set(ref _topK, value); }
    public string Reasoning { get => _reasoning; set => Set(ref _reasoning, value); }
    public string Timeout { get => _timeout; set => Set(ref _timeout, value); }
    public string ExtraJson { get => _extraJson; set => Set(ref _extraJson, value); }

    public static ModelRow From(VisionModelConfig m) => new()
    {
        Model = m.Model, ProviderId = m.ProviderId, MaxTokens = m.MaxTokens.ToString(CultureInfo.InvariantCulture),
        Temperature = F(m.Temperature), TopP = F(m.TopP), TopK = m.TopK?.ToString(CultureInfo.InvariantCulture) ?? "",
        Reasoning = m.ReasoningEffort ?? "", Timeout = m.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), ExtraJson = m.ExtraJson ?? "",
    };

    public VisionModelConfig ToConfig()
    {
        if (!string.IsNullOrWhiteSpace(ExtraJson) && JsonNode.Parse(ExtraJson) is not JsonObject)
            throw new FormatException($"Model '{Model}': extra JSON must be an object.");
        return new VisionModelConfig
        {
            Model = Model.Trim(), ProviderId = ProviderId.Trim(), MaxTokens = Int(MaxTokens) ?? 4096,
            Temperature = Dbl(Temperature), TopP = Dbl(TopP), TopK = Int(TopK),
            ReasoningEffort = string.IsNullOrWhiteSpace(Reasoning) ? null : Reasoning.Trim(),
            TimeoutSeconds = Int(Timeout) ?? 300, ExtraJson = string.IsNullOrWhiteSpace(ExtraJson) ? null : ExtraJson,
        };
    }
}
