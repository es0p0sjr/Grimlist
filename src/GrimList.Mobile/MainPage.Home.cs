using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private Dataset? setupDataset;
    private bool sortByPoints;
    private void RenderHome()
    {
        var body = new VerticalStackLayout { Spacing = 12, Padding = 12 };
        var source = Session.Dataset?.Source;
        body.Add(Ui.Text(source == null ? "Import a library to start building." : $"{Session.Dataset!.Game} · Edition {source.Edition}\n{source.Name} · {source.Date}", 13, colour: Ui.Muted));
        var search = Ui.Entry("Search your armies", homeQuery, "home-search"); search.IsVisible = homeQuery.Length > 0;
        search.TextChanged += (_, _) => { homeQuery = search.Text ?? ""; RenderHomeTiles(); };
        var actions = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        var create = Action("+  New army", StartNewArmy, "new-army", Ui.Accent); create.HeightRequest = 96;
        var find = Ui.Button("Search armies", () => { search.IsVisible = !search.IsVisible; if (search.IsVisible) search.Focus(); }, "find-armies");
        actions.Add(create); actions.Add(find, 1); body.Add(actions); body.Add(search);
        var sort = Ui.Button(sortByPoints ? "Sort: points" : "Sort: name", () => { sortByPoints = !sortByPoints; Render(); }, "sort-armies", Ui.Surface);
        body.Add(sort);
        homeTiles = new Grid { ColumnSpacing = 10, RowSpacing = 10 }; body.Add(homeTiles); RenderHomeTiles();
        if (homeArmies.Count == 0) body.Add(Ui.Text("Your armies will appear here. Tap + New army to begin.", 16, colour: Ui.Muted));
        SetPage(Heading("GrimList", false), Ui.Scroll(body));
    }
    private void RenderHomeTiles()
    {
        if (homeTiles == null) return;
        homeTiles.Clear(); homeTiles.RowDefinitions.Clear(); homeTiles.ColumnDefinitions.Clear();
        var columns = Width > 800 ? 3 : Width is > 0 and < 340 ? 1 : 2;
        for (var i = 0; i < columns; i++) homeTiles.ColumnDefinitions.Add(new(GridLength.Star));
        var matching = homeArmies.Where(a => $"{a.Army.Name} {a.Army.Faction} {a.Army.Detachment}".Contains(homeQuery, StringComparison.OrdinalIgnoreCase));
        matching = sortByPoints ? matching.OrderByDescending(a => a.Result.Total) : matching.OrderBy(a => a.Army.Name);
        var index = 0;
        foreach (var saved in matching)
        {
            if (index % columns == 0) homeTiles.RowDefinitions.Add(new(GridLength.Auto));
            var colour = MobilePalette.ForFaction(saved.Army.Faction);
            var tile = new Grid { RowDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
                MinimumHeightRequest = 194, Padding = 12, InputTransparent = true, CascadeInputTransparent = true };
            var name = Ui.Text(saved.Army.Name, 20); name.Margin = new Thickness(0, 0, 48, 15); name.VerticalOptions = LayoutOptions.Start;
            var info = new VerticalStackLayout { Spacing = 4 };
            info.Add(Ui.Text(saved.Army.Faction, 14)); info.Add(Ui.Text(string.Join(" / ", DetachmentRules.Selected(saved.Army)), 12));
            info.Add(Ui.Text($"{saved.Result.Total}/{saved.Army.Limit} points" + (saved.Result.Complete ? "" : " · incomplete"), 15, true));
            tile.Add(name); tile.Add(info, 0, 1);
            async Task Open() { await Session.RunAsync(s => s.Open(saved.Army.Id)); previewKey = ""; playMode = false; await Go(Screen.Editor); }
            // A native button covers the entire card, including its padding and blank space.
            // The display-only labels pass touches through; the menu remains a separate button.
            var open = Action("", Open, "open-army-" + saved.Army.Id, Colors.Transparent);
            open.Padding = 0;
            SemanticProperties.SetDescription(open, $"Open {saved.Army.Name}, {saved.Army.Faction}, {saved.Result.Total}/{saved.Army.Limit} points");
            var card = new Grid(); card.Add(tile); card.Add(open);
            var more = Action("⋯", async () =>
            {
                var action = await DisplayActionSheetAsync(saved.Army.Name, "Cancel", null, "Open", "Duplicate", "Delete");
                if (action == "Open") await Open();
                else if (action == "Duplicate") await Session.RunAsync(s => s.DuplicateArmy(saved.Army));
                else if (action == "Delete" && await DisplayAlertAsync("Delete army?", saved.Army.Name, "Delete", "Cancel"))
                    await Session.RunAsync(s => s.DeleteArmy(saved.Army.Id));
            }, "army-menu-" + saved.Army.Id, colour, true);
            more.MinimumWidthRequest = 48; more.MinimumHeightRequest = 48;
            more.HorizontalOptions = LayoutOptions.End; more.VerticalOptions = LayoutOptions.Start; more.Margin = 12;
            SemanticProperties.SetDescription(more, "Options for " + saved.Army.Name);
            card.Add(more);
            homeTiles.Add(Ui.Card(card, colour, 0), index % columns, index / columns); index++;
        }
    }
    private void RenderFactions()
    {
        var body = new VerticalStackLayout { Spacing = 12, Padding = 12 };
        foreach (var group in Session.Factions.GroupBy(MobilePalette.Group).OrderBy(g => g.Key))
        {
            body.Add(Ui.Text(group.Key, 13, colour: Ui.Muted));
            var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10, RowSpacing = 10 };
            var index = 0;
            foreach (var name in group)
            {
                if (index % 2 == 0) grid.RowDefinitions.Add(new(GridLength.Auto));
                var button = Action(name, async () => { selectedFaction = name; draftDetachments.Clear(); await Go(Screen.Setup); }, "faction-" + name, MobilePalette.ForFaction(name));
                button.MinimumHeightRequest = 70; grid.Add(button, index % 2, index / 2); index++;
            }
            body.Add(Ui.Card(grid, Ui.Surface, 10));
        }
        SetPage(Heading("New army · Faction"), Ui.Scroll(body));
    }
    private void RenderSetup()
    {
        var body = new VerticalStackLayout { Spacing = 14, Padding = 18 };
        body.Add(Ui.Text("ARMY NAME", 13, colour: Ui.Muted));
        var name = Ui.Entry("Enter army name", draftName, "army-name"); name.TextChanged += (_, _) => draftName = name.Text ?? "";
        body.Add(Ui.Card(name, Ui.Input, 4)); body.Add(Ui.Text("MAX POINTS", 13, colour: Ui.Muted));
        var limit = Ui.Entry("Points limit", draftLimit, "army-limit"); limit.Keyboard = Keyboard.Numeric;
        var detachments = new VerticalStackLayout { Spacing = 8 };
        limit.TextChanged += (_, _) => { draftLimit = limit.Text ?? ""; DrawDetachments(); };
        body.Add(limit); body.Add(Ui.Text("DETACHMENTS", 13, colour: Ui.Muted)); body.Add(detachments);
        void DrawDetachments()
        {
            detachments.Clear(); int.TryParse(draftLimit, out var points);
            var draft = DetachmentRules.WithSelection(ArmyList.New(selectedFaction) with { Limit = points }, draftDetachments);
            var usesPoints = DetachmentRules.UsesPoints(selectedFaction, setupDataset);
            if (usesPoints) detachments.Add(Ui.Text(DetachmentRules.SelectionSummary(draft, setupDataset), 14, colour: Ui.Muted));
            foreach (var option in DetachmentRules.Options(draft, setupDataset))
            {
                var selected = draftDetachments.Contains(option.Name);
                var button = Ui.Button($"{(selected ? "✓  " : "")}{option.Name}{(option.Cost.Length > 0 ? " · " + option.Cost : "")}", () =>
                {
                    if (selected) draftDetachments.Remove(option.Name);
                    else { if (!usesPoints) draftDetachments.Clear(); draftDetachments.Add(option.Name); }
                    DrawDetachments();
                }, "detachment-" + option.Name, selected ? Accent : Ui.Surface);
                button.IsEnabled = selected || !usesPoints || DetachmentRules.CanAdd(draft, option.Name, setupDataset);
                detachments.Add(button);
                detachments.Add(Ui.Text(DetachmentRules.SelectionMetadata(option, setupDataset), 13, colour: Ui.Muted));
            }
            if (detachments.Children.Count == 0) detachments.Add(Ui.Text("No detachments in this library.", colour: Ui.Muted));
        }
        DrawDetachments();
        body.Add(Toggle("Exclude Legends", hideLegends, value => { hideLegends = value; return Task.CompletedTask; }, false));
        body.Add(Action("Create army", async () =>
        {
            if (!int.TryParse(draftLimit, out var points)) throw new InvalidDataException("Enter a numeric points limit.");
            if (draftDetachments.Count == 0) throw new InvalidDataException("Choose a detachment before creating the army.");
            await Session.RunAsync(s => s.Create(selectedFaction, draftName, points, draftDetachments));
            playMode = false; unitQuery = ""; openCategories.Clear(); await Go(Screen.Editor);
        }, "create-army"));
        SetPage(Heading("New " + selectedFaction + " army"), Ui.Scroll(body));
    }
    private View Toggle(string label, bool selected, Func<bool, Task> changed, bool refresh = true)
    {
        var toggle = new Switch { IsToggled = selected, OnColor = Accent, ThumbColor = Colors.White };
        toggle.Toggled += async (_, e) => { if (rendering) return; if (refresh) await Execute(() => changed(e.Value)); else await changed(e.Value); };
        var row = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
        row.Add(Ui.Text(label, 15)); row.Add(toggle, 1); return row;
    }
    private void UpdateColumns()
    {
        if (menu is Grid overlay && overlay.Children.OfType<Border>().FirstOrDefault() is { } panel)
            panel.MaximumHeightRequest = Math.Max(120, Height - 84);
        if (homeTiles != null && screen == Screen.Home) RenderHomeTiles();
        if (editorColumns != null && !playMode && editorColumns.ColumnDefinitions.Count == 2)
        {
            editorColumns.ColumnDefinitions[0].Width = new GridLength(Width > 700 ? .8 : 1, GridUnitType.Star);
            editorColumns.ColumnDefinitions[1].Width = new GridLength(Width > 700 ? 1.2 : 1, GridUnitType.Star);
        }
    }
}
