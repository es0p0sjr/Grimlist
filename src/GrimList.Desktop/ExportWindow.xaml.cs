using System.IO;
using System.Net.Http;
using System.Windows;
using GrimList.Core;
using Microsoft.Win32;

namespace GrimList.Desktop;

public partial class ExportWindow : Window
{
    private readonly ArmyList army;
    private readonly Dataset? data;
    private readonly IRosterPdfExporter pdf;
    private readonly IClipboardService clipboard;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string? yellowscribe;
    public ExportWindow(ArmyList army, Dataset? data, IRosterPdfExporter? pdf = null, IClipboardService? clipboard = null)
    {
        InitializeComponent(); DarkWindow.Apply(this);
        this.army = army; this.data = data;
        this.pdf = pdf ?? new DesktopPdfExporter(); this.clipboard = clipboard ?? new DesktopClipboard();
        ArmyTitle.Text = army.Name + " · " + army.Faction;
        var validation = ArmyCalculator.Calculate(army, data);
        ValidationStatus.Text = validation.ValidationStatus;
        ValidationStatus.Foreground = (System.Windows.Media.Brush)FindResource(validation.Validated ? "Success" : "Warning");
        ValidationStatus.ToolTip = validation.Validated ? "Detachment, Character, warlord, points and unit limits passed."
            : string.Join("\n", validation.Issues);
        TextPreview.Text = RosterExport.Text(army, data);
        try { yellowscribe = YellowscribeExport.Roster(army, data); YellowscribeProblem.Text = "Ready to export."; }
        catch (InvalidDataException ex) { YellowscribeProblem.Text = ex.Message; }
        SaveYellowscribeButton.IsEnabled = SendYellowscribeButton.IsEnabled = yellowscribe != null;
        Closed += (_, _) => cancellation.Cancel();
    }
    private string FileName => string.Concat(army.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim() is { Length: > 0 } name ? name : "army";
    // Shared by the file-dialog actions and native regression checks.
    internal void SaveToFile(string path, string format)
    {
        var bytes = format switch
        {
            "pdf" => pdf.Create(army, data, IncludeRules.IsChecked == true, AllWeapons.IsChecked == true),
            "ros" => System.Text.Encoding.UTF8.GetBytes(yellowscribe ?? throw new InvalidDataException(YellowscribeProblem.Text)),
            "txt" => System.Text.Encoding.UTF8.GetBytes(TextPreview.Text),
            _ => throw new InvalidDataException("Unknown export format.")
        };
        File.WriteAllBytes(path, bytes);
        ExportStatus.Text = "Saved " + path;
    }
    private void Save(string format, string label)
    {
        var dialog = new SaveFileDialog { FileName = FileName + "." + format, DefaultExt = "." + format, Filter = label + "|*." + format };
        if (dialog.ShowDialog(this) != true) return;
        try { SaveToFile(dialog.FileName, format); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ExportStatus.Text = ex.Message; }
    }
    private void SaveText(object sender, RoutedEventArgs e) => Save("txt", "Text list");
    private void SavePdf(object sender, RoutedEventArgs e) => Save("pdf", "PDF roster and datasheets");
    private void SaveYellowscribe(object sender, RoutedEventArgs e) => Save("ros", "Yellowscribe roster");
    private async void CopyText(object sender, RoutedEventArgs e)
    {
        try { await clipboard.SetTextAsync(TextPreview.Text); ExportStatus.Text = "List copied."; }
        catch (System.Runtime.InteropServices.COMException) { ExportStatus.Text = "Clipboard is busy. Try again or save the text file."; }
    }
    private async void SendYellowscribe(object sender, RoutedEventArgs e)
    {
        if (yellowscribe == null) return;
        SendYellowscribeButton.IsEnabled = false; ArmyCode.Text = ""; ExportStatus.Text = "Sending roster to Yellowscribe…";
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            ArmyCode.Text = await YellowscribeClient.Upload(client, yellowscribe, cancellation.Token);
            ExportStatus.Text = "Select and copy the army code into the Yellowscribe TTS mod.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or OperationCanceledException)
        { ExportStatus.Text = ex is OperationCanceledException ? "Yellowscribe request cancelled or timed out. Your local roster is unchanged." : ex.Message; }
        finally { SendYellowscribeButton.IsEnabled = true; }
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
