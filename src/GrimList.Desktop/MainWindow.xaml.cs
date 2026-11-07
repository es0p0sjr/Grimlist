using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GrimList.Core;
using Microsoft.Win32;

namespace GrimList.Desktop;
public partial class MainWindow : Window
{
    private readonly ArmyStore store;
    private readonly ArmySession session;
    private Dataset? data => session?.Dataset;
    private ArmyList army => session.Army;
    private bool rendering, renderingDetails, rosterOpen, returnToRoster;
    private string? detailEntryId { get => session?.Context.EntryId; set { if (session != null) session.SelectEntry(value); } }
    public MainWindow(string directory) : this(LibraryBootstrap.Open(new WindowsPaths(directory), new WindowsSqliteConnectionFactory())) { }
    public MainWindow(ArmyStore repository)
    {
        InitializeComponent();
        RangedWeaponTable.RowHeight = double.NaN;
        MeleeWeaponTable.RowHeight = double.NaN;
        store = repository; session = new ArmySession(store, useFactionLoading: true);
        Setup.FactionData = faction => session.DatasetForArmy(ArmyList.New(faction));
        Home.NewRequested += () => NewArmy(this, new RoutedEventArgs());
        Home.OpenRequested += selected => Run(() => OpenRoster(selected));
        Home.DuplicateRequested += selected => Run(() => { session.DuplicateArmy(selected); RefreshHome(); });
        Home.DeleteRequested += selected => Run(() => DeleteSavedArmy(selected));
        Setup.Completed += created => { OpenRoster(created); Status("Army created · saved locally"); };
        Setup.CancelRequested += () => { if (returnToRoster) OpenRoster(army); else ShowHome(); };
        ShowHome(); DarkWindow.Apply(this);
        Closing += (_, e) => { if (!CommitFields(refresh: false)) e.Cancel = true; };
        Closed += (_, _) => store.Dispose();
    }
    private void ReportBug(object sender, RoutedEventArgs e) => Run(() => new BugReportWindow(this, data, rosterOpen ? army : null, new WindowsPaths(store.DirectoryPath)).ShowDialog());
    private void Status(string message, bool error = false)
    {
        StatusText.Text = message; StatusText.ToolTip = message;
        StatusText.Foreground = (Brush)FindResource(error ? "Error" : "Success");
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            ApplicationErrors.Record(ex);
            if (Enhancements.IsSelectionError(ex)) ShowError(ex.Message);
            else Status(ex.Message, true);
        }
    }
    private void SetAccent()
    {
        var accent = (SolidColorBrush)FactionPalette.Brush(army.Faction);
        Resources["Accent"] = accent;
        Resources["AccentDark"] = new SolidColorBrush(Color.FromRgb((byte)(accent.Color.R * .45), (byte)(accent.Color.G * .45), (byte)(accent.Color.B * .45)));
    }
    private void RefreshHome()
    {
        InvalidateAttachmentChoices();
        Home.Refresh(data, session.LoadArmySummaries());
    }
    private void ShowHome()
    {
        rosterOpen = false; detailEntryId = null;
        BuilderScreen.Visibility = RulesScreen.Visibility = Setup.Visibility = Visibility.Collapsed;
        Home.Visibility = GlobalTools.Visibility = Visibility.Visible;
        RefreshHome(); Status("Ready");
    }
    private void GoHome(object sender, RoutedEventArgs e) { if (CommitFields(refresh: false)) ShowHome(); }
    private void NewArmy(object sender, RoutedEventArgs e)
    {
        if (!CommitFields(refresh: false)) return;
        returnToRoster = rosterOpen; rosterOpen = false;
        Home.Visibility = GlobalTools.Visibility = BuilderScreen.Visibility = RulesScreen.Visibility = Visibility.Collapsed;
        Setup.Visibility = Visibility.Visible; Setup.Begin(data, session.LoadArmies(), returnToRoster, session.Factions); Status("Select faction");
    }
    private void OpenRoster(ArmyList selected)
    {
        ClearError();
        DetachmentOptionsPanel.Visibility = Visibility.Collapsed;
        playMode = false; ApplyPlayLayout();
        session.Open(selected); rosterOpen = true; SetAccent();
        Home.Visibility = GlobalTools.Visibility = Setup.Visibility = RulesScreen.Visibility = Visibility.Collapsed;
        BuilderScreen.Visibility = Visibility.Visible;
        rendering = true; SearchBox.Clear(); rendering = false;
        Render(); Status("Army loaded");
    }
    private static ListCollectionView Grouped<T>(List<T> items, string category)
    {
        var view = new ListCollectionView(items); view.GroupDescriptions.Add(new PropertyGroupDescription(category)); return view;
    }
    private string Category(string key) => data?.FindUnit(key) is { } unit ? AlliedRules.Category(army, unit, data) : "Others";
    private sealed class LibraryCategory(ArmyList army, Dataset? data) : GroupDescription
    {
        public override object GroupNameFromItem(object item, int level, System.Globalization.CultureInfo culture)
            => AlliedRules.Category(army, (UnitDefinition)item, data);
    }
    private sealed record LibraryUnit : UnitDefinition
    {
        public bool CanAdd { get; }
        public string AddHint { get; }
        public LibraryUnit(UnitDefinition unit, string? problem) : base(unit)
        { CanAdd = problem == null && unit.Options.Count > 0; AddHint = problem ?? (CanAdd ? "Add unit" : "Points unavailable"); }
    }
    private void Render()
    {
        rendering = true;
        try
        {
            SetAccent(); ArmyName.Text = army.Name; LimitBox.Text = army.Limit.ToString();
            FactionText.Text = army.Faction.ToUpperInvariant(); DetachmentText.Text = army.Detachment.Length == 0 ? "No detachment recorded" : army.Detachment;
            var result = session.Result;
            AlliesSummaryText.Text = AlliedRules.Summary(army, data, result.Entries);
            AlliesSummaryText.ToolTip = string.Join("\n", army.Entries.Select(e => data?.FindUnit(e.UnitKey)).OfType<UnitDefinition>()
                .Select(u => AlliedRules.Permission(army, u, data)).OfType<AlliedPermission>().Distinct()
                .Select(p => p.Name + ": " + AlliedRules.Allowance(p, army.Limit) + "\n" + p.SourceUrl));
            TotalText.Text = $"{result.Total}/{army.Limit}";
            RemainingText.Text = !result.Complete ? "incomplete" : result.Total <= army.Limit ? $"{army.Limit - result.Total} remaining" : $"{result.Total - army.Limit} over";
            PointsBar.Maximum = Math.Max(1, army.Limit); PointsBar.Value = Math.Min(result.Total, army.Limit);
            PointsBar.Foreground = (Brush)FindResource(result.Total > army.Limit ? "Error" : "Accent");
            RosterCount.Text = $"{army.Entries.Count} units · {army.Entries.Sum(e => e.Models)} models";
            var rows = AttachmentRows(result);
            var rosterView = Grouped(rows.OrderBy(r => UnitCategories.Order(r.Category)).ToList(), nameof(RosterRow.Category));
            rosterView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RosterRow.AttachmentGroup)));
            Roster.ItemsSource = rosterView;
            if (detailEntryId != null && army.Entries.All(e => e.Id != detailEntryId)) detailEntryId = army.Entries.FirstOrDefault()?.Id;
            Roster.SelectedValue = detailEntryId;
            RosterEmpty.Visibility = army.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var enhancementUsage = Enhancements.Usage(army, data);
            EnhancementBudgetText.Text = $"Enhancements: {enhancementUsage.Choices}/{Enhancements.Budget(army.Limit)?.ToString() ?? "?"} choices · Upgrades: {enhancementUsage.Upgrades}";
            EnhancementBudgetText.ToolTip = "Up to three copies of each Upgrade use one enhancement choice. One enhancement or upgrade per unit.";
            ValidationText.Text = result.Validated ? result.ValidationStatus : string.Join("\n", result.Issues.Take(3));
            ValidationText.ToolTip = string.Join("\n", result.Issues);
            ValidationText.Foreground = (Brush)FindResource(result.Validated ? "Success" : "Warning");
            var sources = data?.Units.Where(u => u.Faction == army.Faction).Select(u => u.PointsSource ?? data.Source).Distinct().ToList() ?? [];
            SourceText.Text = sources.Count == 0 ? "No points imported" : $"{sources.Count} points source(s) · review dates";
            SourceText.ToolTip = string.Join("\n", sources.Select(s => $"{s.Name} · {s.Version} · {s.Date}\n{s.Url}"));
            UndoButton.IsEnabled = session.CanUndo; RedoButton.IsEnabled = session.CanRedo;
            RulesUndoButton.IsEnabled = session.CanUndo; RulesRedoButton.IsEnabled = session.CanRedo;
            var candidates = WarlordRules.Candidates(army, data);
            WarlordPicker.ItemsSource = new[] { new WarlordChoice("", "Select warlord…") }.Concat(candidates.Select((e, i) => new WarlordChoice(e.Id,
                e.Name + (candidates.Count(c => c.Name == e.Name) > 1 ? $" (unit {i + 1})" : "")))).ToList();
            WarlordPicker.SelectedValue = army.WarlordEntryId;
            var mandatory = candidates.Where(e => WarlordRules.Required(army, e, data)).ToList();
            WarlordPicker.IsEnabled = candidates.Count > 0 && mandatory.Count != 1;
            WarlordHint.Text = mandatory.Count == 1 ? "Automatically selected · mandatory warlord"
                : mandatory.Count > 1 ? "Choose one of the mandatory warlords."
                : candidates.Any(e => data?.FindUnit(e.UnitKey)?.Profile is { MustBeWarlord: null }) ? "Reimport BSData datasheets to load automatic warlord requirements."
                : candidates.Count == 0 ? "Add an eligible character. Import datasheets if eligibility is unknown." : "Choose one eligible character from this roster.";
            var exclusions = army.Entries.Select(e => (e.Name, Reason: WarlordRules.RestrictionReason(army, e, data)))
                .Where(e => e.Reason.Length > 0).Select(e => e.Name + ": " + e.Reason).Distinct().ToList();
            if (exclusions.Count > 0) WarlordHint.Text += "\n" + string.Join("\n", exclusions);
            RefreshMuster(); RefreshLibrary(); RefreshRules();
        }
        finally { rendering = false; }
        RefreshDetails();
    }
    private List<PointOption> OptionsFor(string key, int copy) => session.OptionsFor(key, copy);
    private void RefreshLibrary()
    {
        if (UnitPicker == null) return;
        var selected = (UnitPicker.SelectedItem as UnitDefinition)?.Key;
        var list = session.BrowseLibrary(new(SearchBox.Text, FitsOnly.IsChecked == true, HideLegends.IsChecked == true, HideCrucible.IsChecked == true))
            .Select(c => new LibraryUnit(c.Unit, c.Problem)).ToList();
        var wasRendering = rendering; rendering = true;
        var libraryView = new ListCollectionView(list); libraryView.GroupDescriptions.Add(new LibraryCategory(army, data));
        UnitPicker.ItemsSource = libraryView;
        UnitPicker.SelectedItem = list.FirstOrDefault(u => u.Key == selected) ?? list.FirstOrDefault();
        rendering = wasRendering;
        UnitCount.Text = $"{list.Count} units";
        LibraryEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyTitle.Text = data == null ? "Import unit points" : "No matching units";
        LibraryEmptyBody.Text = data == null ? "Download BSData or choose a local catalogue ZIP." : "Try another search, change unit filters, or select a detachment that permits these units.";
        UpdateSizePicker();
        if (!rendering) RefreshDetails();
    }
    private void UpdateSizePicker()
    {
        if (SizePicker == null) return;
        var models = (SizePicker.SelectedItem as PointOption)?.Models;
        var unit = UnitPicker.SelectedItem as UnitDefinition;
        var options = unit == null ? [] : OptionsFor(unit.Key, ArmyCalculator.NextCopy(army, data, unit));
        // Replacing the items fires SelectionChanged with no selected size. Do not
        // render that intermediate state as a one-model preview of a multi-model unit.
        // Callers refresh details after the complete picker update; manual size
        // changes still refresh through PreviewSizeChanged normally.
        var wasRendering = rendering;
        rendering = true;
        try
        {
            SizePicker.ItemsSource = options;
            SizePicker.SelectedItem = options.FirstOrDefault(o => o.Models == models) ?? options.FirstOrDefault();
        }
        finally { rendering = wasRendering; }
        var problem = unit == null ? null : MusterRules.AddProblem(army, data, unit);
        AddButton.IsEnabled = options.Count > 0 && problem == null;
        AddButton.ToolTip = problem ?? "Add unit";
    }
    private void RefreshDetails()
    {
        if (rendering) return;
        renderingDetails = true;
        try
        {
            var entry = army.Entries.FirstOrDefault(e => e.Id == detailEntryId);
            var effectiveState = entry == null ? null : EffectiveDatasheets.Project(army, entry, data);
            var unit = entry == null ? (playMode ? null : UnitPicker.SelectedItem as UnitDefinition) : effectiveState?.Unit;
            if (entry == null && unit != null) unit = EffectiveDatasheets.Preview(army, data, unit);
            var baseProfile = effectiveState?.BaseUnit.Profile ?? (unit == null ? null : data?.FindUnit(unit.Key)?.Profile);
            RefreshDatasheetChoices(entry);
            referenceUnit = unit;
            DetailName.Text = (entry?.Name ?? unit?.Name ?? "Select a unit").ToUpperInvariant();
            AllyHintText.Text = unit != null && AlliedRules.IsAlly(army, unit)
                ? $"Ally · {AlliedRules.OriginFaction(unit)} · {AlliedRules.Permission(army, unit, data)?.Name ?? "not permitted by current detachments"}" : "";
            AllyHintText.Visibility = AllyHintText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AllyHintText.ToolTip = unit != null && AlliedRules.Permission(army, unit, data) is { } allyRule
                ? AlliedRules.Allowance(allyRule, army.Limit) + "\n" + allyRule.SourceUrl : null;
            MissingDatasheet.Visibility = unit != null && unit.Profile == null ? Visibility.Visible : Visibility.Collapsed;
            MissingDatasheetText.Text = unit?.Options.Count > 0
                ? "Only points have been imported for this unit. Import datasheets to show its stats, weapons and abilities."
                : "No datasheet has been imported for this unit.";
            var statlines = EffectiveDatasheetDisplay.Statlines(baseProfile, unit?.Profile);
            var primaryStats = statlines.FirstOrDefault();
            PrimaryModelName.Text = primaryStats?.Name ?? "";
            PrimaryModelName.Visibility = statlines.Count > 1 && PrimaryModelName.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Stats.ItemsSource = EffectiveDatasheetDisplay.Characteristics(primaryStats?.Source, primaryStats?.Effective);
            Stats.Visibility = primaryStats != null ? Visibility.Visible : Visibility.Collapsed;
            InvulnerableValue.Text = DatasheetStats.Invulnerable(primaryStats?.Effective);
            HighlightEffectiveValue(InvulnerableValue, EffectiveDatasheetDisplay.Changed(DatasheetStats.Invulnerable(primaryStats?.Source), InvulnerableValue.Text));
            InvulnerablePanel.Visibility = InvulnerableValue.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            DefensiveStats.Margin = SaveMargin(primaryStats?.Effective);
            feelNoPainRules = DatasheetStats.FeelNoPain(army, data, unit, entry);
            if (primaryStats != null)
                feelNoPainRules = feelNoPainRules.Concat(DatasheetStats.FeelNoPain(new(Characteristics: primaryStats.Effective))).Distinct().ToList();
            FeelNoPainValue.Text = DatasheetStats.FeelNoPainLabel(feelNoPainRules);
            FeelNoPainPanel.Visibility = feelNoPainRules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FeelNoPainPanel.ToolTip = string.Join("\n\n", feelNoPainRules.Select(r => r.Text));
            var weapons = playMode && unit != null ? Loadouts.Weapons(unit, entry?.Loadout).Select(w => w.Profile with { Name = $"{w.Count} × {w.Profile.Name}" }).ToList() : unit?.Profile?.Weapons ?? [];
            weapons = RefreshEffects(unit, entry, weapons);
            StyleEffectiveWeapons(RangedWeaponTable, baseProfile);
            StyleEffectiveWeapons(MeleeWeaponTable, baseProfile);
            var weaponsByMeleeRange = weapons.ToLookup(w => string.Equals(w.Range?.Trim(), "Melee", StringComparison.OrdinalIgnoreCase));
            var meleeWeapons = weaponsByMeleeRange[true].ToList();
            var rangedWeapons = weaponsByMeleeRange[false].ToList();
            RangedWeaponTable.ItemsSource = rangedWeapons;
            MeleeWeaponTable.ItemsSource = meleeWeapons;
            RangedWeaponsSection.Visibility = rangedWeapons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MeleeWeaponsSection.Visibility = meleeWeapons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoProfiles.Visibility = weapons.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            NoProfiles.Text = playMode ? "No equipped weapons selected. Return to Build mode to configure this unit." : "No weapon profiles in this points source.";
            UnitKeywords.Text = unit == null ? "" : string.Join(" · ", MusterRules.Keywords(army, unit, data));
            UnitKeywords.Visibility = UnitKeywords.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ModelCards.ItemsSource = statlines.Skip(1).Select(m => new { m.Name,
                Characteristics = EffectiveDatasheetDisplay.Characteristics(m.Source, m.Effective), InvulnerableSave = DatasheetStats.Invulnerable(m.Effective),
                InvulnerableModified = EffectiveDatasheetDisplay.Changed(DatasheetStats.Invulnerable(m.Source), DatasheetStats.Invulnerable(m.Effective)),
                InvulnerableVisibility = DatasheetStats.Invulnerable(m.Effective).Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                FeelNoPain = string.Join(" / ", DatasheetStats.FeelNoPain(new(Characteristics: m.Effective)).Select(r => r.Name)),
                FeelNoPainVisibility = DatasheetStats.FeelNoPain(new(Characteristics: m.Effective)).Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                InvulnerableMargin = SaveMargin(m.Effective) }).ToList();
            ModelsSection.Visibility = statlines.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            LibraryComposition.Visibility = entry == null ? Visibility.Visible : Visibility.Collapsed;
            RosterComposition.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
            if (entry != null)
            {
                var priced = session.Result.Entries.First(e => e.Entry.Id == entry.Id);
                UnitPointsBreakdown.Text = $"{priced.Points?.ToString() ?? "?"} pts · {priced.Breakdown}";
                var sizes = OptionsFor(entry.UnitKey, priced.Copy).Select(o => new SizeChoice(o.Models, o.Label)).ToList();
                if (sizes.All(s => s.Models != entry.Models)) sizes.Add(new(entry.Models, $"{entry.Models} models · unavailable"));
                EntrySizePicker.ItemsSource = sizes; EntrySizePicker.Tag = entry.Id; EntrySizePicker.SelectedValue = entry.Models;
                DetailModels.Text = $"{entry.Models} models";
                DecreaseSizeButton.IsEnabled = EntrySizePicker.SelectedIndex > 0;
                IncreaseSizeButton.IsEnabled = EntrySizePicker.SelectedIndex >= 0 && EntrySizePicker.SelectedIndex < sizes.Count - 1;
                DuplicateEntryButton.Tag = entry.Id;
                DuplicateEntryButton.IsEnabled = unit != null && MusterRules.AddProblem(army, data, unit) == null;
                DuplicateEntryButton.ToolTip = unit == null ? "Datasheet unavailable" : MusterRules.AddProblem(army, data, unit) ?? "Duplicate unit";
            }
            else
            {
                var option = SizePicker.SelectedItem as PointOption;
                var equipment = unit == null || option == null ? null : WargearPricing.Cost(unit, new("preview", unit.Key, unit.Name, option.Models));
                UnitPointsBreakdown.Text = option == null ? "" : $"{option.Points} pts before wargear · Default wargear +{equipment?.ToString() ?? "?"} pts";
            }
            UnitPointsBreakdown.Visibility = UnitPointsBreakdown.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshLoadout(unit, entry);
            RefreshAttachments(unit, entry);
            RefreshEnhancements(unit, entry);
            RefreshError();
            static string Describe(SourceInfo? s) => s == null ? "Not imported" : $"{s.Name} · {s.Version} · {(s.Date.Length == 0 ? "date unknown" : s.Date)} · edition {(s.Edition.Length == 0 ? "unknown" : s.Edition)}";
            DetailSource.Text = $"Points: {Describe(unit?.PointsSource ?? (unit?.Options.Count > 0 ? data?.Source : null))}\nDatasheet: {Describe(unit?.ProfileSource)}";
            DetailSource.ToolTip = $"Points: {unit?.PointsSource?.Url}\nDatasheet: {unit?.ProfileSource?.Url}";
        }
        finally { renderingDetails = false; }
    }
    private bool CommitFields(bool refresh = true)
    {
        if (rendering || !rosterOpen) return true;
        if (!int.TryParse(LimitBox.Text, out var limit) || limit is <= 0 or > 1000000 || string.IsNullOrWhiteSpace(ArmyName.Text))
        { Status("Enter an army name and a points limit from 1 to 1,000,000.", true); return false; }
        try
        {
            // Edits are saved as they happen. Losing focus or navigating with
            // unchanged metadata must not save and rebuild the editor again.
            if (ArmyName.Text.Trim() == army.Name && limit == army.Limit) return true;
            Edit(() => session.SetMetadata(ArmyName.Text, limit), refresh);
            return true;
        }
        catch (Exception ex) { ApplicationErrors.Record(ex); Status(ex.Message, true); return false; }
    }
    private void Edit(Action action, bool refresh = true)
    {
        action(); ClearError(); if (refresh) Render(); Status("Saved locally");
        if (session.NewIssues.Count > 0) Status(string.Join("\n", session.NewIssues), true);
    }
    private void SaveFields(object sender, RoutedEventArgs e) { if (store != null) CommitFields(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!rendering && store != null) RefreshLibrary(); }
    private void FilterChanged(object sender, RoutedEventArgs e) { if (!rendering && store != null) RefreshLibrary(); }
    private void ClearSearch(object sender, RoutedEventArgs e) => SearchBox.Clear();
    private void SelectUnit(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || store == null) return;
        detailEntryId = null; rendering = true; Roster.SelectedIndex = -1; rendering = false;
        UpdateSizePicker(); RefreshDetails();
    }
    private void RosterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || store == null || Roster.SelectedItem is not RosterRow row) return;
        detailEntryId = row.Id; RefreshDetails();
    }
    private void LibraryClicked(object sender, MouseButtonEventArgs e)
    {
        if (detailEntryId == null || ItemsControl.ContainerFromElement(UnitPicker, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
        detailEntryId = null; rendering = true; Roster.SelectedIndex = -1; rendering = false;
        UpdateSizePicker(); RefreshDetails();
    }
    private void AddUnit(object sender, RoutedEventArgs e)
    {
        if (playMode) return;
        if (!CommitFields()) return;
        if (UnitPicker.SelectedItem is UnitDefinition unit && SizePicker.SelectedItem is PointOption size)
            Run(() => Edit(() => session.AddUnit(unit.Key, size.Models)));
    }
    private void QuickAddUnit(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        UnitPicker.SelectedItem = UnitPicker.Items.Cast<UnitDefinition>().First(u => u.Key == key);
        UpdateSizePicker(); AddUnit(sender, e); e.Handled = true;
    }
    private void ResizeUnit(object sender, SelectionChangedEventArgs e)
    {
        if (playMode || rendering || renderingDetails || sender is not ComboBox { Tag: string id, SelectedItem: SizeChoice size }) return;
        if (army.Entries.FirstOrDefault(e => e.Id == id) is not { } entry || entry.Models == size.Models) return;
        Run(() => Edit(() => session.ResizeUnit(id, size.Models)));
    }
    private void StepUnitSize(object sender, RoutedEventArgs e)
    {
        var direction = int.Parse((string)((Button)sender).Tag);
        var index = EntrySizePicker.SelectedIndex + direction;
        if (index >= 0 && index < EntrySizePicker.Items.Count) EntrySizePicker.SelectedIndex = index;
    }
    private void DuplicateUnit(object sender, RoutedEventArgs e)
        => DuplicateEntry((string)((Button)sender).Tag);

    private void DuplicateEntry(string entryId)
    {
        if (playMode) return;
        Run(() => Edit(() => session.DuplicateUnit(entryId)));
    }
    private void RemoveUnit(object sender, RoutedEventArgs e)
    {
        if (playMode) return;
        Run(() => Edit(() => session.RemoveUnit((string)((Button)sender).Tag))); e.Handled = true;
    }
    private void Undo(object sender, RoutedEventArgs e)
    {
        if (playMode) return;
        Run(() => { Edit(session.Undo); Status("Undone"); });
    }
    private void Redo(object sender, RoutedEventArgs e)
    {
        if (playMode) return;
        Run(() => { Edit(session.Redo); Status("Redone"); });
    }
    private void SelectWarlord(object sender, SelectionChangedEventArgs e)
    {
        if (playMode || rendering || !rosterOpen || WarlordPicker.SelectedValue is not string id || id == army.WarlordEntryId) return;
        Run(() => Edit(() => session.SelectWarlord(id)));
    }
    private static Thickness SaveMargin(Dictionary<string, string>? stats)
    {
        var keys = DatasheetStats.Standard(stats).Keys.ToList();
        return new(Math.Max(0, keys.FindIndex(k => UnitIdentity.Normalize(k) is "SV" or "SAVE")) * 50, 10, 0, 0);
    }
    private void DeleteSavedArmy(ArmyList selected)
    {
        if (MessageBox.Show(this, $"Delete “{selected.Name}”?", "Delete army", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        session.DeleteArmy(selected.Id); ShowHome(); Status("Army deleted");
    }
    private void SelectDetachments(object sender, RoutedEventArgs e)
    {
        if (!CommitFields()) return;
        DetachmentOptionsPanel.Visibility = DetachmentOptionsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        SelectDetachmentsButton.Content = DetachmentOptionsPanel.Visibility == Visibility.Visible ? "Done" : "Add detachments";
    }
    private bool refreshingRules;
    private void AddDetachment(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || !CommitFields() || !DetachmentRules.CanAdd(army, name, data)) return;
        Run(() => Edit(() => session.AddDetachment(name)));
    }
    private void RemoveDetachment(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || !CommitFields()) return;
        Run(() => Edit(() => session.RemoveDetachment(name)));
    }
    private void ChooseDisposition(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingRules || rendering || DispositionPicker.SelectedItem is not string choice || !DetachmentRules.Dispositions(army, data).Contains(choice) || choice == army.Disposition) return;
        Run(() => Edit(() => session.SelectDisposition(choice)));
    }
    private void RefreshRules()
    {
        var selected = DetachmentRules.Selected(army);
        SelectedDetachmentsText.Text = selected.Count == 0 ? "No detachments selected" : "Your detachments";
        var usesDp = DetachmentRules.UsesPoints(army.Faction, data);
        SelectDetachmentsButton.IsEnabled = usesDp;
        SelectDetachmentsButton.Content = DetachmentOptionsPanel.Visibility == Visibility.Visible ? "Done" : "Add detachments";
        CurrentDetachments.ItemsSource = selected.Select(n => new DetachmentChoice(n, DetachmentRules.Find(army, n, data)?.Cost ?? "", "", true, "Remove")).ToList();
        AvailableDetachments.ItemsSource = DetachmentRules.Options(army, data).Where(d => !DetachmentRules.Contains(army, d.Name, data))
            .Select(d => new DetachmentChoice(d.Name, d.Cost, DetachmentRules.SelectionMetadata(d, data), DetachmentRules.CanAdd(army, d.Name, data),
                "Add")).ToList();
        DetachmentBudgetText.Text = usesDp
            ? DetachmentRules.SelectionSummary(army, data) +
                (DetachmentRules.Issues(army, data).Count > 0 ? " · " + string.Join(" ", DetachmentRules.Issues(army, data)) : "")
            : "Import detachment DP costs to enable selection.";
        refreshingRules = true;
        var dispositions = DetachmentRules.Dispositions(army, data);
        DispositionPicker.ItemsSource = dispositions;
        DispositionPicker.SelectedItem = army.Disposition;
        DispositionPicker.IsEnabled = dispositions.Count > 1;
        DispositionHint.Text = dispositions.Count == 0 ? "Add a detachment to unlock a disposition."
            : army.Disposition.Length == 0 ? "Choose the disposition this army will use."
            : "Available from: " + string.Join(", ", selected.Where(n => DetachmentRules.Find(army, n, data)?.DispositionOptions
                .Any(d => UnitIdentity.Normalize(d) == UnitIdentity.Normalize(army.Disposition)) == true));
        refreshingRules = false;
        var primaries = DispositionPrimaries.ForArmy(army, data);
        PrimaryMissionsTitle.Text = "Primary missions" + (primaries.Disposition.Length > 0 ? " · " + primaries.Disposition : "");
        PrimaryMissionsHint.Text = primaries.Hint;
        PrimaryMatchups.ItemsSource = primaries.Matchups;
        PrimaryMatchupHeader.Visibility = PrimaryMissionsSource.Visibility = primaries.Matchups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrimaryMissionsSource.Content = DispositionPrimaries.SourceLabel + " · Source";
        var cards = ArmyReferences.ForArmy(army, data);
        List<ReferenceCard> Section(string name) => cards.Where(c => c.Section.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        ArmyRuleCards.ItemsSource = Section("Army Rules"); DetachmentCards.ItemsSource = Section("Detachments");
        EnhancementCards.ItemsSource = Section("Enhancements");
        OtherReferenceCards.ItemsSource = cards.Where(c => c.Section is not ("Army Rules" or "Detachments" or "Enhancements" or "Stratagems" or "Dispositions")).ToList();
        StratagemCards.ItemsSource = Section("Stratagems").Where(c => !ArmyReferences.IsCore(c)).ToList();
        PreviousStratagemCards.ItemsSource = ArmyReferences.PreviousForArmy(army, data);
        PreviousStratagems.Visibility = PreviousStratagemCards.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CoreStratagemCards.ItemsSource = Section("Stratagems").Where(ArmyReferences.IsCore).ToList();
        CoreStratagemHint.Text = CoreStratagemCards.Items.Count == 0
            ? "Core stratagems unavailable for this edition. Import Core reference cards to add them."
            : "Available to every army.";
        NoArmyRules.Visibility = ArmyRuleCards.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoEnhancements.Visibility = EnhancementCards.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var missingStratagems = ArmyReferences.MissingStratagemDetachments(army, data);
        NoStratagems.Text = missingStratagems.Count == 0 ? "No verified current stratagems available."
            : "No verified current stratagems for: " + string.Join(", ", missingStratagems) + ". Use Update data or import a current rules reference for this edition.";
        NoStratagems.Visibility = StratagemCards.Items.Count == 0 || missingStratagems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (data != null)
        {
            var coverage = DatasetSnapshot.For(data).References.Coverage.Factions.Where(f => UnitIdentity.Normalize(f.Faction) == UnitIdentity.Normalize(army.Faction))
                .SelectMany(f => f.Detachments).Where(d => DetachmentRules.Contains(army, d.Detachment, data)).ToList();
            if (coverage.Count > 0)
            {
                NoStratagems.Text = string.Join("\n", coverage.Select(d => $"{d.Detachment}: {d.StratagemCount} current stratagem cards · coverage {d.Coverage}. {string.Join(" ", d.Missing)}"));
                NoStratagems.Visibility = Visibility.Visible;
            }
        }
        DetachmentExpander.Header = "Selected detachment rules";
        DetachmentText.Text = selected.Count == 0 ? "Add a detachment to see its rules, stratagems and enhancements." : string.Join(" · ", selected);
    }
    private record DetachmentChoice(string Name, string Cost, string Disposition, bool CanAdd, string Action);
    private void ShowRules(object sender, RoutedEventArgs e)
    {
        if (!CommitFields()) return;
        RefreshRules(); BuilderScreen.Visibility = Visibility.Collapsed; RulesScreen.Visibility = Visibility.Visible;
    }
    private void CloseRules(object sender, RoutedEventArgs e)
    {
        if (!CommitFields()) return;
        RulesScreen.Visibility = Visibility.Collapsed; BuilderScreen.Visibility = Visibility.Visible;
    }
    private void OpenRosterMenu(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = ActionMenu(button);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = button.ActualWidth - menu.MinWidth;
        menu.VerticalOffset = 6;
        MenuAction(menu, "New army", "Choose a faction and detachment", "\uE710", () => NewArmy(this, e));
        MenuAction(menu, "Export army", "Text, PDF or Yellowscribe", "\uE74E", () => ExportArmy(this, e));
        MenuAction(menu, "Battle tracker", "Start or resume a battle with this army", "\uE7C1", () => OpenBattleTracker(this, e));
        menu.Items.Add(new Separator());
        MenuAction(menu, "Manage data", "Import and review your library", "\uE713", () => ImportPoints(this, e));
        MenuAction(menu, "Report a Bug", "Review and share a bug report", "\uE946", () => ReportBug(this, e));
        MenuAction(menu, "Export dataset", "Save your library as JSON", "\uE8A5", () => ExportDataset(this, e));
        menu.Items.Add(new Separator());
        var delete = MenuAction(menu, "Delete army", "Remove this saved roster", "\uE74D", () => DeleteSavedArmy(army));
        delete.Foreground = (Brush)FindResource("Error");
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private void ExportArmy(object sender, RoutedEventArgs e)
    {
        if (!CommitFields()) return;
        new ExportWindow(army, data) { Owner = this }.ShowDialog();
    }
    private void OpenBattleTracker(object sender, RoutedEventArgs e)
    {
        if (!CommitFields()) return;
        Run(() => new BattleWindow(army, data, store.DirectoryPath) { Owner = this }.ShowDialog());
    }
    private void ExportDataset(object sender, RoutedEventArgs e)
    {
        if (data == null) { Status("Import points first."); return; }
        var dialog = new SaveFileDialog { Filter = "Points dataset|*.json", FileName = "grimlist-points.json" };
        if (dialog.ShowDialog(this) == true) Run(() => { File.WriteAllText(dialog.FileName, DataJson.Write(ReferenceModel.ExportDataset(session.LoadFullDataset()!))); Status("Library and references exported"); });
    }
    private void ImportPoints(object sender, RoutedEventArgs e)
        => ManageData(false);
    private void ImportDatasheets(object sender, RoutedEventArgs e) => ManageData(false, ImportLayer.Profiles);
    private void ManageData(bool download, ImportLayer? initialLayer = null)
    {
        if (!CommitFields()) return;
        Run(() =>
        {
            var needsReview = 0;
            var libraryFilter = new LibraryFilter(SearchBox.Text, FitsOnly.IsChecked == true, HideLegends.IsChecked == true, HideCrucible.IsChecked == true);
            var dialog = new ImportWindow(store, rosterOpen ? army.Faction : null, download, initialLayer, data?.Source.Edition,
                async progress =>
                {
                    progress.Report("Finishing update · Rebuilding the army library…");
                    await session.RunAsync(s => s.ReloadLibrary());
                    progress.Report("Finishing update · Checking saved armies…");
                    await session.RunAsync(s =>
                    {
                        s.LoadArmySummaries(); needsReview = s.ArmiesNeedingReview();
                        if (rosterOpen) s.BrowseLibrary(libraryFilter);
                    });
                }) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (rosterOpen) Render();
            RefreshHome(); if (Setup.Visibility == Visibility.Visible) Setup.RefreshData(data, session.LoadArmies(), session.Factions);
            Status($"Data updated · {needsReview} armies need review");
        });
    }
    private void OpenCommunity(object sender, RoutedEventArgs e) => Run(() => Process.Start(new ProcessStartInfo($"https://github.com/BSData/wh40k-{(data?.Source.Edition == "10" ? "10" : "11")}e") { UseShellExecute = true }));
    private void OpenPrimarySource(object sender, RoutedEventArgs e) => Run(() => Process.Start(new ProcessStartInfo(DispositionPrimaries.SourceUrl) { UseShellExecute = true }));
    private void CheckUpdates(object sender, RoutedEventArgs e) => ManageData(true, ImportLayer.Combined);
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.K) { if (Home.Visibility == Visibility.Visible) Home.FocusSearch(); else if (rosterOpen && BuilderScreen.Visibility == Visibility.Visible) { SearchBox.Focus(); SearchBox.SelectAll(); } else if (Setup.Visibility == Visibility.Visible && Setup.Step == 0) ((TextBox)Setup.FindName("FactionSearch")).Focus(); e.Handled = true; }
        if (e.Key == Key.N) { NewArmy(this, e); e.Handled = true; }
        if (e.Key == Key.S) { if (rosterOpen && CommitFields()) Status("Saved locally"); e.Handled = true; }
        if (rosterOpen && Keyboard.FocusedElement is not TextBox && e.Key is Key.Z or Key.Y) { if (e.Key == Key.Z) Undo(this, e); else Redo(this, e); e.Handled = true; }
    }
    private record SizeChoice(int Models, string Label) { public override string ToString() => Label; }
    private record WarlordChoice(string Id, string Name);
    private record AttachmentGroup(string Id, bool IsAttached, string Summary);
    private record RosterRow(string Id, string Name, string Category, string Summary, AttachmentGroup AttachmentGroup);
}
