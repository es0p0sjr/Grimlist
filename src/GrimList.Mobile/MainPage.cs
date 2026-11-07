using System.ComponentModel;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;
using Microsoft.Maui.Controls;
using IFilePicker = GrimList.Platform.IFilePicker;

namespace GrimList.Mobile;

/// <summary>Android presentation over the same persisted workspace and commands used by WPF.</summary>
public sealed partial class MainPage : ContentPage
{
    private enum Screen { Home, Factions, Setup, Editor, Unit, Rules, Library, Export, ImportReview }
    private readonly MobileWorkspace owner;
    private readonly IFilePicker files;
    private readonly IClipboardService clipboard;
    private readonly IMobileDocumentSaver documents;
    private readonly IPdfTextExtractor pdfText;
    private readonly IRosterPdfExporter pdfExport;
    private ArmyWorkspace? workspace;
    private ArmySession Session => workspace!.Session;
    private Screen screen = Screen.Home;
    private readonly Grid root = new(), page = new();
    private readonly Label activityText = Ui.Text("Opening your library…", 14);
    private readonly ActivityIndicator spinner = new() { Color = Colors.White, IsRunning = true };
    private readonly Button cancelImport;
    private readonly Border activity;
    private View? menu;
    private bool busy, subscribed, initialized, rendering;
    private bool playMode, hideLegends, fitsRemaining;
    private bool hideCrucible = true;
    private readonly HashSet<string> afflictedTargets = [];
    private CancellationTokenSource? importCancellation;
    private string unitQuery = "", homeQuery = "", selectedFaction = "", previewKey = "", unitTab = "datasheet";
    private string draftName = "", draftLimit = "2000";
    private readonly HashSet<string> draftDetachments = [];
    private readonly HashSet<string> openCategories = [];
    private List<SavedArmySummary> homeArmies = [];
    private List<LibraryRow> libraryRows = [];
    private List<RosterGroup> rosterGroups = [];
    private List<ReferenceCard> references = [];
    private UnitView? unitView;
    private IReadOnlyList<LibraryChoice>? preparedLibraryChoices;
    private CollectionView? libraryBody;
    private Grid? homeTiles, editorColumns;
    private int searchVersion;

    public MainPage(MobileWorkspace owner, IFilePicker files, IClipboardService clipboard, IMobileDocumentSaver documents, IPdfTextExtractor pdfText, IRosterPdfExporter pdfExport)
    {
        this.owner = owner; this.files = files; this.clipboard = clipboard; this.documents = documents; this.pdfText = pdfText; this.pdfExport = pdfExport;
        Title = "GrimList"; BackgroundColor = Ui.Background;
        NavigationPage.SetHasNavigationBar(this, false);
        cancelImport = Ui.Button("Cancel", () => importCancellation?.Cancel(), "cancel-import", Ui.Surface);
        cancelImport.IsVisible = false;
        var progress = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 12 };
        progress.Add(spinner); progress.Add(activityText, 1); progress.Add(cancelImport, 2);
        activity = Ui.Card(progress, Ui.Accent, 12);
        activity.VerticalOptions = LayoutOptions.End; activity.Margin = 12;
        root.Add(page); root.Add(activity); Content = root;
        SizeChanged += (_, _) => UpdateColumns();
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initialized) { Subscribe(); await HandleIncomingBattleQr(); return; }
        await OpenLibrary();
        await HandleIncomingBattleQr();
#if PERFORMANCE_CHECKS
        if (initialized) await RunPerformance();
#endif
    }
    private async Task OpenLibrary()
    {
        await Execute(async () =>
        {
            workspace = await owner.GetAsync(new Progress<string>(message => activityText.Text = message)); Subscribe();
#if PERFORMANCE_CHECKS
            performanceLibraryMs = PerformanceClock.Elapsed.TotalMilliseconds;
#endif
            if (Session.IsOpen)
                screen = Session.Context.Screen switch { "home" => Screen.Home, "unit" or "play-unit" when Session.SelectedEntry != null => Screen.Unit,
                    "rules" or "play-rules" => Screen.Rules, "play" => Screen.Editor, _ => Screen.Editor };
            playMode = Session.Context.Screen is "play" or "play-unit" or "play-rules";
            unitTab = Session.Context.LoadoutSection == "equipment" ? "equipment" : "datasheet";
            initialized = true;
        }, "Opening your library…");
    }
    private void Subscribe()
    { if (workspace != null && !subscribed) { Session.PropertyChanged += SessionChanged; subscribed = true; } }
    protected override void OnDisappearing()
    {
        if (subscribed) { Session.PropertyChanged -= SessionChanged; subscribed = false; }
        base.OnDisappearing();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Commands owned by Execute already publish once below. Do not enqueue a
        // second refresh that races completion of the same command.
        if (busy) return;
        Dispatcher.Dispatch(() => { if (!busy && initialized && presentedVersion != Session.Mutation.Version) _ = Execute(() => Task.CompletedTask); });
    }
    private Button Action(string text, Func<Task> action, string id = "", Color? colour = null, bool compact = false)
        => Ui.Button(text, async () => await Execute(action), id, colour ?? Accent, compact);
    private Color Accent => MobilePalette.ForFaction(screen is Screen.Factions or Screen.Home or Screen.Library ? "" :
        screen == Screen.Setup ? selectedFaction : Session.Army.Faction);
    private async Task Execute(Func<Task> action, string message = "Updating…")
    {
        if (busy) return;
        busy = true; ++searchVersion; CloseMenu(); page.IsEnabled = false;
        activityText.Text = message; activity.IsVisible = true; spinner.IsRunning = true;
        string? error = null;
        try { await action(); }
        catch (OperationCanceledException) { error = "Import cancelled. Your current library and armies are unchanged."; }
        catch (Exception ex) { ApplicationErrors.Record(ex); error = ex.Message; }
        try { if (workspace != null) { await PrepareAsync(); RefreshPresentation(); } }
        catch (Exception ex) { ApplicationErrors.Record(ex); error = "Could not open this view: " + ex.Message; }
        finally { busy = false; page.IsEnabled = true; spinner.IsRunning = false; activity.IsVisible = false; }
        if (workspace == null && error != null)
        {
            page.Children.Clear();
            var retry = new Button { Text = "Retry opening library" };
            retry.Clicked += async (_, _) => await OpenLibrary();
            page.Add(new VerticalStackLayout { Padding = 24, Spacing = 16, Children = { Ui.Text(error, 14), retry } });
        }
        if (error != null) await DisplayAlertAsync("GrimList", error, "OK");
    }
    private async Task Go(Screen next)
    {
        if (screen == Screen.ImportReview && next != Screen.ImportReview) pendingImport = null;
        screen = next;
        if (Session.IsOpen && next is not (Screen.Library or Screen.Export or Screen.ImportReview)) await Session.RunAsync(s => s.Navigate(next switch
        { Screen.Unit when previewKey.Length == 0 => playMode ? "play-unit" : "unit", Screen.Rules => playMode ? "play-rules" : "rules",
            Screen.Editor or Screen.Unit => playMode ? "play" : "roster", _ => "home" }));
    }
    protected override bool OnBackButtonPressed()
    {
        if (menu != null) { CloseMenu(); return true; }
        var input = this.GetVisualTreeDescendants().OfType<Entry>().FirstOrDefault(e => e.IsFocused);
        if (input != null)
        {
            _ = input.HideSoftInputAsync(CancellationToken.None);
            input.Unfocus(); return true;
        }
        if (busy) { importCancellation?.Cancel(); return true; }
        if (screen == Screen.Home) return base.OnBackButtonPressed();
        _ = Execute(Back); return true;
    }
    private Task Back()
    {
        if (screen == Screen.ImportReview) { pendingImport = null; return Go(reviewReturnScreen); }
        return Go(screen switch { Screen.Library or Screen.Export => utilityReturnScreen, Screen.Setup => Screen.Factions,
            Screen.Unit or Screen.Rules => Screen.Editor, _ => Screen.Home });
    }
    private async Task PrepareAsync()
    {
        await Session.RunAsync(s =>
        {
            if (screen == Screen.Home)
                homeArmies = s.LoadArmySummaries();
            if (screen == Screen.Setup) setupDataset = s.DatasetForArmy(ArmyList.New(selectedFaction));
            if (screen == Screen.Editor)
            {
                var choices = playMode ? null : s.BrowseLibrary(new("", fitsRemaining, hideLegends, hideCrucible));
                if (!playMode && !ReferenceEquals(preparedLibraryChoices, choices))
                {
                    libraryRows = choices!.Select(c =>
                        new LibraryRow(c, Category(s, c.Unit), s.OptionsFor(c.Unit.Key, ArmyCalculator.NextCopy(s.Army, s.Dataset, c.Unit)))).ToList();
                    preparedLibraryChoices = choices;
                }
                rosterGroups = s.Army.Entries.Where(e => e.AttachedToId.Length == 0 || s.Army.Entries.All(b => b.Id != e.AttachedToId))
                    .OrderBy(e => e, RosterOrder.ByNameThenSize)
                    .Select(e => new RosterGroup(e, MusterEffects.EffectiveUnit(s.Army, e, s.Dataset) is { } u ? Category(s, u) : "Others",
                        s.Result.Entries.Where(p => p.Entry.Id == e.Id || p.Entry.AttachedToId == e.Id)
                            .OrderBy(p => p.Entry.Id == e.Id ? 0 : 1)
                            .ThenBy(p => p.Entry, RosterOrder.ByNameThenSize).ToList())).ToList();
                rosterWargear = s.Army.Entries.ToDictionary(e => e.Id,
                    e => s.Dataset?.FindUnit(e.UnitKey) is { } unit ? WargearBadges(unit, e) : []);
            }
            if (screen == Screen.Unit) unitView = PrepareUnit(s);
            if (screen == Screen.Export) PrepareExport(s);
            if (screen == Screen.Rules) references = ArmyReferences.ForArmy(s.Army, s.Dataset);
        });
    }
    private static string Category(ArmySession s, UnitDefinition unit) => AlliedRules.IsAlly(s.Army, unit)
        ? "Allies: " + AlliedRules.OriginFaction(unit) : AlliedRules.Category(s.Army, unit, s.Dataset);
    private static int CategoryOrder(string category) => UnitCategories.Order(
        category.StartsWith("Allies: ", StringComparison.Ordinal) ? "Allies" : category);
    private void Render()
    {
        FullPageBuildCount++;
        ReleaseScroll();
        rendering = true; ResetPresentation(); page.Clear(); page.RowDefinitions.Clear(); libraryBody = null; editorColumns = null;
        try
        {
            switch (screen)
            {
                case Screen.Home: RenderHome(); break;
                case Screen.Factions: RenderFactions(); break;
                case Screen.Setup: RenderSetup(); break;
                case Screen.Editor: RenderEditor(); break;
                case Screen.Unit: RenderUnit(); break;
                case Screen.Rules: RenderRules(); break;
                case Screen.Library: RenderLibrarySettings(); break;
                case Screen.Export: RenderExport(); break;
                case Screen.ImportReview: RenderImportReview(); break;
            }
        }
        finally { rendering = false; }
    }
    private void SetPage(View header, View content)
    {
        if (content is ScrollView) RetainScroll(content, "page");
        page.RowDefinitions.Add(new(GridLength.Auto)); page.RowDefinitions.Add(new(GridLength.Star));
        page.Add(header, 0, 0); page.Add(content, 0, 1);
    }
    private View Heading(string title, bool back = true)
    {
        var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, Padding = new Thickness(8, 10), ColumnSpacing = 8 };
        if (back) row.Add(Action("‹", Back, "back", Ui.Background, true), 0);
        row.Add(Ui.Text(title, 23, true), 1);
        row.Add(Ui.Button("☰", ToggleMenu, "menu", Ui.Background, true), 2);
        return row;
    }
    private void CloseMenu() { if (menu != null) { root.Remove(menu); menu = null; } }
    private sealed record LibraryRow(LibraryChoice Choice, string Category, List<PointOption> Options);
    private sealed record RosterGroup(ArmyEntry Root, string Category, List<PricedEntry> Entries);
}
