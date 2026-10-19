using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ItemNet.App.Services;
using ItemNet.Core.SkyrimNet;
using ItemNet.Vision;

namespace ItemNet.App.ViewModels;

/// <summary>
/// Runs render + describe over the main list's selected rows (when more than one is selected) or
/// its current rows (search and filter applied), saving drafts for review or writing straight to
/// SkyrimNet.
/// </summary>
public sealed partial class BatchViewModel : ObservableObject
{
    private const int MaxLogLines = 1000;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _clock = new();
    private int _total, _finished;
    private double _cost;
    private int _costed;

    public ObservableCollection<string> LogLines { get; } = [];
    public IReadOnlyList<BatchGender> Genders { get; } = Enum.GetValues<BatchGender>();
    public IReadOnlyList<int> ConcurrencyChoices { get; } = Enumerable.Range(1, 16).ToList();
    public ObservableCollection<VisionModelConfig> Models { get; } = [];
    public List<BatchJob> Failed { get; } = [];

    [ObservableProperty] private BatchGender _gender = BatchGender.Female;
    [ObservableProperty] private bool _skipDone = true;
    [ObservableProperty] private bool _skipHandEdited = true;
    [ObservableProperty] private bool _writeToSkyrimNet;
    [ObservableProperty] private int _concurrency = 3;
    [ObservableProperty] private VisionModelConfig? _selectedModel;
    [ObservableProperty] private string _scopeText = "";
    [ObservableProperty] private string _renderText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _costText = "";
    [ObservableProperty] private int _drafted;
    [ObservableProperty] private int _saved;
    [ObservableProperty] private int _failedCount;

    public BatchViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var m in main.Models) Models.Add(m);
        SelectedModel = main.SelectedModel ?? Models.FirstOrDefault();
        var prefs = main.Settings.Batch;
        _concurrency = Math.Clamp(prefs.Concurrency, 1, 16);
        _gender = prefs.Gender;
        _skipDone = prefs.SkipDone;
        _skipHandEdited = prefs.SkipHandEdited;
        UpdateScope();
    }

    partial void OnGenderChanged(BatchGender value) { _main.Settings.Batch.Gender = value; SavePrefs(); }
    partial void OnSkipDoneChanged(bool value) { _main.Settings.Batch.SkipDone = value; SavePrefs(); }
    partial void OnSkipHandEditedChanged(bool value) { _main.Settings.Batch.SkipHandEdited = value; SavePrefs(); }
    partial void OnConcurrencyChanged(int value) { _main.Settings.Batch.Concurrency = value; SavePrefs(); }

    private void SavePrefs()
    {
        _main.Settings.Save();
        UpdateScope();
    }

    /// <summary>Recounts the jobs; the main list may have been filtered differently since.</summary>
    public void UpdateScope()
    {
        if (IsRunning) return;
        var rows = _main.BatchRows(out var fromSelection).Count;
        var jobs = BuildJobs(out var skipped);
        ScopeText = $"{jobs.Count:N0} to do from the {rows:N0} " + (fromSelection ? "selected items" : "items the list shows")
            + (skipped > 0 ? $" ({skipped:N0} skipped)" : "") + ".";
        RenderText = $"Renders with the main window's settings: {_main.MannequinLabel} mannequin, weight {_main.Weight:0.00}, {_main.Settings.RenderSize} px.";
    }

    private List<BatchJob> BuildJobs(out int skipped)
    {
        skipped = 0;
        var jobs = new List<BatchJob>();
        if (_main.Workspace is not { } ws) return jobs;
        ItemGender[] genders = Gender switch
        {
            BatchGender.Female => [ItemGender.Female],
            BatchGender.Male => [ItemGender.Male],
            _ => [ItemGender.Female, ItemGender.Male],
        };
        foreach (var row in _main.BatchRows(out _))
        {
            var e = row.Entry;
            if (!e.HasFemaleModel && !e.HasMaleModel) continue;
            foreach (var g in genders)
            {
                var db = ws.DbRow(e, g);
                var done = ws.CatalogEntry(e, g) is not null || db?.DescriptionModel?.StartsWith("ItemNet", StringComparison.Ordinal) == true;
                if ((SkipDone && done) || (SkipHandEdited && db?.DescriptionModel == "user-edited")) { skipped++; continue; }
                jobs.Add(new BatchJob(e, g));
            }
        }
        return jobs;
    }

    [RelayCommand]
    private Task StartAsync() => RunAsync(BuildJobs(out _));

    [RelayCommand]
    private Task RetryFailedAsync() => RunAsync(Failed.ToList());

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        Append("Stopping: requests in flight are abandoned; finished items are already saved.");
    }

    private async Task RunAsync(List<BatchJob> jobs)
    {
        if (IsRunning || _main.Workspace is not { } ws) return;
        if (jobs.Count == 0) { Append("Nothing to do."); return; }
        if (SelectedModel is not { } model) { MessageBox.Show("Add a vision model in Settings first.", "ItemNet"); return; }
        var provider = _main.Settings.Providers.FirstOrDefault(p => p.Id == model.ProviderId);
        if (provider is null) { MessageBox.Show($"The model's provider '{model.ProviderId}' isn't configured.", "ItemNet"); return; }
        if (WriteToSkyrimNet && ws.TargetDb is null) { MessageBox.Show("No SkyrimNet database is selected.", "ItemNet"); return; }

        var settings = new BatchSettings
        {
            Provider = provider, Model = model,
            PromptTemplate = _main.Settings.PromptTemplate ?? PromptTemplate.Default,
            Scene = _main.BatchSceneOptions(), RenderSize = _main.Settings.RenderSize,
            Concurrency = Math.Clamp(Concurrency, 1, 16), WriteToSkyrimNet = WriteToSkyrimNet,
        };

        IsRunning = true;
        Failed.Clear();
        _total = jobs.Count;
        _finished = Drafted = Saved = FailedCount = _costed = 0;
        _cost = 0;
        _clock.Restart();
        UpdateProgress();
        Append($"Starting {jobs.Count:N0} items with {model.Model}, {settings.Concurrency} at a time, " +
               (WriteToSkyrimNet ? $"writing to {ws.TargetDb!.Name}." : "saving drafts for review."));
        Log.Write($"Batch start: {jobs.Count} jobs, {model.Model}, concurrency {settings.Concurrency}, writeThrough={WriteToSkyrimNet}");

        _cts = new CancellationTokenSource();
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            await BatchRunner.RunAsync(ws, jobs, settings,
                action => dispatcher.InvokeAsync(action).Task,
                ev => dispatcher.InvokeAsync(() => OnEvent(ev)),
                _cts.Token);
            Append($"Done: {Drafted + Saved:N0} described, {FailedCount:N0} failed, in {Duration(_clock.Elapsed)}.");
        }
        catch (OperationCanceledException) { Append($"Stopped after {_finished:N0} of {_total:N0}."); }
        catch (Exception e) { Append("Batch failed: " + e.Message); }
        finally
        {
            _clock.Stop();
            IsRunning = false;
            _main.RefreshList();
            UpdateScope();
            Log.Write($"Batch end: {_finished}/{_total} finished, {FailedCount} failed, cost {_cost:F4}");
        }
    }

    private void OnEvent(BatchEvent ev)
    {
        switch (ev.Outcome)
        {
            case null:
                Append($"↻ {ev.Job}: {ev.Message}");
                return;
            case BatchOutcome.Failed:
                FailedCount++;
                Failed.Add(ev.Job);
                Append($"✗ {ev.Job}: {ev.Message}");
                break;
            default:
                if (ev.Outcome == BatchOutcome.Saved) Saved++; else Drafted++;
                var note = ev.Outcome == BatchOutcome.KeptAsDraft ? " (kept as a draft: SkyrimNet's entry is hand-edited)" : "";
                Append($"✓ {ev.Job} → \"{ev.Message}\"{(ev.Cost is { } c ? $"  ${c:F4}" : "")}{note}");
                if (ev.Cost is { } cost) { _cost += cost; _costed++; }
                break;
        }
        _finished++;
        _main.RefreshRow(ev.Job.Item);
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        Progress = _total == 0 ? 0 : 100.0 * _finished / _total;
        var remaining = _total - _finished;
        var eta = _finished == 0 ? "" : $", about {Duration(TimeSpan.FromSeconds(_clock.Elapsed.TotalSeconds / _finished * remaining))} left";
        ProgressText = $"{_finished:N0} of {_total:N0} ({Drafted + Saved:N0} done, {FailedCount:N0} failed), {Duration(_clock.Elapsed)} elapsed{eta}";
        CostText = _costed > 0 ? $"Cost: ${_cost:F4} so far, ${_cost / _costed:F4} per item, about ${_cost / _costed * remaining:F2} for the rest"
            : Drafted + Saved > 0 ? "Cost: this provider doesn't report it"
            : "Cost: not reported yet";
    }

    /// <summary>h:mm:ss, with days in front once there are any ("h" alone is the hours part, 0-23).</summary>
    private static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t:hh\\:mm\\:ss}" : $"{t:h\\:mm\\:ss}";

    private void Append(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }
}
