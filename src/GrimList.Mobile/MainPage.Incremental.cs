using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private sealed record HeaderState(string Points, string Name, bool Undo, bool Redo, bool Valid, double Fraction);
    private readonly PresentationValue<HeaderState> headerState = new(new("", "", false, false, false, 0));
    private readonly PresentationValue<string> entryPrice = new("");
    private readonly PresentationValue<string> feelNoPain = new("");
    private readonly Dictionary<string, PresentationValue<LoadoutCounter>> counterStates = [];
    private readonly Dictionary<string, PresentationValue<EquipmentChoice>> choiceStates = [];
    private readonly Dictionary<string, PresentationValue<LoadoutCard>> cardStates = [];
    private readonly Dictionary<string, (string Shape, View View)> equipmentCards = [];
    private readonly Dictionary<string, (string Shape, View View)> equipmentGroups = [];
    private VerticalStackLayout? equipmentBody;
    private VerticalStackLayout? datasheetBody;
    private string renderedKey = "";
    private Dataset? renderedDataset;
    private long presentedVersion = -1;
    private UnitProfile? renderedProfile;
    private UnitProfile? renderedBaseProfile;
    private VerticalStackLayout? statlineBody;
    public long StatlineBuildCount { get; private set; }
    private List<LibraryRow>? renderedLibraryRows;
    private sealed record PickerDisplayRow(string Key, string Category, bool Opened, LibraryRow? Row, string Fingerprint);
    private sealed record RosterDisplayRow(string? Heading = null, RosterGroup? Group = null, string Key = "empty");
    private readonly ObservableCollection<PickerDisplayRow> pickerItems = [];
    private readonly ObservableCollection<PresentationValue<RosterDisplayRow>> rosterItems = [];
    private readonly Dictionary<string, (string Fingerprint, PresentationValue<RosterDisplayRow> Item)> rosterItemIndex = [];
    public long PickerRowBuildCount { get; private set; }
    public long RosterRowBuildCount { get; private set; }
    private readonly Dictionary<string, (object? Value, VerticalStackLayout Body)> datasheetRegions = [];
    public long FullPageBuildCount { get; private set; }
    public long EquipmentUpdateCount { get; private set; }
    public long EquipmentCardBuildCount { get; private set; }
    private string ViewKey => $"{screen}/{playMode}/{Session.Army.Id}/" +
        (screen == Screen.Unit ? $"{Session.Context.EntryId}/{previewKey}/{unitTab}" : "");
    private static PresentationValue<T> State<T>(Dictionary<string, PresentationValue<T>> states, string key, T value)
    {
        if (!states.TryGetValue(key, out var state)) states.Add(key, state = new(value));
        else state.Update(value);
        return state;
    }
    private void UpdateHeader()
    {
        unitSize.Update(unitView?.Entry is { } selected ? SizeState(selected.Models, unitView.Sizes) : new(0, null, null));
        feelNoPain.Update(unitView?.FeelNoPain ?? "");
        var result = Session.Result;
        headerState.Update(new($"{result.Total}/{Session.Army.Limit} points\n{Session.Army.Limit - result.Total} remaining" + (result.Complete ? "" : " · incomplete"),
            Session.Army.Name, !playMode && Session.CanUndo, !playMode && Session.CanRedo, result.Validated,
            Math.Clamp((double)result.Total / Math.Max(1, Session.Army.Limit), 0, 1)));
        if (Session.SelectedEntry is { } entry && Session.Result.Entries.FirstOrDefault(p => p.Entry.Id == entry.Id) is { } priced)
            entryPrice.Update($"{priced.Points?.ToString() ?? "?"} pts · {entry.Models} model{(entry.Models == 1 ? "" : "s")}\n{priced.Breakdown}");
    }
    private void RefreshPresentation()
    {
        CaptureScroll();
        InvalidateAttachmentChoices();
        InvalidateEnhancementChoices();
        UpdateHeader();
        var sameView = renderedKey == ViewKey && ReferenceEquals(renderedDataset, Session.Dataset);
        var incrementalDatasheet = screen == Screen.Unit && unitView?.Entry != null && datasheetBody != null && statlineBody != null;
        // The datasheet refreshes each region from the complete new projection.
        // Equipment and library previews retain their conservative rebuild path.
        if (!incrementalDatasheet && !PresentationEquality.Stats(unitView?.Unit.Profile, renderedProfile)) sameView = false;
        if (!incrementalDatasheet && presentedVersion != Session.Mutation.Version
            && (Session.Mutation.Changes & (ArmyChange.Rules | ArmyChange.Enhancement | ArmyChange.Attachments)) != 0) sameView = false;
        if (sameView && screen == Screen.Unit && unitView is { Entry: not null } view && !playMode && unitTab == "equipment" && equipmentBody != null)
            UpdateEquipment(view);
        else if (sameView && screen == Screen.Editor)
        {
            UpdateRosterItems();
            if (!ReferenceEquals(renderedLibraryRows, libraryRows)) RenderLibraryRows();
        }
        else if (sameView && screen == Screen.Unit && datasheetBody != null && unitView != null)
        {
            UpdateUnitStatlines(unitView);
            RenderDatasheet(datasheetBody, unitView);
        }
        else if (!(sameView && screen == Screen.Rules && presentedVersion == Session.Mutation.Version)) Render();
        RestoreScroll();
        renderedKey = ViewKey; renderedDataset = Session.Dataset; presentedVersion = Session.Mutation.Version;
        renderedProfile = unitView?.Unit.Profile;
    }
    private void ResetPresentation()
    {
        counterStates.Clear(); choiceStates.Clear(); cardStates.Clear(); equipmentCards.Clear(); equipmentGroups.Clear();
        datasheetRegions.Clear();
        equipmentBody = null; datasheetBody = null; statlineBody = null; renderedProfile = null; renderedBaseProfile = null;
        rosterItems.Clear(); rosterItemIndex.Clear(); pickerItems.Clear();
    }
    private static string CardShape(LoadoutCard card) => DataJson.Write(card.Blocks.Select(b => new
    {
        b.Pool, Counters = b.Counters.Select(c => c.Id).ToArray(),
        Choices = b.Choices.Select(g => new { g.Name, g.Exclusive, g.Instruction, Options = g.Choices.Select(c => (object)new { c.Model, c.GroupId, c.OptionId }).ToArray() }).ToArray()
    }).ToArray());
    private static string ChoiceKey(EquipmentChoice choice) => $"{choice.Model}/{choice.GroupId}/{choice.OptionId}";
    private static string EquipmentGroupKey(EquipmentSection group) => group.Choices.FirstOrDefault()?.GroupId ?? group.Name;
    private static string EquipmentGroupShape(EquipmentSection group) => DataJson.Write(new { group.Name, group.Instruction, group.Exclusive,
        Options = group.Choices.Select(c => new { c.Model, c.GroupId, c.OptionId }).ToArray() });
    private void UpdateEquipment(UnitView view)
    {
        EquipmentUpdateCount++;
        // Cards whose row structure is unchanged keep their controls and bindings.
        // A role/size change may add/remove option groups; only that card is replaced.
        var incoming = view.LoadoutCards.Select(c => c.Id).ToHashSet();
        foreach (var stale in equipmentCards.Keys.Where(k => !incoming.Contains(k)).ToArray())
        { equipmentBody!.Remove(equipmentCards[stale].View); equipmentCards.Remove(stale); }
        foreach (var card in view.LoadoutCards)
        {
            var shape = CardShape(card);
            var sameShape = equipmentCards.TryGetValue(card.Id, out var previous) && previous.Shape == shape;
            if (sameShape) State(cardStates, card.Id, card);
            else cardStates.Remove(card.Id); // Old bindings must not index a new, shorter block list.
            foreach (var block in card.Blocks)
            {
                foreach (var counter in block.Counters) State(counterStates, counter.Id, counter);
                foreach (var choice in block.Choices.SelectMany(g => g.Choices)) State(choiceStates, ChoiceKey(choice), choice);
            }
            if (sameShape) continue;
            var replacement = RenderLoadoutCard(view.Entry!, card);
            if (previous.View != null)
            { var index = equipmentBody!.Children.IndexOf(previous.View); equipmentBody.Children[index] = replacement; }
            else equipmentBody!.Insert(Math.Max(0, equipmentBody.Children.Count - 1), replacement);
            equipmentCards[card.Id] = (shape, replacement);
        }
        foreach (var group in view.Equipment)
        {
            foreach (var choice in group.Choices) State(choiceStates, ChoiceKey(choice), choice);
            var key = EquipmentGroupKey(group); var shape = EquipmentGroupShape(group);
            if (equipmentGroups.TryGetValue(key, out var previous) && previous.Shape != shape)
            {
                var replacement = new VerticalStackLayout { Spacing = 10 }; RenderEquipmentGroup(replacement, view.Entry!, group);
                var index = equipmentBody!.Children.IndexOf(previous.View); equipmentBody.Children[index] = replacement;
                equipmentGroups[key] = (shape, replacement);
            }
        }
        UpdateEquipmentSizes(view);
    }
    private void UpdateDatasheetRegion(VerticalStackLayout parent, string key, object? value, Action<VerticalStackLayout> render)
        => UpdateRegion(parent, key, DataJson.Write(value), render);

    private void UpdateRegion<T>(VerticalStackLayout parent, string key, T value, Action<VerticalStackLayout> render, Func<T, T, bool>? same = null)
    {
        if (!datasheetRegions.TryGetValue(key, out var region))
        { region = (null, new() { Spacing = 10 }); parent.Add(region.Body); }
        else if (region.Value is T previous && (same?.Invoke(previous, value) ?? EqualityComparer<T>.Default.Equals(previous, value))) return;
        region.Body.Clear(); render(region.Body); region.Body.IsVisible = region.Body.Children.Count > 0;
        datasheetRegions[key] = (value, region.Body);
    }

    private void UpdateUnitStatlines(UnitView view)
    {
        if (statlineBody == null) return;
        var baseline = view.EffectiveState?.BaseUnit.Profile ?? Session.Dataset?.FindUnit(view.Unit.Key)?.Profile;
        var profile = view.Unit.Profile;
        if (statlineBody.Children.Count > 0 && PresentationEquality.Stats(profile, renderedProfile)
            && PresentationEquality.Stats(baseline, renderedBaseProfile)) return;
        statlineBody.Clear(); StatlineBuildCount++;
        var lines = EffectiveDatasheetDisplay.Statlines(baseline, profile);
        for (var index = 0; index < lines.Count; index++)
            statlineBody.Add(RenderStatline(lines[index], lines.Count > 1, index,
                index > 0 || DatasheetStats.Standard(profile?.Characteristics).Count == 0));
        renderedProfile = profile; renderedBaseProfile = baseline;
    }
    private void UpdateRosterItems()
    {
        List<(string Key, string Fingerprint, RosterDisplayRow Row)> descriptors = [];
        if (rosterGroups.Count == 0)
            descriptors.Add(("empty", "empty", new()));
        foreach (var category in rosterGroups.GroupBy(g => g.Category).OrderBy(g => CategoryOrder(g.Key)))
        {
            var heading = category.Key + $" · {category.SelectMany(g => g.Entries).Sum(p => p.Points ?? 0)} pts";
            descriptors.Add(("category/" + category.Key, heading, new(Heading: heading, Key: "category/" + category.Key)));
            foreach (var group in category)
            {
                var fingerprint = DataJson.Write(new { group.Entries, Warlord = group.Entries.Any(e => e.Entry.Id == Session.Army.WarlordEntryId),
                    Grants = group.Entries.Select(e => MusterEffects.GrantedKeywords(Session.Army, e.Entry, Session.Dataset)).ToArray(),
                    Wargear = group.Entries.Select(e => rosterWargear.GetValueOrDefault(e.Entry.Id)).ToArray() });
                descriptors.Add(("entry/" + group.Root.Id, fingerprint, new(Group: group, Key: "entry/" + group.Root.Id)));
            }
        }
        var live = descriptors.Select(d => d.Key).ToHashSet();
        foreach (var key in rosterItemIndex.Keys.Where(k => !live.Contains(k)).ToArray())
        { rosterItems.Remove(rosterItemIndex[key].Item); rosterItemIndex.Remove(key); }
        for (var i = 0; i < descriptors.Count; i++)
        {
            var row = descriptors[i];
            if (!rosterItemIndex.TryGetValue(row.Key, out var existing))
            {
                existing = (row.Fingerprint, new(row.Row)); rosterItemIndex.Add(row.Key, existing); rosterItems.Insert(i, existing.Item);
            }
            else
            {
                if (existing.Fingerprint != row.Fingerprint)
                { existing.Item.Update(row.Row); rosterItemIndex[row.Key] = (row.Fingerprint, existing.Item); }
                var index = rosterItems.IndexOf(existing.Item); if (index != i) rosterItems.Move(index, i);
            }
        }
    }
    private View RenderRosterRow(RosterDisplayRow row)
    {
        RosterRowBuildCount++;
        if (row.Heading != null) return Ui.Section(row.Heading, Accent);
        if (row.Group is not { } group) return Ui.Card(new VerticalStackLayout { Spacing = 8, Children =
            { Ui.Text("Your army starts here", 17, true), Ui.Text("Open a category and tap + to add a unit.", 14, colour: Ui.Muted) } }, Ui.Surface);
        var content = new VerticalStackLayout { Spacing = 7 };
        if (group.Entries.Count > 1) content.Add(Ui.Text("ATTACHED UNIT · " + group.Entries.Sum(e => e.Points ?? 0) + " pts", 12, true));
        foreach (var priced in group.Entries) content.Add(RosterCard(priced));
        return Ui.Card(content, group.Entries.Count > 1 ? Ui.Dark(Accent) : Ui.Background, group.Entries.Count > 1 ? 7 : 0);
    }
}
