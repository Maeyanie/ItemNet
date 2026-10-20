using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ItemNet.App.Services;
using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;
using ItemNet.Render.D3D;
using ItemNet.Render.Scene;
using ItemNet.Vision;

namespace ItemNet.App.ViewModels;

public enum StatusFilter { All, NoDescription, SkyrimNetAuto, UserEdited, ItemNet, Drafts }
public enum MannequinChoice { Textured, Clay, None }

public sealed partial class MainViewModel : ObservableObject
{
    private Workspace? _ws;
    private int _renderVersion;
    private List<RenderedView>? _views;
    private string _viewsKey = "";
    private string _generatedName = "";
    private string _generatedDescription = "";
    private string _generatedModel = "";
    private CancellationTokenSource? _describeCts;
    private CancellationTokenSource? _weightDebounce;

    public AppSettings Settings { get; private set; }
    public ObservableCollection<ItemRowViewModel> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public ObservableCollection<BitmapSource?> Views { get; } = [null, null, null, null];
    public ObservableCollection<BitmapSource?> SkyrimNetViews { get; } = [null, null, null, null];
    public ObservableCollection<FileInfo> Databases { get; } = [];
    public ObservableCollection<VisionModelConfig> Models { get; } = [];
    public IReadOnlyList<StatusFilter> StatusFilters { get; } = Enum.GetValues<StatusFilter>();
    public IReadOnlyList<MannequinChoice> MannequinChoices { get; } = Enum.GetValues<MannequinChoice>();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadingText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private StatusFilter _filter = StatusFilter.All;
    [ObservableProperty] private ItemRowViewModel? _selected;
    [ObservableProperty] private bool _isFemale = true;
    [ObservableProperty] private MannequinChoice _mannequin;
    [ObservableProperty] private double _weight;
    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private string _renderWarnings = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _skyrimNetName = "";
    [ObservableProperty] private string _skyrimNetDescription = "";
    [ObservableProperty] private string _skyrimNetSource = "";
    [ObservableProperty] private FileInfo? _selectedDatabase;
    [ObservableProperty] private VisionModelConfig? _selectedModel;
    [ObservableProperty] private string _suggestedName = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _describeInfo = "";
    [ObservableProperty] private string _rawReply = "";
    [ObservableProperty] private bool _isDescribing;
    [ObservableProperty] private string _itemCount = "";
    [ObservableProperty] private bool _isDraft;

    private readonly Dictionary<Mutagen.Bethesda.Plugins.FormKey, ItemRowViewModel> _rowsByKey = new();

    // ---- for the batch window ------------------------------------------------------------------

    internal Workspace? Workspace => _ws;
    /// <summary>The rows the list currently shows (search and filter applied), in display order.</summary>
    internal List<ItemRowViewModel> VisibleRows() => ItemsView.Cast<ItemRowViewModel>().ToList();

    private HashSet<ItemRowViewModel> _multiSelection = [];

    /// <summary>The list's selected rows; the view keeps this current (SelectedItems isn't bindable).</summary>
    internal void SetMultiSelection(IEnumerable<ItemRowViewModel> rows) => _multiSelection = rows.ToHashSet();

    /// <summary>What a batch works on: the selected rows when several are selected, else every visible row.</summary>
    internal List<ItemRowViewModel> BatchRows(out bool fromSelection)
    {
        var visible = VisibleRows();
        var selected = _multiSelection.Count > 1 ? visible.Where(_multiSelection.Contains).ToList() : [];
        fromSelection = selected.Count > 1;
        return fromSelection ? selected : visible;
    }
    internal SceneOptions BatchSceneOptions() => CurrentSceneOptions();
    internal string MannequinLabel => Mannequin.ToString();

    /// <summary>Updates the F/M labels of rows a batch touched, without re-filtering under the user.</summary>
    internal void RefreshRow(Core.Items.ItemEntry e)
    {
        if (_ws is not null && _rowsByKey.TryGetValue(e.Key, out var row)) row.Refresh(_ws);
    }

    /// <summary>Re-applies the filter, e.g. once a batch finishes.</summary>
    internal void RefreshList()
    {
        ItemsView.Refresh();
        UpdateCount();
    }

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        _mannequin = settings.Mannequin == MannequinStyle.Clay ? MannequinChoice.Clay : MannequinChoice.Textured;
        _weight = settings.Weight;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is ItemRowViewModel r && Matches(r);
        LoadModels();
    }

    private ItemGender Gender => IsFemale ? ItemGender.Female : ItemGender.Male;

    private void LoadModels()
    {
        Models.Clear();
        foreach (var m in Settings.VisionModels) Models.Add(m);
        SelectedModel = Models.Count == 0 ? null : Models[Math.Clamp(Settings.SelectedModel, 0, Models.Count - 1)];
    }

    // ---- loading -------------------------------------------------------------------------------

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsLoading = true;
        StatusText = "";
        var old = _ws;
        _ws = null;
        Items.Clear();
        _rowsByKey.Clear();
        Selected = null;
        try
        {
            old?.Dispose();
            var progress = new Progress<string>(t => LoadingText = t);
            var settings = Settings;
            _ws = await Task.Run(() => Workspace.Open(settings, progress));
            SourceText = _ws.SourceLabel;
            Databases.Clear();
            foreach (var db in _ws.Databases) Databases.Add(db);
            SelectedDatabase = _ws.TargetDb;
            foreach (var e in _ws.Items)
            {
                var row = new ItemRowViewModel(e);
                row.Refresh(_ws);
                Items.Add(row);
                _rowsByKey[e.Key] = row;
            }
            UpdateCount();
            StatusText = $"{_ws.Items.Count:N0} items from {_ws.Source.Game.Plugins.Count} plugins."
                + (_ws.Databases.Count == 0 ? " No SkyrimNet database found; saves go to the catalog only." : "");
        }
        catch (Exception e)
        {
            StatusText = "Couldn't load the game: " + e.Message;
            MessageBox.Show(e.Message + "\n\nCheck the data source in Settings.", "ItemNet", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsLoading = false; }
    }

    partial void OnSelectedDatabaseChanged(FileInfo? value)
    {
        // While loading, the combo box reports null as its list is refilled; that isn't a choice.
        // The new workspace has already selected the remembered DB.
        if (IsLoading || _ws is null || value?.FullName == _ws.TargetDb?.FullName) return;
        _ws.SelectDatabase(value);
        Settings.TargetDb = value?.Name ?? "";
        Settings.Save();
        foreach (var r in Items) r.Refresh(_ws);
        ItemsView.Refresh();
        ShowSkyrimNetEntry();
    }

    // ---- filtering -----------------------------------------------------------------------------

    private bool Matches(ItemRowViewModel r)
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (!(r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                  || r.EditorId.Contains(q, StringComparison.OrdinalIgnoreCase)
                  || r.Plugin.Contains(q, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        var s = Gender == ItemGender.Female ? r.Female : r.Male;
        return Filter switch
        {
            StatusFilter.NoDescription => s == DescriptionState.None,
            StatusFilter.SkyrimNetAuto => s == DescriptionState.SkyrimNetAuto,
            StatusFilter.UserEdited => s == DescriptionState.UserEdited,
            StatusFilter.ItemNet => s == DescriptionState.ItemNet,
            StatusFilter.Drafts => s == DescriptionState.Draft,
            _ => true,
        };
    }

    partial void OnSearchTextChanged(string value) { ItemsView.Refresh(); UpdateCount(); }
    partial void OnFilterChanged(StatusFilter value) { ItemsView.Refresh(); UpdateCount(); }
    private void UpdateCount() => ItemCount = $"{ItemsView.Cast<object>().Count():N0} of {Items.Count:N0}";

    // ---- selection and rendering ---------------------------------------------------------------

    partial void OnSelectedChanged(ItemRowViewModel? value)
    {
        ShowDetails();
        ShowSkyrimNetEntry();
        LoadCatalogText();
        _ = RenderAsync();
    }

    partial void OnIsFemaleChanged(bool value)
    {
        ItemsView.Refresh();
        UpdateCount();
        ShowSkyrimNetEntry();
        LoadCatalogText();
        _ = RenderAsync();
    }

    partial void OnMannequinChanged(MannequinChoice value)
    {
        if (value != MannequinChoice.None)
        {
            Settings.Mannequin = value == MannequinChoice.Clay ? MannequinStyle.Clay : MannequinStyle.Textured;
            Settings.Save();
        }
        _ = RenderAsync();
    }

    partial void OnWeightChanged(double value)
    {
        // Debounce slider drags: render once the value settles.
        _weightDebounce?.Cancel();
        var cts = _weightDebounce = new CancellationTokenSource();
        _ = Task.Delay(350, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Settings.Weight = (float)value;
            Settings.Save();
            _ = RenderAsync();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private SceneOptions CurrentSceneOptions() => new()
    {
        Weight = (float)Weight,
        ShowMannequin = Mannequin != MannequinChoice.None,
        Mannequin = Mannequin == MannequinChoice.Clay ? MannequinStyle.Clay : MannequinStyle.Textured,
    };

    private string CurrentViewsKey() =>
        Selected is null ? "" : $"{Selected.Entry.Key}|{Gender}|{Mannequin}|{Weight:F2}|{Settings.RenderSize}";

    [RelayCommand]
    public async Task RenderAsync()
    {
        if (_ws is null || Selected is null) { ClearViews(); return; }
        var version = ++_renderVersion;
        var key = CurrentViewsKey();
        var entry = Selected.Entry;
        var gender = Gender;
        if (gender == ItemGender.Female ? !entry.HasFemaleModel && !entry.HasMaleModel : !entry.HasMaleModel && !entry.HasFemaleModel)
        {
            ClearViews();
            RenderWarnings = "This item has no model.";
            return;
        }
        IsRendering = true;
        try
        {
            var (views, warnings) = await _ws.RenderAsync(entry, gender, CurrentSceneOptions(), Settings.RenderSize, CancellationToken.None);
            if (version != _renderVersion) return; // superseded by a newer selection
            _views = views;
            _viewsKey = key;
            for (var i = 0; i < 4; i++) Views[i] = i < views.Count ? Images.FromView(views[i]) : null;
            RenderWarnings = warnings.Count == 0 ? "" : string.Join("\n", warnings.Take(8)) + (warnings.Count > 8 ? $"\n… {warnings.Count - 8} more" : "");
        }
        catch (Exception e)
        {
            if (version == _renderVersion) { ClearViews(); RenderWarnings = "Render failed: " + e.Message; }
        }
        finally
        {
            if (version == _renderVersion) IsRendering = false;
        }
    }

    private void ClearViews()
    {
        _views = null;
        _viewsKey = "";
        for (var i = 0; i < 4; i++) Views[i] = null;
    }

    private void ShowDetails()
    {
        if (Selected?.Entry is not { } e) { Details = ""; return; }
        Details = $"{e.Name}   [{e.EditorId}]\n" +
                  $"{e.Key}   runtime {(e.RuntimeId is { } r ? r.ToString("X8") : "—")}\n" +
                  $"{e.Kind}; slots {e.SlotNames}; AR {e.ArmorRating:0.#}, {e.Weight:0.#} wt, {e.Value} gold\n" +
                  (e.Keywords.Count > 0 ? "Keywords: " + string.Join(", ", e.Keywords) : "");
    }

    private void ShowSkyrimNetEntry()
    {
        for (var i = 0; i < 4; i++) SkyrimNetViews[i] = null;
        SkyrimNetName = SkyrimNetDescription = SkyrimNetSource = "";
        if (_ws is null || Selected is null) return;
        var row = _ws.DbRow(Selected.Entry, Gender);
        if (row is null)
        {
            SkyrimNetSource = _ws.TargetDb is null ? "No SkyrimNet database selected." : "No entry in this playthrough yet.";
            return;
        }
        SkyrimNetName = row.SuggestedName ?? "";
        SkyrimNetDescription = row.Description ?? "";
        SkyrimNetSource = string.IsNullOrEmpty(row.DescriptionModel) ? "SkyrimNet (automatic)" : row.DescriptionModel!;
        if (row.Locked) SkyrimNetSource += ", locked";
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(row.FilePathJson ?? "[]") ?? [];
            for (var i = 0; i < Math.Min(4, paths.Count); i++) SkyrimNetViews[i] = Images.FromFile(_ws.SkyrimNet.ResolveImage(paths[i]));
        }
        catch (JsonException) { }
    }

    private void LoadCatalogText()
    {
        RawReply = DescribeInfo = "";
        _generatedName = _generatedDescription = _generatedModel = "";
        IsDraft = false;
        if (_ws is null || Selected is null) { SuggestedName = Description = ""; return; }
        if (_ws.CatalogEntry(Selected.Entry, Gender) is { } c)
        {
            SuggestedName = c.SuggestedName;
            Description = c.Description;
            _generatedModel = c.Model.StartsWith("ItemNet:", StringComparison.Ordinal) ? c.Model["ItemNet:".Length..].Replace("+edited", "") : c.Model;
            _generatedName = c.Edited ? "" : c.SuggestedName;
            _generatedDescription = c.Edited ? "" : c.Description;
            IsDraft = c.Status == Core.Catalog.EntryStatus.Draft;
            DescribeInfo = IsDraft
                ? $"Draft from a batch ({_generatedModel}, {c.Updated.ToLocalTime():g}). Save to approve (Ctrl+Enter saves and moves on), or discard it."
                : $"From the ItemNet catalog ({c.Model}, {c.Updated.ToLocalTime():g}).";
        }
        else SuggestedName = Description = "";
    }

    // ---- describe ------------------------------------------------------------------------------

    // Concurrent execution stays allowed so the same button can cancel a running request.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DescribeAsync()
    {
        if (IsDescribing) { _describeCts?.Cancel(); return; }
        if (_ws is null || Selected is null) return;
        if (SelectedModel is not { } model)
        {
            MessageBox.Show("Add a vision model in Settings first (or import SkyrimNet's).", "ItemNet");
            return;
        }
        var provider = Settings.Providers.FirstOrDefault(p => p.Id == model.ProviderId);
        if (provider is null)
        {
            MessageBox.Show($"The model's provider '{model.ProviderId}' isn't configured.", "ItemNet");
            return;
        }
        if (_views is null || _viewsKey != CurrentViewsKey()) await RenderAsync();
        if (_views is null) return;

        var facts = Workspace.Facts(Selected.Entry, Gender, Mannequin == MannequinChoice.Textured, _views);
        string prompt;
        try { prompt = PromptTemplate.Render(Settings.PromptTemplate ?? PromptTemplate.Default, facts); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Prompt template"); return; }

        IsDescribing = true;
        DescribeInfo = $"Asking {model.Model}…";
        RawReply = "";
        _describeCts = new CancellationTokenSource();
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            var r = await _ws.DescribeAsync(provider, model, prompt, _views, _describeCts.Token, n =>
                dispatcher.InvokeAsync(() => DescribeInfo =
                    $"Attempt {n.Attempt} of {n.MaxAttempts} failed, retrying in {n.Delay.TotalSeconds:F0}s: {n.Reason}"));
            SuggestedName = _generatedName = r.SuggestedName;
            Description = _generatedDescription = r.Description;
            _generatedModel = model.Model;
            RawReply = FormatRawReply(r);
            DescribeInfo = $"{r.Model} in {r.Elapsed.TotalSeconds:F0}s"
                + (r.PromptTokens is { } pt ? $", {pt:N0} in / {r.CompletionTokens:N0} out tokens" : "")
                + (r.Cost is { } c ? $", ${c:F4}" : "");
        }
        catch (OperationCanceledException) { DescribeInfo = "Cancelled."; }
        catch (VisionException ex) { DescribeInfo = "Failed: " + ex.Message; RawReply = string.IsNullOrEmpty(ex.RawResponse) ? "" : "=== Full response ===\n" + VisionClient.Pretty(ex.RawResponse); }
        catch (Exception ex) { DescribeInfo = "Failed: " + ex.Message; Log.Write("Describe failed: " + ex); }
        finally { IsDescribing = false; }
    }

    /// <summary>Reasoning first (when the model gave one), then the reply text, then the whole response body.</summary>
    private static string FormatRawReply(DescriptionResult r)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.Reasoning)) sb.Append("=== Reasoning ===\n").Append(r.Reasoning.Trim()).Append("\n\n");
        sb.Append("=== Reply ===\n").Append(r.RawContent.Trim()).Append("\n\n");
        sb.Append("=== Full response ===\n").Append(r.FullResponse);
        return sb.ToString();
    }

    // ---- save ----------------------------------------------------------------------------------

    [RelayCommand]
    private void Save() => SaveCore();

    /// <summary>
    /// Saves, then moves to the item that followed in the list (review mode). Bound to Ctrl+Enter.
    /// </summary>
    [RelayCommand]
    private void SaveAndNext()
    {
        var before = VisibleRows();
        var index = Selected is null ? -1 : before.IndexOf(Selected);
        if (index < 0 || !SaveCore()) return;
        RefreshList();
        var after = VisibleRows();
        var next = index + 1 < before.Count ? before[index + 1] : null;
        Selected = next is not null && after.Contains(next) ? next : after.ElementAtOrDefault(Math.Min(index, after.Count - 1));
    }

    [RelayCommand]
    private void DiscardDraft()
    {
        if (_ws is null || Selected is null || !IsDraft) return;
        _ws.DiscardDraft(Selected.Entry, Gender);
        Selected.Refresh(_ws);
        LoadCatalogText();
        StatusText = "Draft discarded.";
    }

    private bool SaveCore()
    {
        if (_ws is null || Selected is null) return false;
        if (string.IsNullOrWhiteSpace(Description))
        {
            MessageBox.Show("There's no description to save yet.", "ItemNet");
            return false;
        }
        if (_views is null || _viewsKey != CurrentViewsKey())
        {
            MessageBox.Show("The views are out of date; wait for the render to finish.", "ItemNet");
            return false;
        }
        var existing = _ws.DbRow(Selected.Entry, Gender);
        if (existing is { DescriptionModel: "user-edited" })
        {
            var answer = MessageBox.Show(
                $"SkyrimNet already has a hand-edited description for this item:\n\n\"{existing.SuggestedName}\": {existing.Description}\n\nReplace it? (The database is backed up before the first save.)",
                "Replace edited description?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;
        }

        var edited = SuggestedName.Trim() != _generatedName || Description.Trim() != _generatedDescription;
        var model = string.IsNullOrEmpty(_generatedModel) ? "manual" : _generatedModel;
        try
        {
            var result = _ws.Save(Selected.Entry, Gender, SuggestedName.Trim(), Description.Trim(), model, edited, _views,
                Workspace.DescribeRenderSettings(CurrentSceneOptions(), Settings.RenderSize));
            Selected.Refresh(_ws);
            ShowSkyrimNetEntry();
            IsDraft = false;
            StatusText = result.Message;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Save failed: " + ex.Message, "ItemNet", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ---- settings ------------------------------------------------------------------------------

    public string? SkyrimNetConfigDir() => _ws?.SkyrimNetConfigDir();

    public async Task ApplySettingsAsync(AppSettings updated)
    {
        var reload = updated.Mode != Settings.Mode || updated.GamePath != Settings.GamePath
            || updated.Mo2Instance != Settings.Mo2Instance || updated.Mo2Profile != Settings.Mo2Profile
            || updated.CatalogFolder != Settings.CatalogFolder;
        Settings = updated;
        Settings.Save();
        LoadModels();
        if (reload || _ws is null) await ReloadAsync();
        else _ = RenderAsync();
    }

    partial void OnSelectedModelChanged(VisionModelConfig? value)
    {
        if (value is null) return;
        Settings.SelectedModel = Models.IndexOf(value);
        Settings.Save();
    }
}
