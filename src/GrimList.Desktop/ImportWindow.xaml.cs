using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using GrimList.Core;
using Microsoft.Win32;

namespace GrimList.Desktop;
public partial class ImportWindow : Window
{
    private readonly IArmyRepository store;
    private readonly LibraryWorkflow workflow;
    private LibraryPreview? prepared;
    private Dataset? preview;
    private Dataset? loadedSource;
    private readonly Func<IProgress<string>, Task>? refreshLibrary;
    private bool busy, committed, closeRequested, readDocumentMetadata;
    private readonly HashSet<string> editedSourceFields = [];
    private int progressGeneration;
    internal Task PendingOperation { get; private set; } = Task.CompletedTask;
    private CancellationTokenSource? downloadCancellation;
    private List<string> downloadWarnings = [];
    private SourceInfo? downloadedSource;
    private bool settingSource;
    private readonly ImportLayer communityLayer;
    public ImportWindow(IArmyRepository store, string? faction = null, bool downloadOnOpen = false, ImportLayer? initialLayer = null,
        string? edition = null, Func<IProgress<string>, Task>? refreshLibrary = null)
    {
        InitializeComponent(); DarkWindow.Apply(this); this.store = store; workflow = new(store, new WindowsPdfTextExtractor());
        this.refreshLibrary = refreshLibrary;
        communityLayer = initialLayer ?? ImportLayer.Combined;
        PreviewGrid.RowHeight = 48;
        FallbackFaction.ItemsSource = new[] { "Auto-detect" }.Concat(MfmImporter.Factions); FallbackFaction.SelectedIndex = 0;
        Provider.SelectedIndex = 0;
        DownloadEdition.SelectedIndex = edition == "10" ? 0 : 1;
        SetProvider(this, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object>(), new List<object>()));
        LayerPicker.SelectedIndex = (int)(initialLayer ?? ImportLayer.Points);
        if (initialLayer == ImportLayer.Profiles)
            Summary.Text = "Download BSData or choose the original catalogue ZIP to add datasheets and categories. Your points will be preserved.";
        foreach (var box in new[] { SourceName, VersionBox, DateBox, UrlBox }) box.TextChanged += SourceChanged;
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            if (downloadCancellation != null) { closeRequested = true; StopDownload(this, new RoutedEventArgs()); }
        };
        if (downloadOnOpen) ContentRendered += (_, _) => DownloadPoints(this, new RoutedEventArgs());
    }
    private bool Community => Provider.SelectedIndex == 0;
    private async void RepairMissingPoints(object sender, RoutedEventArgs e)
    {
        if (busy || committed) return;
        var dialog = new PricingRepairWindow(store) { Owner = this };
        if (dialog.ShowDialog() == true) { committed = true; await (PendingOperation = ApplyAsync()); }
    }
    private string SelectedEdition => DownloadEdition.SelectedIndex == 0 ? "10" : "11";
    private void SetProvider(object sender, SelectionChangedEventArgs e)
    {
        if (DownloadFaction == null || DownloadEdition == null) return;
        DownloadFaction.ItemsSource = Community ? new[] { "All armies" } : new[] { "All factions" }.Concat(MfmImporter.Factions);
        DownloadFaction.SelectedIndex = 0;
        DownloadEdition.Visibility = Community ? Visibility.Visible : Visibility.Collapsed;
    }
    private SourceInfo Source => new(SourceName.Text.Trim(), VersionBox.Text.Trim(), DateBox.Text.Trim(), UrlBox.Text.Trim(), Edition: EditionBox.Text.Trim(), ContentHash: downloadedSource?.ContentHash ?? "");
    private ImportLayer Layer => (ImportLayer)Math.Max(0, LayerPicker.SelectedIndex);
    private void InvalidatePreview(object sender, SelectionChangedEventArgs e) => ClearPreview();
    private void ScopeChanged(object sender, RoutedEventArgs e) => ClearPreview();
    private void SourceChanged(object sender, TextChangedEventArgs e)
    {
        ClearPreview();
        if (settingSource) return;
        downloadedSource = null;
        if (sender == RawText)
        {
            readDocumentMetadata = true; editedSourceFields.Clear();
            loadedSource = null;
            downloadWarnings = [];
        }
        else if (readDocumentMetadata && sender is TextBox box) editedSourceFields.Add(box.Name);
    }
    private void SetMetadata(SourceInfo source)
    {
        settingSource = true;
        SourceName.Text = source.Name; VersionBox.Text = source.Version; DateBox.Text = source.Date; UrlBox.Text = source.Url; EditionBox.Text = source.Edition;
        settingSource = false;
    }
    private void ClearPreview()
    {
        preview = null;
        prepared = null;
        if (ApplyButton == null) return;
        ApplyButton.IsEnabled = false; PreviewGrid.ItemsSource = null; ChangesList.ItemsSource = null;
        Summary.Text = "Source changed. Preview it before applying.";
    }
    private void SetBusy(bool busy)
    {
        this.busy = busy;
        var editable = !busy && !committed;
        RepairButton.IsEnabled = DownloadButton.IsEnabled = BrowseButton.IsEnabled = PreviewButton.IsEnabled = RawText.IsEnabled = LayerPicker.IsEnabled = CompleteFaction.IsEnabled = DownloadFaction.IsEnabled = editable;
        foreach (var box in new[] { SourceName, VersionBox, DateBox, UrlBox, EditionBox }) box.IsEnabled = editable;
        FallbackFaction.IsEnabled = Provider.IsEnabled = DownloadEdition.IsEnabled = editable;
        StopButton.Visibility = busy && downloadCancellation != null ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = downloadCancellation?.IsCancellationRequested != true;
        CancelButton.IsEnabled = !busy || downloadCancellation != null;
        ImportProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ImportProgress.IsIndeterminate = busy;
        ApplyButton.IsEnabled = !busy && (committed || prepared?.CanApply == true);
        if (!busy) { progressGeneration++; if (closeRequested) { closeRequested = false; DialogResult = false; } }
    }
    private IProgress<string> StageProgress()
    {
        var generation = ++progressGeneration;
        return new Progress<string>(message =>
        {
            if (busy && generation == progressGeneration && downloadCancellation?.IsCancellationRequested != true) Summary.Text = message;
        });
    }
    private void Stage(string message) { progressGeneration++; Summary.Text = message; }
    private void StopDownload(object sender, RoutedEventArgs e)
    {
        downloadCancellation?.Cancel(); StopButton.IsEnabled = false;
        Stage("Stopping… Waiting for the current step to finish. Your library has not changed.");
    }
    private void SetLoadedSource(Dataset dataset, List<string> warnings)
    {
        settingSource = true;
        RawText.Clear();
        settingSource = false;
        loadedSource = dataset;
        readDocumentMetadata = false;
        SetMetadata(dataset.Source); downloadedSource = dataset.Source; downloadWarnings = warnings;
        RawText.Visibility = Visibility.Collapsed;
        LoadedSourceText.Visibility = Visibility.Visible;
        LoadedSourceText.Text = $"{dataset.Source.Name}\n{dataset.Units.Count} units loaded.\n\nPreview the changes before applying this source.";
    }
    private async void DownloadPoints(object sender, RoutedEventArgs e) { if (!busy) await (PendingOperation = DownloadAsync()); }
    private async Task DownloadAsync()
    {
        if (busy || committed) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); downloadCancellation = cancellation;
        ClearPreview();
        SetBusy(true);
        Stage("Downloading data · Connecting to the source…");
        try
        {
            var progress = StageProgress();
            var downloaded = await workflow.DownloadAsync(Community, SelectedEdition,
                DownloadFaction.SelectedIndex == 0 ? null : (string)DownloadFaction.SelectedItem, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetLoadedSource(downloaded.Dataset, downloaded.Warnings);
            LayerPicker.SelectedIndex = Community ? (int)communityLayer : 0; CompleteFaction.IsChecked = false;
            FileLabel.Text = Community ? $"BSData + MFM · {downloaded.Dataset.Units.Select(u => u.Faction).Distinct().Count()} armies cached locally" : "MFM points downloaded · source snapshot cached locally";
            await PreparePreviewAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { Summary.Text = "Download cancelled or timed out. Local data unchanged."; }
        catch (HttpRequestException ex) { Summary.Text = "Download unavailable. Local data unchanged. " + ex.Message + (Community ? " Use Choose local source with a BSData repository ZIP saved from GitHub." : ""); }
        catch (InvalidDataException ex) { Summary.Text = "Downloaded data could not be imported. Local data unchanged. " + ex.Message; }
        catch (Exception ex) { Summary.Text = "Update failed. Local data unchanged. " + ex.Message; }
        finally { downloadCancellation = null; SetBusy(false); }
    }
    private async void Browse(object sender, RoutedEventArgs e) { if (!busy) await (PendingOperation = BrowseAsync()); }
    private async Task BrowseAsync()
    {
        if (busy || committed) return;
        var dialog = new OpenFileDialog { Filter = "Data sources|*.zip;*.cat;*.gst;*.pdf;*.txt;*.json|BSData repository ZIP|*.zip|BSData catalogue|*.cat;*.gst;*.json|MFM PDF / text|*.pdf;*.txt|GrimList JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); downloadCancellation = cancellation;
        ClearPreview();
        SetBusy(true);
        try
        {
            if (new FileInfo(dialog.FileName).Length > LibraryWorkflow.MaximumDocumentBytes) throw new InvalidDataException("Choose a source no larger than 128 MB.");
            Stage("Reading source · Loading points, datasheets and rules…");
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (extension is ".zip" or ".cat" or ".gst" or ".json")
            {
                await LoadDocumentAsync(dialog.FileName, cancellation.Token);
            }
            else
            {
                var text = extension == ".pdf" ? await new WindowsPdfTextExtractor().ExtractAsync(dialog.FileName, Path.Combine(store.DirectoryPath, "temp")) : await File.ReadAllTextAsync(dialog.FileName, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                RawText.Visibility = Visibility.Visible; LoadedSourceText.Visibility = Visibility.Collapsed;
                RawText.Text = text; LayerPicker.SelectedIndex = 0; VersionBox.Text = MfmImporter.GuessVersion(text); DateBox.Clear(); EditionBox.Clear();
            }
            CompleteFaction.IsChecked = false;
            FileLabel.Text = Path.GetFileName(dialog.FileName);
            Stage("Source loaded. Check its version, date and import layer, then preview.");
        }
        catch (OperationCanceledException) { Summary.Text = "Import cancelled or timed out. Local data unchanged."; }
        catch (Exception ex) { Summary.Text = ex.Message; }
        finally { downloadCancellation = null; SetBusy(false); }
    }
    internal async Task LoadDocumentAsync(string path, CancellationToken cancellation = default)
    {
        using var document = new GrimList.Platform.PickedDocument(Path.GetFileName(path), File.OpenRead(path));
        var result = await workflow.ReadDocumentAsync(document, SelectedEdition, StageProgress(), cancellation);
        cancellation.ThrowIfCancellationRequested();
        SetLoadedSource(result.Dataset, result.Warnings);
        LayerPicker.SelectedIndex = communityLayer == ImportLayer.Profiles || result.Dataset.ReferenceOnly || result.Dataset.RulesPacks is { Count: > 0 }
            || result.Dataset.Units.All(u => u.Options.Count == 0) ? 1 : (int)communityLayer;
    }
    internal async Task LoadCommunity(List<BsDataDocument> documents, byte[] bytes)
    {
        var progress = StageProgress();
        var result = await workflow.ReadBsDataAsync(documents, bytes, SelectedEdition, progress, downloadCancellation?.Token ?? CancellationToken.None);
        SetLoadedSource(result.Dataset, result.Warnings);
        LayerPicker.SelectedIndex = (int)communityLayer; CompleteFaction.IsChecked = false;
        Summary.Text = "BSData categories, points and datasheets loaded. Preview before applying.";
    }
    private async void Preview(object sender, RoutedEventArgs e) { if (!busy) await (PendingOperation = PreviewAsync()); }
    private async Task PreviewAsync()
    {
        if (busy || committed) return;
        using var cancellation = new CancellationTokenSource(); downloadCancellation = cancellation;
        ClearPreview(); SetBusy(true);
        try { await PreparePreviewAsync(cancellation.Token); }
        catch (OperationCanceledException) { ClearPreview(); Summary.Text = "Preview cancelled. Local data unchanged."; }
        catch (Exception ex) { ClearPreview(); Summary.Text = "Import not ready: " + ex.Message; }
        finally { downloadCancellation = null; SetBusy(false); }
    }
    private async Task PreparePreviewAsync(CancellationToken cancellation)
    {
        Stage("Preparing changes · Reading the source…");
        var text = RawText.Text; var source = Source; var layer = Layer; var incoming = loadedSource;
        var fallback = FallbackFaction.SelectedIndex > 0 ? (string)FallbackFaction.SelectedItem : null;
        var useDocumentSource = readDocumentMetadata;
        var parsed = await Task.Run(() => incoming == null ? workflow.Parse(text, source, layer, fallback, useDocumentSource)
            : new ImportPreview(incoming with { Source = source }, []), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (useDocumentSource)
        {
            // Pasted JSON is parsed in the background. Preserve individual metadata
            // edits made before preview without dropping the document's other fields.
            var metadata = parsed.Dataset.Source with
            {
                Name = editedSourceFields.Contains(nameof(SourceName)) ? source.Name : parsed.Dataset.Source.Name,
                Version = editedSourceFields.Contains(nameof(VersionBox)) ? source.Version : parsed.Dataset.Source.Version,
                Date = editedSourceFields.Contains(nameof(DateBox)) ? source.Date : parsed.Dataset.Source.Date,
                Url = editedSourceFields.Contains(nameof(UrlBox)) ? source.Url : parsed.Dataset.Source.Url,
                Edition = editedSourceFields.Contains(nameof(EditionBox)) ? source.Edition : parsed.Dataset.Source.Edition
            };
            parsed = parsed with { Dataset = parsed.Dataset with { Source = metadata } };
            SetMetadata(metadata); readDocumentMetadata = false;
        }
        if (layer == ImportLayer.Profiles && parsed.Dataset.Units.Count > 0 && parsed.Dataset.Units.All(u => u.Profile == null))
            throw new InvalidDataException("This file contains only points. Download BSData or choose the original catalogue ZIP; a points-only cache cannot supply datasheets.");
        var candidate = await workflow.PrepareAsync(parsed.Dataset, layer, CompleteFaction.IsChecked == true,
            downloadWarnings.Concat(parsed.Warnings).ToList(), cancellation, StageProgress());
        cancellation.ThrowIfCancellationRequested();
        Stage("Preparing changes · Building the review…");
        var rows = await Task.Run(() => candidate.Dataset.Units.SelectMany(u => (u.Options.Count > 0 ? u.Options : [new PointOption(0, 0)]).Select(o => new
        { u.Faction, Unit = u.Name, Category = u.DisplayCategory, Models = o.Models == 0 ? "—" : o.Models.ToString(), Copy = o.Models == 0 ? "—" : o.FromCopy.ToString(), Points = layer == ImportLayer.Profiles || o.Models == 0 ? "—" : o.Points.ToString(), Profile = u.Profile == null ? "—" : $"{u.Profile.Characteristics?.Count ?? u.Profile.Models?.Sum(m => m.Characteristics.Count) ?? 0} stats / {u.Profile.Weapons?.Count ?? 0} weapons\n{u.Profile.Abilities?.Count ?? 0} abilities" })).ToList(), cancellation);
        cancellation.ThrowIfCancellationRequested();
        prepared = candidate; preview = candidate.Dataset;
        try
        {
            var changes = prepared.Prices; var descriptions = prepared.Changes;
            PreviewGrid.ItemsSource = rows;
            ChangesList.ItemsSource = descriptions.Select(d => new { Description = d });
            if (parsed.Dataset.Units.Count == 0 && parsed.Dataset.RulesPacks is not { Count: > 0 })
            {
                PreviewGrid.ItemsSource = (parsed.Dataset.ReferenceCards ?? []).Select(c => new
                { c.Faction, Unit = c.Name, Category = c.Section, Models = "—", Copy = "—", Points = c.Cost, Profile = c.Detachment }).ToList();
                descriptions.AddRange((parsed.Dataset.ReferenceCards ?? []).GroupBy(c => c.Section).Select(g => $"{g.Count()} {g.Key} reference cards."));
                descriptions.Add("Units, points, loadouts and saved army selections are preserved.");
                ChangesList.ItemsSource = descriptions.Select(d => new { Description = d });
            }
            var impacted = prepared.Armies;
            Summary.Text = $"{parsed.Dataset.Units.Select(u => u.Faction).Distinct().Count()} factions · {parsed.Dataset.Units.Count} incoming units · {parsed.Dataset.Units.Count(u => u.Profile != null)} datasheets · {changes.Count} price changes · {impacted.Count} affected armies";
            if (parsed.Dataset.RulesPacks is { Count: > 0 } packs)
                Summary.Text = $"{packs.Count} rules packs · {packs.Sum(p => p.Units.Count)} unit updates · {packs.Sum(p => p.Cards.Count)} rules · {packs.Sum(p => p.Detachments.Count)} detachments · {impacted.Count} affected armies\n"
                    + string.Join("\n", packs.Select(p => $"{p.Source.Name}: {p.Status}, {p.Coverage} coverage. {p.Notes}"));
            else if (parsed.Dataset.ReferenceOnly) Summary.Text = $"{parsed.Dataset.ReferenceCards?.Count ?? 0} reference cards · {parsed.Dataset.Detachments?.Count ?? 0} detachments · unit data preserved";
            else Summary.Text += "\n" + MfmPoints.Summary(preview);
            if (prepared.Stratagems is { } stratagems)
            {
                Summary.Text += "\n" + stratagems.Message;
                descriptions.AddRange(stratagems.Coverage);
                descriptions.AddRange(prepared.ReferenceChanges.Select(c => $"{c.Faction} / {c.Detachment} / {c.Name}: {c.Status}. {c.Message}"));
                ChangesList.ItemsSource = descriptions.Select(d => new { Description = d });
            }
            Warnings.Text = string.Join("\n", prepared.Warnings.Concat(impacted.Select(a => $"{a.Name}: {a.Before.Total} → {a.After.Total} / {a.Limit} pts{(a.After.Complete ? "" : " · incomplete")}{(a.After.Total > a.Limit ? " · OVER LIMIT" : "")}" )).Distinct());
            Tabs.SelectedIndex = 1;
        }
        catch { ClearPreview(); throw; }
    }
    private async void Apply(object sender, RoutedEventArgs e) { if (!busy) await (PendingOperation = ApplyAsync()); }
    private async Task ApplyAsync()
    {
        if (busy || (!committed && prepared?.CanApply != true)) return;
        SetBusy(true);
        try
        {
            if (!committed)
            {
                Stage("Saving update · Saving points, datasheets and rules…");
                await workflow.ApplyAsync(prepared!);
                committed = true;
            }
            Stage("Finishing update · Loading the updated library…");
            if (refreshLibrary != null) await refreshLibrary(StageProgress());
            Stage("Update complete.");
            SetBusy(false); DialogResult = true;
        }
        catch (Exception ex)
        {
            if (!committed) { ClearPreview(); Stage("Nothing applied: " + ex.Message); }
            else { Stage("Update saved, but refreshing the library failed. Retry finishing the update. " + ex.Message); ApplyButton.Content = "Retry finishing update"; }
        }
        finally { SetBusy(false); }
    }
}
