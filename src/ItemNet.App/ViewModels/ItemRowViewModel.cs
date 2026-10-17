using CommunityToolkit.Mvvm.ComponentModel;
using ItemNet.App.Services;
using ItemNet.Core.Items;
using ItemNet.Core.SkyrimNet;

namespace ItemNet.App.ViewModels;

public enum DescriptionState { None, SkyrimNetAuto, UserEdited, ItemNet }

/// <summary>One row of the item list, with where each gender's description currently comes from.</summary>
public sealed partial class ItemRowViewModel(ItemEntry entry) : ObservableObject
{
    public ItemEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Kind => Entry.Kind;
    public string Plugin => Entry.Plugin;
    public string EditorId => Entry.EditorId;
    public string Slots => Entry.SlotNames;

    [ObservableProperty] private DescriptionState _female;
    [ObservableProperty] private DescriptionState _male;

    public string FemaleText => Label(Female);
    public string MaleText => Label(Male);

    partial void OnFemaleChanged(DescriptionState value) => OnPropertyChanged(nameof(FemaleText));
    partial void OnMaleChanged(DescriptionState value) => OnPropertyChanged(nameof(MaleText));

    public void Refresh(Workspace ws)
    {
        Female = StateOf(ws.DbRow(Entry, ItemGender.Female));
        Male = StateOf(ws.DbRow(Entry, ItemGender.Male));
    }

    private static DescriptionState StateOf(OmniSightItemRow? row) => row switch
    {
        null => DescriptionState.None,
        { DescriptionModel: { } m } when m.StartsWith("ItemNet", StringComparison.Ordinal) => DescriptionState.ItemNet,
        { DescriptionModel: "user-edited" } => DescriptionState.UserEdited,
        { Description.Length: > 0 } => DescriptionState.SkyrimNetAuto,
        _ => DescriptionState.None,
    };

    private static string Label(DescriptionState s) => s switch
    {
        DescriptionState.ItemNet => "ItemNet",
        DescriptionState.UserEdited => "edited",
        DescriptionState.SkyrimNetAuto => "auto",
        _ => "",
    };
}
