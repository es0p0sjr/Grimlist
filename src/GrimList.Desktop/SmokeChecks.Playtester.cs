using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckPlaytesterFixes(MainWindow owner, string directory, string imagePath)
    {
        var path = Path.Combine(Path.GetDirectoryName(directory)!, "detachment-applicability.json");
        var data = DataJson.Read<Dataset>(File.ReadAllText(path));
        var selector = new DetachmentSelector(); var window = new Window { Owner = owner, Width = 1100, Height = 720, Content = selector };
        window.Show();
        try
        {
            foreach (var faction in new[] { "Imperial Fists", "Blood Angels", "Black Templars", "Dark Angels", "Space Wolves" })
            {
                selector.Begin(ArmyList.New(faction), data); window.UpdateLayout();
                var options = Find<ListBox>(selector, "OptionsList").Items.Cast<object>().ToList();
                string Value(object item, string name) => (string)item.GetType().GetProperty(name)!.GetValue(item)!;
                Check(options.Any(o => Value(o, "Name") == "Gladius Task Force"), faction + ": generic detachment hidden");
                Check(options.Any(o => Value(o, "Name") == "Angelic Inheritors") == (faction == "Blood Angels"), faction + ": exclusive detachment leak");
                var speartip = options.Single(o => Value(o, "Name") == "Armoured Speartip");
                Check(Value(speartip, "CostLabel") == "3 DP", "Windows did not display resolved DP cost");
            }
            Capture(window, imagePath + ".detachments.png");
        }
        finally { window.Close(); }
        var report = new BugReportWindow(owner, data, null); report.Show(); report.UpdateLayout();
        try
        {
            T Control<T>(string id) where T : FrameworkElement => Descendants<T>(report).Single(c => AutomationProperties.GetAutomationId(c) == id);
            Check(!Control<Button>("bug-share").IsEnabled && !Control<Button>("bug-save").IsEnabled, "Report can be sent before review");
            Control<TextBox>("bug-title").Text = "Detachment test"; Control<TextBox>("bug-description").Text = "Open the picker";
            Control<Button>("bug-review").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); report.UpdateLayout();
            Check(Control<Button>("bug-share").IsEnabled && Control<Button>("bug-save").IsEnabled, "Reviewed report cannot be exported");
            Check(Descendants<TextBox>(report).Any(t => t.IsReadOnly && t.Text.Contains("Diagnostics")), "Exact JSON preview missing");
            Capture(report, imagePath + ".bug-report.png");
            // Exercise the real unpackaged-WPF Windows share adapter without choosing
            // a recipient. Pump the dispatcher while native file loading completes.
            using var sharing = new WindowsFileSharing(report, new WindowsPaths(Path.Combine(Path.GetDirectoryName(directory)!, "report-share")));
            var package = new BugReportService().Export(new BugReportService().Prepare(new("UI smoke", "Native share adapter"), BugDiagnostics.Capture(null, null, "test", "Windows", "test"), []));
            var task = sharing.ShareAsync(package.Name, package.ContentType, package.Content);
            if (!task.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                _ = task.ContinueWith(_ => report.Dispatcher.BeginInvoke(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            Control<TextBox>("bug-title").Text = "Edited after review";
            Check(!Control<Button>("bug-share").IsEnabled, "Stale preview remains sendable");
        }
        finally { report.Close(); }
        File.WriteAllText(imagePath + ".playtester.txt", "PASS: Windows chapter selector/cost parity; report form, review gating and invalidation.");
    }
}
