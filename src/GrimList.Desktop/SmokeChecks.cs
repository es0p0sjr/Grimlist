using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T Find<T>(FrameworkElement owner, string name) where T : FrameworkElement => (T)owner.FindName(name);
    private static void Click(FrameworkElement owner, string name)
    {
        Find<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (owner is ImportWindow import) PumpUntil(import.PendingOperation);
    }
    private static ComboBox? OpenAttachmentPicker(ComboBox? picker)
    {
        if (picker is not DeferredAttachmentPicker deferred) return picker;
        picker.IsDropDownOpen = true;
        PumpUntil(picker.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task);
        PumpUntil(deferred.Loading);
        picker.IsDropDownOpen = false;
        return picker;
    }
    private static void PumpUntil(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var began = System.Diagnostics.Stopwatch.StartNew();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted || began.Elapsed > TimeSpan.FromSeconds(30)) frame.Continue = false; };
            timer.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        Check(task.IsCompleted, "Background operation timed out");
        task.GetAwaiter().GetResult();
    }
    private static Button FindTaggedButton(FrameworkElement owner, string tag)
    {
        // Smoke actions run synchronously. A handler can replace ItemsSource before
        // WPF gets its next layout pass, leaving the new item templates unrealized.
        (Window.GetWindow(owner) ?? owner).UpdateLayout();
        var matches = Descendants<Button>(owner).Where(b => b.Tag as string == tag).ToList();
        Check(matches.Count == 1, $"Expected one button tagged '{tag}' in '{owner.Name}' after layout; found {matches.Count}.");
        return matches[0];
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    public static void Run(MainWindow window, string directory, string imagePath)
    {
        CheckWindowsPerformance(window, directory, imagePath);
        CheckStatlines(window, directory, imagePath);
        CheckDecimus(window, directory, imagePath);
        CheckPlaytesterFixes(window, directory, imagePath);
        CheckEffectiveDatasheets(window, directory, imagePath);
        CheckWeaponRules(window, directory, imagePath);
        CheckWarlordRestrictions(window, directory, imagePath);
        CheckNominations(window, directory, imagePath);
        CheckMenus(window, directory, imagePath);
        CheckDatasheetReferences(window, directory, imagePath);
        CheckGroupedReferences(window, directory, imagePath);
        CheckEnhancementDefences(window, directory, imagePath);
        using var store = WindowsServices.OpenStore(directory);
        var beforeCount = store.LoadArmies().Count;
        var setup = Find<ArmySetupView>(window, "Setup");
        var home = Find<HomeView>(window, "Home");
        Check(home.Visibility == Visibility.Visible, "App must start on army tiles.");
        Capture(window, imagePath + ".home.png");
        Click(home, "NewArmyTile");
        Check(setup.Step == 0 && setup.Visibility == Visibility.Visible, "New army must start on factions.");
        Check(Find<Grid>(window, "BuilderScreen").Visibility == Visibility.Collapsed, "Roster must not be visible during setup.");
        Click(setup, "NextButton"); Check(setup.Step == 0, "Cannot continue without an army.");
        Capture(window, imagePath + ".army.png");
        var data = store.LoadDataset();
        Find<ListBox>(setup, "FactionList").SelectedValue = data?.Units.First().Faction ?? "Space Marines";
        Click(setup, "NextButton"); Check(setup.Step == 1, "Detachment screen missing.");
        Click(setup, "NextButton"); Check(setup.Step == 1, "Cannot continue without a detachment.");
        var detachments = Find<ListBox>(setup, "DetachmentList");
        var firstFaction = (string)Find<ListBox>(setup, "FactionList").SelectedValue;
        var otherNames = (data?.Detachments ?? []).Where(d => d.Faction != firstFaction).Select(d => d.Name);
        Check(!detachments.Items.Cast<string>().Any(otherNames.Contains), "Detachments from another faction leaked into selection.");
        if (detachments.Items.Count > 0) detachments.SelectedIndex = 0;
        else Find<TextBox>(setup, "DetachmentName").Text = "UI test detachment";
        var detachment = Find<TextBox>(setup, "DetachmentName").Text;
        Capture(window, imagePath + ".detachment.png");
        Click(setup, "NextButton"); Check(setup.Step == 2, "Name screen missing.");
        Click(setup, "NextButton"); Check(setup.Step == 2, "Cannot continue with a blank name.");
        Find<TextBox>(setup, "NameInput").Text = "UI workflow check";
        Find<TextBox>(setup, "LimitInput").Text = "-1";
        Check(!Find<Button>(setup, "NextButton").IsEnabled, "Invalid points limit accepted.");
        Find<TextBox>(setup, "LimitInput").Text = "2000";
        Click(setup, "BackButton"); Check(setup.Step == 1, "Back did not return to Detachment.");
        Check(Find<TextBox>(setup, "DetachmentName").Text == detachment, "Back lost detachment selection.");
        Click(setup, "NextButton");
        Check(Find<TextBox>(setup, "NameInput").Text == "UI workflow check", "Back lost the army name.");
        Check(store.LoadArmies().Count == beforeCount, "Incomplete setup must not save a draft army.");
        Capture(window, imagePath + ".name.png");
        Click(setup, "NextButton");
        Check(setup.Visibility == Visibility.Collapsed && Find<Grid>(window, "BuilderScreen").Visibility == Visibility.Visible, "Name completion did not open roster.");
        var created = store.LoadArmies().First(a => a.Name == "UI workflow check");
        Check(created.Detachment == detachment && store.LoadArmies().Count == beforeCount + 1, "Army and detachment were not saved.");
        if (Find<Button>(window, "AddButton").IsEnabled)
        {
            var before = Find<TextBlock>(window, "RosterCount").Text;
            Click(window, "AddButton"); Check(Find<TextBlock>(window, "RosterCount").Text != before, "Add did not update roster.");
            Click(window, "UndoButton"); Check(Find<TextBlock>(window, "RosterCount").Text == before, "Undo did not restore roster.");
            Click(window, "RedoButton"); Check(Find<TextBlock>(window, "RosterCount").Text != before, "Redo did not restore the unit.");
            Check(window.FindName("EntryNotes") == null && window.FindName("UnitNotesSection") == null && window.FindName("LoadoutWarningsSection") == null,
                "Removed source and unit notes sections must not remain in the UI.");
            var sizePicker = Find<ComboBox>(window, "EntrySizePicker");
            if (sizePicker.Items.Count > 1)
            {
                sizePicker.SelectedIndex = sizePicker.Items.Count - 1;
                Check(store.LoadArmies().Single(a => a.Id == created.Id).Entries.Last().Models > 5, "Detail composition did not resize unit.");
                Click(window, "DecreaseSizeButton");
                Check(store.LoadArmies().Single(a => a.Id == created.Id).Entries.Last().Models == 5, "Previous size control did not resize unit.");
                Click(window, "IncreaseSizeButton");
                Check(store.LoadArmies().Single(a => a.Id == created.Id).Entries.Last().Models > 5, "Next size control did not resize unit.");
            }
        }
        Capture(window, imagePath);
        Click(window, "RulesButton"); Check(Find<Grid>(window, "RulesScreen").Visibility == Visibility.Visible, "Rules screen missing.");
        Check(!Find<ItemsControl>(window, "ArmyRuleCards").Items.Cast<ReferenceCard>().Any(c => c.Faction != firstFaction), "Rules from another faction leaked.");
        Capture(window, imagePath + ".rules.png"); Click(window, "CloseRulesButton");
        Click(window, "NewArmyButton"); Check(home.Visibility == Visibility.Visible, "Back must return to army tiles.");
        Click(home, "NewArmyTile"); Check(setup.Step == 0, "New army must start at Select army.");
        Find<ListBox>(setup, "FactionList").SelectedValue = firstFaction;
        Click(setup, "NextButton"); Find<TextBox>(setup, "DetachmentName").Text = "Discarded draft";
        Click(setup, "CancelButton");
        Check(home.Visibility == Visibility.Visible && store.LoadArmies().Count == beforeCount + 1, "Cancel created a phantom army.");
        var tile = Find<WrapPanel>(home, "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList a && a.Id == created.Id);
        tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(setup.Visibility == Visibility.Collapsed && Find<TextBox>(window, "ArmyName").Text == created.Name &&
            store.LoadArmies().Single(a => a.Id == created.Id).Detachment == detachment,
            "Saved army did not reopen its detachment.");
        if (data != null)
        {
            var import = new ImportWindow(store) { Owner = window };
            Find<TextBox>(import, "RawText").Text = DataJson.Write(data);
            string? failure = null;
            import.ContentRendered += async (_, _) =>
            {
                try
                {
                    Click(import, "PreviewButton");
                    Check(Find<Button>(import, "ApplyButton").IsEnabled, Find<TextBlock>(import, "Summary").Text);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Capture(import, imagePath + ".import.png");
                }
                catch (Exception ex) { failure = ex.Message; }
                finally { import.DialogResult = false; }
            };
            import.ShowDialog(); Check(failure == null, failure ?? "");
            var original = store.LoadDataset()!;
            var first = original.Units.First();
            var pointsUpdate = new Dataset(original.Source with { Version = "UI points update" }, [new(first.Faction, first.Name, first.Options.Select(o => o with { Points = o.Points + 5 }).ToList())]);
            var apply = new ImportWindow(store) { Owner = window };
            Find<TextBox>(apply, "RawText").Text = DataJson.Write(pointsUpdate);
            apply.ContentRendered += (_, _) =>
            {
                try
                {
                    Click(apply, "PreviewButton");
                    Check(Find<Button>(apply, "ApplyButton").IsEnabled, Find<TextBlock>(apply, "Summary").Text);
                    Find<TextBox>(apply, "VersionBox").Text = "UI reviewed update";
                    Check(!Find<Button>(apply, "ApplyButton").IsEnabled, "Metadata edits must invalidate the review.");
                    Click(apply, "PreviewButton"); Click(apply, "ApplyButton");
                    Check(apply.DialogResult == true, Find<TextBlock>(apply, "Summary").Text);
                }
                catch (Exception ex) { failure = ex.Message; apply.DialogResult = false; }
            };
            apply.ShowDialog(); Check(failure == null, failure ?? "");
            var updated = store.LoadDataset()!.FindUnit(first.Key)!;
            Check(updated.Options[0].Points == first.Options[0].Points + 5, "UI did not apply new prices.");
            Check(DataJson.Write(updated.Profile) == DataJson.Write(first.Profile), "Points UI erased a datasheet.");
            Check(updated.PointsSource?.Version == "UI reviewed update", "Reviewed source metadata was not saved.");
            var fixturePath = Path.Combine(Path.GetDirectoryName(directory)!, "bsdata-schema-fixture.zip");
            if (File.Exists(fixturePath))
            {
                // Reproduce a real points-only library: the UI must explain the missing sheet,
                // and the repair import must preserve prices even when the catalogue differs.
                var fixtureBytes = File.ReadAllBytes(fixturePath);
                var fullFixture = BsDataImporter.Import(BsDataDocument.ReadArchive(fixtureBytes), new SourceInfo("UI catalogue fixture", "1", "", "", Edition: "11")).Dataset;
                var pointsOnly = fullFixture with { Units = fullFixture.Units.Select(u => u with { Profile = null, ProfileSource = null, Category = "Units", Options = u.Options.Select(o => o with { Points = o.Points + 13 }).ToList() }).ToList() };
                store.ApplyImport(store.PreviewImport(pointsOnly, ImportLayer.Points).Dataset);
                var missingArmy = ArmyList.New("Death Guard") with { Name = "Missing datasheet fixture" };
                store.Save(missingArmy);
                var missingWindow = new MainWindow(directory) { Owner = window };
                missingWindow.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        var missingHome = Find<HomeView>(missingWindow, "Home");
                        var missingTile = Find<WrapPanel>(missingHome, "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList a && a.Id == missingArmy.Id);
                        missingTile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(Find<Border>(missingWindow, "MissingDatasheet").Visibility == Visibility.Visible, "A points-only unit needs an explicit missing datasheet message.");
                        Check(Find<TextBlock>(missingWindow, "MissingDatasheetText").Text.Contains("Only points"), "Missing sheet explanation is incorrect.");
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Capture(missingWindow, imagePath + ".missing-datasheet.png");
                        _ = missingWindow.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            var repair = System.Windows.Application.Current.Windows.OfType<ImportWindow>().Single(w => w.Owner == missingWindow);
                            try { Check(Find<ComboBox>(repair, "LayerPicker").SelectedIndex == 1, "Import datasheets action must preserve points."); }
                            catch (Exception ex) { failure = ex.Message; }
                            finally { repair.DialogResult = false; }
                        }));
                        Click(missingWindow, "ImportDatasheetsButton");
                    }
                    catch (Exception ex) { failure = ex.Message; }
                    finally { missingWindow.Close(); }
                };
                missingWindow.ShowDialog(); Check(failure == null, failure ?? "");
                var repairImport = new ImportWindow(store, initialLayer: ImportLayer.Profiles) { Owner = window };
                repairImport.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        Find<TextBox>(repairImport, "RawText").Text = DataJson.Write(pointsOnly);
                        Click(repairImport, "PreviewButton");
                        Check(!Find<Button>(repairImport, "ApplyButton").IsEnabled && Find<TextBlock>(repairImport, "Summary").Text.Contains("points-only cache"), "An old cache must explain why it cannot supply sheets.");
                        await repairImport.LoadCommunity(BsDataDocument.ReadArchive(fixtureBytes), fixtureBytes);
                        Check(Find<ComboBox>(repairImport, "LayerPicker").SelectedIndex == 1, "Loading the ZIP must retain datasheets-only mode.");
                        Click(repairImport, "PreviewButton"); Click(repairImport, "ApplyButton");
                        Check(repairImport.DialogResult == true, Find<TextBlock>(repairImport, "Summary").Text);
                    }
                    catch (Exception ex) { failure = ex.Message; repairImport.DialogResult = false; }
                };
                repairImport.ShowDialog(); Check(failure == null, failure ?? "");
                var repairedHero = store.LoadDataset()!.Units.Single(u => u.Name == "Typhus");
                Check(repairedHero.Options[0].Points == 113 && repairedHero.Profile?.Characteristics?.Count == 7 && repairedHero.Category == "Epic Heroes", "Datasheet repair must fill profiles/categories and retain current prices.");
                var community = new ImportWindow(store) { Owner = window };
                Check(Find<ComboBox>(community, "Provider").SelectedIndex == 0, "BSData must be the default provider.");
                community.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(fixturePath);
                        Find<ComboBox>(community, "DownloadEdition").SelectedIndex = 1;
                        await community.LoadCommunity(BsDataDocument.ReadArchive(bytes), bytes);
                        Check(Find<ComboBox>(community, "LayerPicker").SelectedIndex == 2, "BSData must default to points and datasheets.");
                        Check(Find<CheckBox>(community, "CompleteFaction").IsChecked == false, "BSData must preserve units outside the import scope.");
                        Click(community, "PreviewButton");
                        Check(Find<Button>(community, "ApplyButton").IsEnabled, Find<TextBlock>(community, "Summary").Text);
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Capture(community, imagePath + ".bsdata.png");
                        Click(community, "ApplyButton");
                        Check(community.DialogResult == true, "BSData review did not apply.");
                    }
                    catch (Exception ex) { failure = ex.Message; community.DialogResult = false; }
                };
                community.ShowDialog(); Check(failure == null, failure ?? "");
                Check(store.LoadDataset()!.Units.Count(u => u.Faction == "Death Guard") == 4, "Death Guard points missing after UI apply.");
                Check(DataJson.Write(store.LoadDataset()!.FindUnit(first.Key)!.Profile) == DataJson.Write(first.Profile), "BSData erased another faction's profile.");
                var imported = store.LoadDataset()!.Units.Single(u => u.Name == "Typhus");
                Check(imported.Category == "Epic Heroes" && imported.Profile?.Weapons?.Count > 0, "BSData category or datasheet was not saved.");
                var commander = new UnitDefinition("Death Guard", "Mortarion", [new(1, 375)], Category: "Epic Heroes",
                    Profile: new(new() { ["M"] = "10\"", ["T"] = "12", ["Sv"] = "2+", ["W"] = "16", ["LD"] = "5+", ["OC"] = "6", ["InSv"] = "4+" },
                        Notes: "Warlord UI fixture for testing only.", Keywords: ["Character", "Epic Hero"], MustBeWarlord: true));
                var legends = new UnitDefinition("Death Guard", "Filter fixture [Legends]", [new(1, 10)]);
                var crucible = new UnitDefinition("Death Guard", "Filter fixture [Crucible]", [new(1, 10)]);
                store.ApplyImport(store.PreviewImport(new(new("Warlord UI fixture", "1", "", "", Edition: "11"), [commander, legends, crucible]), ImportLayer.Combined).Dataset);
                var loadoutFixture = Path.Combine(Path.GetDirectoryName(directory)!, "loadout-fixture.json");
                if (File.Exists(loadoutFixture)) store.Import(DataJson.Read<Dataset>(File.ReadAllText(loadoutFixture)));
                var leaderFixture = Path.Combine(Path.GetDirectoryName(directory)!, "leader-fixture.json");
                if (File.Exists(leaderFixture)) store.Import(DataJson.Read<Dataset>(File.ReadAllText(leaderFixture)));
                var effectFixture = Path.Combine(Path.GetDirectoryName(directory)!, "effect-fixture.json");
                if (File.Exists(effectFixture)) store.Import(DataJson.Read<Dataset>(File.ReadAllText(effectFixture)));
                var referenceFixture = Path.Combine(Path.GetDirectoryName(directory)!, "reference-fixture.json");
                if (File.Exists(referenceFixture)) store.Import(DataJson.Read<Dataset>(File.ReadAllText(referenceFixture)));
                var datasheetArmy = ArmyList.New("Death Guard") with { Name = "Datasheet UI fixture" };
                store.Save(datasheetArmy);
                var sheetWindow = new MainWindow(directory) { Owner = window };
                sheetWindow.ContentRendered += async (_, _) =>
                {
                    try
                    {
                        var sheetHome = Find<HomeView>(sheetWindow, "Home");
                        var armyTile = Find<WrapPanel>(sheetHome, "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList a && a.Id == datasheetArmy.Id);
                        armyTile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var picker = Find<ListBox>(sheetWindow, "UnitPicker");
                        Check(((UnitDefinition)picker.SelectedItem).DisplayCategory == "Epic Heroes", "Epic Heroes must be first in the library.");
                        var hideLegends = Find<CheckBox>(sheetWindow, "HideLegends");
                        var hideCrucible = Find<CheckBox>(sheetWindow, "HideCrucible");
                        var search = Find<TextBox>(sheetWindow, "SearchBox");
                        search.Text = "Filter fixture";
                        Check(hideCrucible.IsChecked == true && picker.Items.Count == 1 && ((UnitDefinition)picker.Items[0]).Name == legends.Name, "Crucible units must be hidden by default.");
                        hideCrucible.IsChecked = false;
                        Check(picker.Items.Count == 2, "Special unit fixtures must appear before filtering.");
                        picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == legends.Name);
                        Click(sheetWindow, "AddButton");
                        hideLegends.IsChecked = true;
                        Check(picker.Items.Count == 1 && ((UnitDefinition)picker.Items[0]).Name == crucible.Name, "Hide Legends must leave Crucible units available.");
                        Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Single().Name == legends.Name, "Hiding a unit must preserve existing roster entries.");
                        hideCrucible.IsChecked = true;
                        Check(picker.Items.Count == 0 && Find<StackPanel>(sheetWindow, "LibraryEmpty").Visibility == Visibility.Visible, "Both filters must work together with search and show the empty state.");
                        hideLegends.IsChecked = false;
                        Check(picker.Items.Count == 1 && ((UnitDefinition)picker.Items[0]).Name == legends.Name, "Hide Crucible must leave Legends units available.");
                        hideCrucible.IsChecked = false;
                        Check(picker.Items.Count == 2, "Turning off filters must restore matching units.");
                        Click(sheetWindow, "UndoButton");
                        search.Clear();
                        picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Typhus");
                        Check(Find<ItemsControl>(sheetWindow, "Stats").Items.Count == 6, "Invulnerable save must leave the main stat row.");
                        Check(Find<TextBlock>(sheetWindow, "InvulnerableValue").Text == "4+", "Separate invulnerable save badge missing.");
                        Check(Find<WrapPanel>(sheetWindow, "DefensiveStats").Margin.Left == 100, "Defensive saves must align under SV.");
                        Check(Find<DataGrid>(sheetWindow, "MeleeWeaponTable").Items.Count == 1, "Shared melee weapon missing in UI.");
                        Check(Find<StackPanel>(sheetWindow, "MeleeWeaponsSection").Visibility == Visibility.Visible, "Melee weapons section missing in UI.");
                        Check(Find<StackPanel>(sheetWindow, "RangedWeaponsSection").Visibility == Visibility.Collapsed, "Melee-only units must hide the ranged weapons section.");
                        Check(Find<ItemsControl>(sheetWindow, "AbilityCards").Items.Count == 2, "Abilities missing in UI.");
                        Check(Find<TextBlock>(sheetWindow, "UnitKeywords").Text.Contains("Epic Hero"), "Keywords missing in UI.");
                        Check(Find<Border>(sheetWindow, "MissingDatasheet").Visibility == Visibility.Collapsed, "Imported datasheets must hide the missing sheet prompt.");
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Capture(sheetWindow, imagePath + ".weapons.png");
                        Click(sheetWindow, "AddButton");
                        Check(Find<ListBox>(sheetWindow, "Roster").Items.Groups![0] is System.Windows.Data.CollectionViewGroup group && group.Name.ToString() == "Epic Heroes", "Roster grouping did not use the unit category.");
                        var heroId = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries[0].Id;
                        var warlordPicker = Find<ComboBox>(sheetWindow, "WarlordPicker");
                        warlordPicker.SelectedValue = heroId;
                        Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).WarlordEntryId == heroId, "Manual warlord did not persist.");
                        picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Mortarion");
                        Click(sheetWindow, "AddButton");
                        var commanded = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id);
                        Check(commanded.Entries.Single(e => e.Name == "Mortarion").Id == commanded.WarlordEntryId, "Mortarion must become warlord automatically.");
                        Check(!warlordPicker.IsEnabled, "A single mandatory warlord must lock manual selection.");
                        Click(sheetWindow, "UndoButton");
                        Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).WarlordEntryId == heroId, "Undo must restore the manual warlord.");
                        Click(sheetWindow, "RedoButton");
                        Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).WarlordEntryId == commanded.WarlordEntryId, "Redo must restore the mandatory warlord.");
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Capture(sheetWindow, imagePath + ".datasheet.png");
                        if (File.Exists(loadoutFixture))
                        {
                            picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Loadout fixture squad");
                            Click(sheetWindow, "AddButton");
                            var entry = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last();
                            Check(entry.Loadout?.Models.Count == 5, "Adding a unit must save its default loadout.");
                            sheetWindow.UpdateLayout();
                            Check(Find<StackPanel>(sheetWindow, "LoadoutCards").Children.OfType<Expander>().Count() == 2, "Loadout editor must group the champion and squad rather than create a card per model.");
                            Check(!Descendants<ComboBox>(Find<StackPanel>(sheetWindow, "LoadoutCards")).Any(), "Model-by-model selectors must be removed.");
                            var plasma = Descendants<Button>(Find<StackPanel>(sheetWindow, "LoadoutCards")).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Plasma gun");
                            Check(plasma.IsEnabled, "Available equipment choice must be enabled.");
                            plasma.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var changed = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last();
                            Check(changed.Loadout!.Models[0].Equipment.Any(s => s.OptionId == "plasma"), "Equipment choice must save immediately.");
                            Click(sheetWindow, "UndoButton");
                            Check(!store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Loadout!.Models[0].Equipment.Any(s => s.OptionId == "plasma"), "Undo must restore the previous loadout.");
                            Click(sheetWindow, "RedoButton");
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            Capture(sheetWindow, imagePath + ".loadout.png");
                            Click(sheetWindow, "PlayModeButton");
                            Check(Find<Grid>(sheetWindow, "LibraryPanel").Visibility == Visibility.Collapsed && Find<ScrollViewer>(sheetWindow, "LoadoutEditor").Visibility == Visibility.Collapsed, "Play mode must hide library and editor.");
                            var shown = Find<DataGrid>(sheetWindow, "RangedWeaponTable").Items.Cast<WeaponProfile>().ToList();
                            Check(shown.Count == 3 && shown.All(w => !w.Name.Contains("Heavy gun")), "Play mode must show only equipped weapons, including both plasma modes.");
                            Check(shown.Any(w => w.Name == "4 × Rifle"), "Play mode must show equipped quantities.");
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            Capture(sheetWindow, imagePath + ".play.png");
                            Click(sheetWindow, "PlayModeButton");
                            Check(Find<DataGrid>(sheetWindow, "RangedWeaponTable").Items.Count == 4, "Build mode must restore all weapon alternatives.");
                            Check(DataJson.Write(changed.Loadout) == DataJson.Write(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Loadout), "Mode changes must retain loadout choices.");
                            Click(sheetWindow, "DuplicateEntryButton");
                            Check(DataJson.Write(changed.Loadout) == DataJson.Write(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Loadout), "Duplicating a unit must retain its loadout.");
                            var counterUnit = picker.Items.Cast<UnitDefinition>().FirstOrDefault(u => u.Name == "Blightlord Terminators")
                                ?? picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Option counter fixture");
                            picker.SelectedItem = counterUnit;
                            var sizePicker = Find<ComboBox>(sheetWindow, "SizePicker");
                            sizePicker.SelectedItem = sizePicker.Items.Cast<PointOption>().First(o => o.Models == 10);
                            Click(sheetWindow, "AddButton");
                            Button Counter(string name)
                            {
                                sheetWindow.UpdateLayout();
                                return Descendants<Button>(Find<StackPanel>(sheetWindow, "LoadoutCards")).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);
                            }
                            Counter("Add Blight launcher").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Counter("Add Blight launcher").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check(!Counter("Add Blight launcher").IsEnabled, "A third Blight launcher must be disabled at the imported cap of two.");
                            var selectedLoadout = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Loadout;
                            Check(Loadouts.Weapons(counterUnit, selectedLoadout).Single(w => w.Profile.Name == "Blight launcher").Count == 2, "Option counter must equip two complete weapon bundles.");
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            Counter("Add Blight launcher").BringIntoView();
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            Capture(sheetWindow, imagePath + ".option-counters.png");
                            Counter("Remove Blight launcher").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check(Counter("Add Blight launcher").IsEnabled, "Removing an upgrade must free capacity.");
                            Click(sheetWindow, "UndoButton");
                            Check(!Counter("Add Blight launcher").IsEnabled, "Undo must restore the cap and update the counter buttons.");
                            Click(sheetWindow, "PlayModeButton");
                            Check(Find<DataGrid>(sheetWindow, "RangedWeaponTable").Items.Cast<WeaponProfile>().Any(w => w.Name == "2 × Blight launcher"), "Play mode must use the aggregate selection quantities.");
                            Capture(sheetWindow, imagePath + ".option-play.png");
                            Click(sheetWindow, "PlayModeButton");
                        }
                        if (File.Exists(leaderFixture))
                        {
                            picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Attachment squad");
                            Click(sheetWindow, "AddButton");
                            var bodyguardId = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Id;
                            Click(sheetWindow, "DuplicateEntryButton");
                            picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Attachment leader");
                            Click(sheetWindow, "AddButton");
                            var leaderId = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Id;
                            ComboBox AttachmentPicker()
                            {
                                sheetWindow.UpdateLayout();
                                return OpenAttachmentPicker(Descendants<ComboBox>(Find<StackPanel>(sheetWindow, "AttachmentEditor")).Single())!;
                            }
                            Check(AttachmentPicker().Items.Count == 3, "Both squad copies must be offered as distinct bodyguards.");
                            AttachmentPicker().SelectedValue = bodyguardId;
                            Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().AttachedToId == bodyguardId, "Attachment must save the specific roster entry.");
                            Click(sheetWindow, "UndoButton");
                            Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().AttachedToId == "", "Undo must detach the leader.");
                            Click(sheetWindow, "RedoButton");
                            Check((string)AttachmentPicker().SelectedValue == bodyguardId, "Redo must restore the attachment selection.");
                            Click(sheetWindow, "DuplicateEntryButton");
                            Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().AttachedToId == "", "A duplicate leader must start unattached.");
                            Click(sheetWindow, "UndoButton");
                            Find<ListBox>(sheetWindow, "Roster").SelectedValue = bodyguardId;
                            sheetWindow.UpdateLayout();
                            Capture(sheetWindow, imagePath + ".attachments.png");
                            Click(sheetWindow, "PlayModeButton");
                            Check(Find<StackPanel>(sheetWindow, "AttachmentSummary").Visibility == Visibility.Visible, "Play mode must show attached leaders.");
                            Find<StackPanel>(sheetWindow, "AttachmentSummary").Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check((string)Find<ListBox>(sheetWindow, "Roster").SelectedValue == leaderId, "Attachment links must open the leader datasheet in Play mode.");
                            Capture(sheetWindow, imagePath + ".attachments-play.png");
                            Click(sheetWindow, "PlayModeButton");
                            AttachmentPicker().SelectedValue = "";
                            Check(store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().AttachedToId == "", "Unattached selection must remove the relationship.");
                        }
                        if (File.Exists(effectFixture))
                        {
                            picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Effect squad");
                            Find<ComboBox>(sheetWindow, "SizePicker").SelectedIndex = 0;
                            Click(sheetWindow, "AddButton");
                            var bodyguardId = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Id;
                            picker.SelectedItem = picker.Items.Cast<UnitDefinition>().Single(u => u.Name == "Effect leader");
                            Click(sheetWindow, "AddButton");
                            var leaderId = store.LoadArmies().Single(a => a.Id == datasheetArmy.Id).Entries.Last().Id;
                            sheetWindow.UpdateLayout();
                            OpenAttachmentPicker(Descendants<ComboBox>(Find<StackPanel>(sheetWindow, "AttachmentEditor")).Single())!.SelectedValue = bodyguardId;
                            Find<ListBox>(sheetWindow, "Roster").SelectedValue = bodyguardId;
                            var target = Find<CheckBox>(sheetWindow, "TargetAfflicted");
                            Check(target.Visibility == Visibility.Visible && target.IsChecked == false, "Conditional effects must start off and offer an Afflicted target switch.");
                            WeaponProfile Rifle() => Find<DataGrid>(sheetWindow, "RangedWeaponTable").Items.Cast<WeaponProfile>().Single(w => w.Name.EndsWith("Test rifle"));
                            Check(Rifle().Strength == "4" && Rifle().Ap == "-1", "Unfulfilled target condition must retain base stats.");
                            target.IsChecked = true;
                            Check(Rifle().Strength == "5" && Rifle().Ap == "-2" && Rifle().Keywords.Contains("Sustained Hits 1"), "Active attached effects must update displayed weapon characteristics and keywords.");
                            Check(Find<ItemsControl>(sheetWindow, "AbilityCards").Items.Cast<AbilityProfile>().Any(a => a.Name == "Gift fixture" && a.Section.Contains("Effect leader")), "Bodyguard must show its leader's granted ability and source.");
                            var blade = Find<DataGrid>(sheetWindow, "MeleeWeaponTable").Items.Cast<WeaponProfile>().Single();
                            Check(blade.Strength == "5" && blade.Ap == "-2" && blade.Keywords.Contains("Sustained Hits 1"), "Ranged stat bonus must not modify melee stats.");
                            Capture(sheetWindow, imagePath + ".effects.png");
                            Click(sheetWindow, "PlayModeButton");
                            Check(Find<DataGrid>(sheetWindow, "RangedWeaponTable").Items.Count == 1 && Rifle().Strength == "5", "Play mode must combine equipment filtering with attached effects.");
                            Capture(sheetWindow, imagePath + ".effects-play.png");
                            Find<StackPanel>(sheetWindow, "AttachmentSummary").Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check((string)Find<ListBox>(sheetWindow, "Roster").SelectedValue == leaderId && target.IsChecked == true && Rifle().Strength == "5", "Leader and bodyguard must share target preview conditions and unit-wide bonuses.");
                            target.IsChecked = false;
                            Check(Rifle().Strength == "4" && !Rifle().Keywords.Contains("Sustained"), "Turning off a condition must remove every conditional bonus.");
                            Click(sheetWindow, "PlayModeButton");
                            sheetWindow.UpdateLayout();
                            OpenAttachmentPicker(Descendants<ComboBox>(Find<StackPanel>(sheetWindow, "AttachmentEditor")).Single())!.SelectedValue = "";
                            Find<ListBox>(sheetWindow, "Roster").SelectedValue = bodyguardId;
                            Check(target.Visibility == Visibility.Collapsed && Rifle().Strength == "4", "Detaching must remove the small squad's conditional weapon bonuses.");
                            Click(sheetWindow, "UndoButton");
                            Check(target.Visibility == Visibility.Visible, "Undo must restore inherited effects.");
                        }
                        if (File.Exists(referenceFixture))
                        {
                            CheckDetachmentSelector(sheetWindow, store, datasheetArmy.Id, imagePath);
                        }
                    }
                    catch (Exception ex) { failure = ex.Message; }
                    finally { sheetWindow.Close(); }
                };
                sheetWindow.ShowDialog(); Check(failure == null, failure ?? "");
            }
        }
        CheckEnhancements(window, directory, imagePath);
        CheckUpgradePricing(window, directory, imagePath);
        CheckSelectionLimits(window, directory, imagePath);
        CheckAttachedEnhancementErrors(window, directory, imagePath);
        CheckAvailability(window, directory, imagePath);
        CheckMultiDispositions(window, directory, imagePath);
        CheckAllArmies(window, directory, imagePath);
        CheckAllies(window, directory, imagePath);
        CheckPricingRepair(window, directory, imagePath);
        CheckMultipleAttachments(window, directory, imagePath);
        CheckAttachmentCombinations(window, directory, imagePath);
        CheckStructuralAttachments(window, directory, imagePath);
        CheckExports(window, directory, imagePath);
        CheckMuster(window, directory, imagePath);
        File.WriteAllText(imagePath + ".result.txt", "PASS: army setup, persistence, roster operations, imports, datasheets, loadouts, attachments, detachments, dispositions, stratagem colours and enhancement eligibility/selection/points/undo/Play mode.");
    }
}
