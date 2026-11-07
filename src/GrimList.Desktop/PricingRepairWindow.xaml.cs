using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using GrimList.Core;
using Microsoft.Win32;

namespace GrimList.Desktop;
public partial class PricingRepairWindow : Window
{
    private readonly IArmyRepository store;
    private readonly LibraryWorkflow workflow;
    private CancellationTokenSource? cancellation;
    private PricingRepairPreview? preview;
    private List<PricingRepairRow> rows = [];
    private bool closed;
    private string CacheRoot => Path.Combine(store.DirectoryPath, "sources");
    public PricingRepairWindow(IArmyRepository store)
    {
        InitializeComponent(); DarkWindow.Apply(this); this.store = store; workflow = new(store);
        Results.RowHeight = double.NaN;
        var data = store.LoadDataset(); rows = workflow.ScanPrices(); Results.ItemsSource = rows;
        Summary.Text = data == null ? "Import a unit library first." : $"{rows.Count} units have unavailable prices across the library.";
        CachedButton.IsEnabled = data != null && BsDataClient.CachedArchive(data, CacheRoot) != null;
        OnlineButton.IsEnabled = LocalButton.IsEnabled = rows.Count > 0 && data?.Source.Edition is "10" or "11";
        SourceText.Text = CachedButton.IsEnabled ? "A saved repository is available for an offline check." : "Older imports saved only the results. Download once or choose the original repository ZIP to retry them.";
        Closed += (_, _) => { closed = true; cancellation?.Cancel(); };
    }
    private void Stop(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void Busy(bool busy)
    {
        OnlineButton.IsEnabled = LocalButton.IsEnabled = ReportButton.IsEnabled = !busy;
        CachedButton.IsEnabled = !busy && store.LoadDataset() is { } data && BsDataClient.CachedArchive(data, CacheRoot) != null;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = !busy && preview?.Recovered > 0;
    }
    private async Task Check(Func<Dataset, IProgress<string>, CancellationToken, Task<Dataset>> read)
    {
        if (cancellation != null) return;
        using var tokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(10)); cancellation = tokenSource;
        preview = null; Busy(true); ImpactText.Text = "";
        try
        {
            var current = store.LoadDataset() ?? throw new InvalidDataException("Import a unit library first.");
            rows = workflow.ScanPrices(); Results.ItemsSource = rows; SourceText.Text = "Checking source data…";
            var progress = new Progress<string>(s => { if (!closed) Summary.Text = s; });
            var incoming = await read(current, progress, tokenSource.Token);
            var candidate = await workflow.PreviewRepairAsync(incoming, tokenSource.Token);
            tokenSource.Token.ThrowIfCancellationRequested();
            if (!closed) ShowPreview(candidate, incoming.Source);
        }
        catch (OperationCanceledException) { if (!closed) Summary.Text = "Check cancelled or timed out. Your library is unchanged."; }
        catch (Exception ex) { if (!closed) Summary.Text = "No repairs applied. " + ex.Message; }
        finally { cancellation = null; if (!closed) Busy(false); }
    }
    internal void ShowPreview(PricingRepairPreview candidate, SourceInfo source)
    {
        preview = candidate; rows = candidate.Rows; Results.ItemsSource = rows;
        Summary.Text = $"{rows.Count} checked · {candidate.Recovered} recovered · {candidate.Unresolved} need importer or source support. Skipped: {candidate.SkippedCrucible} Crucible, {candidate.SkippedReferences} battle-only references.";
        SourceText.Text = $"{source.Name} · {source.Version} · {(source.Date.Length == 0 ? "publication date unknown" : source.Date)}\n{source.Url}";
        var before = DataJson.Read<Dataset>(candidate.BaseSnapshot);
        ImpactText.Text = string.Join("\n", store.LoadArmies().Select(a => (a.Name, Old: ArmyCalculator.Calculate(a, before), Next: ArmyCalculator.Calculate(a, candidate.Dataset)))
            .Where(a => a.Old.Total != a.Next.Total || a.Old.Complete != a.Next.Complete)
            .Select(a => $"{a.Name}: {a.Old.Total} → {a.Next.Total} pts{(a.Next.Complete ? "" : " + unknown")}"));
        ApplyButton.IsEnabled = candidate.Recovered > 0;
    }
    private async void FindOnline(object sender, RoutedEventArgs e) => await Check(async (current, progress, token) =>
    {
        return (await workflow.DownloadAsync(true, current.Source.Edition, null, progress, token)).Dataset;
    });
    private async void CheckCache(object sender, RoutedEventArgs e) => await Check(async (current, progress, token) =>
        (await BsDataClient.ReadCached(BsDataClient.CachedArchive(current, CacheRoot) ?? throw new InvalidDataException("No cached repository available."), progress, token)).Dataset);
    private async void ChooseSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "BSData repository ZIP|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        await Check(async (current, progress, token) =>
        {
            if (new FileInfo(dialog.FileName).Length > 50_000_000) throw new InvalidDataException("Choose a repository ZIP smaller than 50 MB.");
            var bytes = await File.ReadAllBytesAsync(dialog.FileName, token);
            return (await workflow.ReadBsDataAsync(bytes, current.Source.Edition, progress, token)).Dataset;
        });
    }
    private void Apply(object sender, RoutedEventArgs e)
    {
        if (preview == null) return;
        try { workflow.ApplyRepair(preview); DialogResult = true; }
        catch (Exception ex) { ApplyButton.IsEnabled = false; Summary.Text = "No repairs applied. " + ex.Message; }
    }
    private void ExportReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "GrimList-pricing-report.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, DataJson.Write(rows)); }
        catch (Exception ex) { Summary.Text = "Report could not be saved. " + ex.Message; }
    }
}
