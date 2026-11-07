using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private View ArmyHeader(bool back = false)
    {
        var header = new VerticalStackLayout { Spacing = 5, Padding = new Thickness(8, 6) };
        var top = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
        if (back) top.Add(Action("‹", Back, "back", Ui.Background, true));
        var track = new Grid { BackgroundColor = Ui.Dark(Accent), MinimumHeightRequest = 48 };
        var fill = new BoxView { Color = Accent, HorizontalOptions = LayoutOptions.Start };
        track.SizeChanged += (_, _) => headerState.Bind(fill, WidthRequestProperty, h => Math.Max(0, track.Width) * h.Fraction);
        headerState.Bind(fill, WidthRequestProperty, h => Math.Max(0, track.Width) * h.Fraction);
        track.Add(fill);
        var label = Ui.Text($"{Session.Result.Total}/{Session.Army.Limit} points\n{Session.Army.Limit - Session.Result.Total} remaining" + (Session.Result.Complete ? "" : " · incomplete"), 15);
        label.HorizontalTextAlignment = TextAlignment.Center; label.AutomationId = "army-points"; track.Add(label);
        headerState.Bind(label, Label.TextProperty, h => h.Points);
        if (!playMode) { Ui.Tap(track, async () => await Execute(EditArmyLimit)); SemanticProperties.SetHint(track, "Tap to change maximum army points"); }
        top.Add(Ui.Card(track, Ui.Dark(Accent), 0), 1); top.Add(Ui.Button("☰", ToggleMenu, "menu", Ui.Background, true), 2);
        header.Add(top);
        var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 4 };
        var undo = Action("↶", () => Session.RunAsync(s => s.Undo()), "undo", Ui.Background, true); undo.IsVisible = !playMode;
        SemanticProperties.SetDescription(undo, "Undo");
        var redo = Action("↷", () => Session.RunAsync(s => s.Redo()), "redo", Ui.Background, true); redo.IsVisible = !playMode;
        SemanticProperties.SetDescription(redo, "Redo"); row.Add(undo); row.Add(redo, 1);
        headerState.Bind(undo, IsEnabledProperty, h => h.Undo); headerState.Bind(redo, IsEnabledProperty, h => h.Redo);
        var title = Action(Session.Army.Name, async () =>
        {
            var name = await DisplayPromptAsync("Army name", "Rename this army", initialValue: Session.Army.Name, maxLength: 120);
            if (name != null) await Session.RunAsync(s => s.SetMetadata(name, s.Army.Limit));
        }, "army-title", Ui.Surface);
        title.IsEnabled = !playMode; row.Add(title, 2);
        headerState.Bind(title, Button.TextProperty, h => h.Name);
        var validation = Ui.Button(Session.Result.Validated ? "✓" : "!", async () =>
            await DisplayAlertAsync(Session.Result.ValidationStatus, Session.Result.Validated ? "All current army checks pass." : string.Join("\n\n", Session.Result.Issues), "OK"),
            "validation", Session.Result.Validated ? Ui.Background : Ui.Danger, true);
        SemanticProperties.SetDescription(validation, Session.Result.ValidationStatus); row.Add(validation, 3); header.Add(row);
        headerState.Bind(validation, Button.TextProperty, h => h.Valid ? "✓" : "!");
        headerState.Bind(validation, BackgroundColorProperty, h => h.Valid ? Ui.Background : Ui.Danger);
        headerState.Bind(validation, SemanticProperties.DescriptionProperty, h => h.Valid ? "Army validated." : "Army not validated.");
        return header;
    }
    private void RenderEditor()
    {
        editorColumns = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 4, 8, 10) };
        editorColumns.ColumnDefinitions.Add(new(GridLength.Star));
        if (!playMode) editorColumns.ColumnDefinitions.Add(new(GridLength.Star));
        if (!playMode)
        {
            var left = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }, RowSpacing = 8 };
            var searchRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 4 };
            var search = Ui.Entry("Search names or keywords", unitQuery, "unit-search");
            search.TextChanged += async (_, _) =>
            {
                unitQuery = search.Text ?? ""; var version = ++searchVersion; await Task.Delay(250);
                if (version == searchVersion && !busy && screen == Screen.Editor) RenderLibraryRows();
            };
            searchRow.Add(search); searchRow.Add(Action("⋮", FilterLibrary, "library-filter", Ui.Surface, true), 1);
            libraryBody = new CollectionView { ItemsSource = pickerItems, SelectionMode = SelectionMode.None,
                ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 8 },
                ItemTemplate = VirtualRows.Template<PickerDisplayRow>(RenderPickerRow) };
            RenderLibraryRows();
            RetainScroll(libraryBody, "library", () => pickerItems.Select(r => r.Key).ToArray());
            left.Add(searchRow); left.Add(libraryBody, 0, 1); editorColumns.Add(left);
        }
        var roster = new VerticalStackLayout { Spacing = 10 };
        if (playMode)
        {
            roster.Add(Ui.Section(Session.Army.Faction, Accent));
            roster.Add(Ui.Text(string.Join(" / ", DetachmentRules.Selected(Session.Army)), 16));
            if (Session.Army.Disposition.Length > 0) roster.Add(Ui.Text(Session.Army.Disposition, 14, colour: Ui.Muted));
        }
        else
        {
            roster.Add(Ui.Text("YOUR ARMY", 12, true, Ui.Muted));
            foreach (var rule in MusterEffects.Rules(Session.Army, Session.Dataset))
                roster.Add(Action(MusterEffects.Summary(Session.Army, rule), () => Go(Screen.Rules), "muster-step-" + rule.Rule.Id, Ui.Surface));
        }
        UpdateRosterItems();
        var list = new CollectionView { ItemsSource = rosterItems, SelectionMode = SelectionMode.None,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 10 }, Header = roster,
            ItemTemplate = VirtualRows.Template<RosterDisplayRow>(RenderRosterRow, "Value") };
        RetainScroll(list, "roster", () => new[] { "header" }.Concat(rosterItems.Select(r => r.Value.Key)).ToArray());
        editorColumns.Add(list, playMode ? 0 : 1); UpdateColumns();
        SetPage(ArmyHeader(), editorColumns);
    }
    private View RosterCard(PricedEntry priced)
    {
        var entry = priced.Entry;
        var body = new VerticalStackLayout { Spacing = 6 };
        var title = Ui.Text(entry.Name, 16); title.AutomationId = "roster-entry-" + entry.Id; body.Add(title);
        body.Add(Ui.Badge($"{priced.Points?.ToString() ?? "?"} pts · {entry.Models} model{(entry.Models == 1 ? "" : "s")}", Accent));
        if (Session.Army.WarlordEntryId == entry.Id) body.Add(Ui.Badge("Warlord", Accent));
        var granted = MusterEffects.GrantedKeywords(Session.Army, entry, Session.Dataset);
        if (granted.Count > 0) body.Add(Ui.Badge("Muster: " + string.Join(", ", granted), Accent));
        if (entry.Enhancement != null)
        {
            var badge = Ui.Badge(entry.Enhancement.Name + "\n" + (priced.EnhancementPoints is { } points ? $"{points} pts" : "Points unavailable"), Accent);
            badge.AutomationId = "roster-enhancement-" + entry.Id; body.Add(badge);
        }
        foreach (var summary in rosterWargear.GetValueOrDefault(entry.Id) ?? [])
            body.Add(Ui.Badge(summary, Accent));
        if (priced.WargearPoints == null) body.Add(Ui.Text("Wargear points incomplete", 12, colour: Color.FromArgb("#DCB87D")));
        if (entry.AttachedToId.Length > 0) body.Add(Ui.Text("↳ Attached", 12, colour: Ui.Muted));
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 2, RowSpacing = 6 };
        grid.Add(body);
        RosterGestures.Attach(body, async () => await Execute(() => OpenEntry(entry.Id)),
            playMode ? null : async () => await Execute(() => RosterActions(entry.Id, body)));
        SemanticProperties.SetHint(body, playMode ? "Tap to view datasheet" : "Tap for datasheet. Hold for Delete or Duplicate.");
        if (!playMode)
        {
            var remove = Action("×", () => DeleteRosterUnit(entry.Id), "remove-" + entry.Id, Ui.Input, true);
            remove.VerticalOptions = LayoutOptions.Start; SemanticProperties.SetDescription(remove, "Remove " + entry.Name); grid.Add(remove, 1);
            var size = SizeState(entry.Models, Session.OptionsFor(entry.UnitKey, priced.Copy));
            if (size.Smaller != null || size.Larger != null)
            {
                // Keep buttons outside the card body's open/long-press gesture area.
                var stepper = UnitSizeStepper(entry.Id, entry.Name, new(size));
                grid.Add(stepper, 0, 1); Grid.SetColumnSpan(stepper, 2);
            }
        }
        return Ui.Card(grid);
    }
    private async Task RosterActions(string id, VisualElement anchor)
    {
        if (playMode || Session.Army.Entries.FirstOrDefault(e => e.Id == id) is not { } entry) return;
        var action = await RosterGestures.Menu(anchor);
        if (action == "Delete") await DeleteRosterUnit(id);
        else if (action == "Duplicate") await Session.RunAsync(s => s.DuplicateUnit(id));
    }
    private async Task DeleteRosterUnit(string id)
    {
        if (playMode || Session.Army.Entries.FirstOrDefault(e => e.Id == id) is not { } entry) return;
        if (await DisplayAlertAsync("Delete unit?", entry.Name, "Delete", "Cancel")) await Session.RunAsync(s => s.RemoveUnit(id));
    }
    private async Task OpenEntry(string id)
    {
        previewKey = ""; unitTab = "datasheet";
        await Session.RunAsync(s => s.SelectEntry(id)); await Go(Screen.Unit);
    }
    private void RenderLibraryRows()
    {
        if (libraryBody == null) return;
        var retained = scrollRegistrations.FirstOrDefault(r => r.Control == libraryBody);
        retained?.Capture();
        renderedLibraryRows = libraryRows;
        List<PickerDisplayRow> desired = [];
        var matching = libraryRows.Where(r => UnitSearch.Matches(r.Choice.Unit, unitQuery)).ToList();
        foreach (var category in matching.GroupBy(r => r.Category).OrderBy(g => CategoryOrder(g.Key)).ThenBy(g => g.Key))
        {
            var opened = unitQuery.Length > 0 || openCategories.Contains(category.Key);
            desired.Add(new("category/" + category.Key, category.Key, opened, null, category.Key + "/" + opened));
            if (!opened) continue;
            foreach (var row in category)
            {
                desired.Add(new("unit/" + row.Choice.Unit.Key, category.Key, true, row,
                    DataJson.Write(new { row.Choice.Unit.Name, row.Choice.Problem, row.Options })));
            }
        }
        if (matching.Count == 0) desired.Add(new("empty", "", false, null, "empty"));
        var live = desired.Select(r => r.Key).ToHashSet();
        for (var i = pickerItems.Count - 1; i >= 0; i--) if (!live.Contains(pickerItems[i].Key)) pickerItems.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var next = desired[i];
            var existing = pickerItems.FirstOrDefault(r => r.Key == next.Key);
            if (existing == null) pickerItems.Insert(i, next);
            else
            {
                var index = pickerItems.IndexOf(existing); if (index != i) pickerItems.Move(index, i);
                if (existing.Fingerprint != next.Fingerprint) pickerItems[i] = next;
            }
        }
        retained?.Restore();
    }
    private View RenderPickerRow(PickerDisplayRow display)
    {
        PickerRowBuildCount++;
        if (display.Key == "empty") return Ui.Text("No matching units.", 14, colour: Ui.Muted);
        if (display.Row is not { } row)
            return Ui.Button(display.Category + (display.Opened ? "  ⌃" : "  ⌄"), () =>
            { if (!openCategories.Add(display.Category)) openCategories.Remove(display.Category); RenderLibraryRows(); }, "category-" + display.Category, Accent);
        var unit = row.Choice.Unit;
        var info = new VerticalStackLayout { Spacing = 6 };
        info.Add(Ui.Text(unit.Name, 16));
        info.Add(Ui.Badge(row.Options.Count == 0 ? "Price unavailable" : $"{(row.Options.Count > 1 ? "From " : "")}{row.Options.Min(o => o.Points)} pts", Accent));
        if (row.Choice.Problem != null) info.Add(Ui.Text(row.Choice.Problem, 12, colour: Ui.Muted));
        Ui.Tap(info, async () => await Execute(async () => { previewKey = unit.Key; unitTab = "datasheet"; await Go(Screen.Unit); }));
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 2 };
        grid.Add(info);
        var add = Action("+", () => AddUnit(row), "add-" + unit.Key, Ui.Input, true);
        SemanticProperties.SetDescription(add, "Add " + unit.Name); grid.Add(add, 1);
        return Ui.Card(grid, Ui.Input, 8);
    }
    private async Task AddUnit(LibraryRow row)
    {
        var unit = row.Choice.Unit;
        if (row.Choice.Problem is { } problem) throw new InvalidDataException(problem);
        if (row.Options.Count == 0) throw new InvalidDataException(unit.Name + ": " + (unit.PointsWarning.Length > 0 ? unit.PointsWarning : "No imported points option is available."));
        var size = row.Options[0];
        if (row.Options.Count > 1)
        {
            var selected = await DisplayActionSheetAsync("Add " + unit.Name, "Cancel", null, row.Options.Select(o => o.Label).ToArray());
            var option = row.Options.FirstOrDefault(o => o.Label == selected); if (option == null) return; size = option;
        }
        await Session.RunAsync(s => s.AddUnit(unit.Key, size.Models));
        previewKey = ""; await Go(Screen.Editor);
    }
    private async Task FilterLibrary()
    {
        var action = await DisplayActionSheetAsync("Library filters", "Cancel", null, "Clear search",
            hideLegends ? "Show Legends" : "Hide Legends", hideCrucible ? "Show Crucible" : "Hide Crucible", fitsRemaining ? "Show all prices" : "Fits remaining points");
        switch (action)
        {
            case "Clear search": unitQuery = ""; break;
            case "Show Legends": case "Hide Legends": hideLegends = !hideLegends; break;
            case "Show Crucible": case "Hide Crucible": hideCrucible = !hideCrucible; break;
            case "Show all prices": case "Fits remaining points": fitsRemaining = !fitsRemaining; break;
        }
    }
}
