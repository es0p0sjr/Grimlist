using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrimList.Application;
using GrimList.Core;
using Microsoft.Win32;

namespace GrimList.Desktop;

public sealed class BugReportWindow : Window
{
    private readonly IBugReportService reports = new BugReportService();
    private readonly List<byte[]> images = [];
    private PreparedBugReport? prepared;
    public BugReportWindow(Window owner, Dataset? data, ArmyList? army, GrimList.Platform.IPlatformPaths? paths = null)
    {
        Owner = owner; Title = "Report a Bug"; Width = 760; Height = 820; MinWidth = 460; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = owner.Background; Foreground = owner.Foreground;
        var sharing = new WindowsFileSharing(this, paths ?? new WindowsPaths()); IBugReportDestination destination = new ShareBugReport(sharing);
        Closed += (_, _) => sharing.Dispose();
        var diagnostics = BugDiagnostics.Capture(data, army, typeof(BugReportWindow).Assembly.GetName().Version?.ToString() ?? "Unknown", "Windows", Environment.OSVersion.VersionString);
        var body = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Label(string text) => body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 5) });
        Label("Describe what happened. Review everything before saving or sharing. Nothing is sent automatically.");
        TextBox Field(string label, int height, string id)
        {
            Label(label); var box = new TextBox { Height = height, AcceptsReturn = height > 40, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, id); body.Children.Add(box); return box;
        }
        var title = Field("Short title *", 36, "bug-title"); title.MaxLength = 160;
        var description = Field("Description / steps to reproduce *", 110, "bug-description");
        var expected = Field("Expected result (optional)", 65, "bug-expected");
        var actual = Field("Actual result (optional)", 65, "bug-actual");
        description.MaxLength = expected.MaxLength = actual.MaxLength = 12000;
        var imageList = new StackPanel(); body.Children.Add(imageList);
        var preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        var actions = new WrapPanel();
        Button Button(string text, string id, Func<Task> action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 6, 8, 6), Padding = new Thickness(12, 8, 12, 8) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, id);
            button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { ApplicationErrors.Record(ex); status.Text = BugReportPrivacy.Redact(ex.Message); } finally { button.IsEnabled = true; } };
            return button;
        }
        var save = Button("Save ZIP…", "bug-save", () =>
        {
            if (prepared == null) return Task.CompletedTask;
            var package = reports.Export(prepared); var dialog = new SaveFileDialog { FileName = package.Name, Filter = "Bug report ZIP|*.zip", DefaultExt = ".zip" };
            if (dialog.ShowDialog(this) == true) { File.WriteAllBytes(dialog.FileName, package.Content); status.Text = "Report saved. Attach the ZIP to your message."; }
            return Task.CompletedTask;
        });
        var share = Button("Share…", "bug-share", async () => { if (prepared != null) { await destination.SendAsync(reports.Export(prepared)); status.Text = "Choose a recipient in Windows Share. You can also save the ZIP."; } });
        void Invalidate() { prepared = null; preview.Visibility = Visibility.Collapsed; save.IsEnabled = share.IsEnabled = false; status.Text = "Review the report to enable Save and Share."; }
        foreach (var field in new[] { title, description, expected, actual }) field.TextChanged += (_, _) => Invalidate();
        void DrawImages()
        {
            imageList.Children.Clear();
            foreach (var bytes in images.ToList())
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = new MemoryStream(bytes); bitmap.EndInit(); bitmap.Freeze();
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Image { Source = bitmap, Height = 100, Width = 140, Stretch = Stretch.Uniform });
                row.Children.Add(Button("Remove image", "bug-remove-image", () => { images.Remove(bytes); DrawImages(); Invalidate(); return Task.CompletedTask; })); imageList.Children.Add(row);
            }
        }
        body.Children.Add(Button("Add screenshots / photos… (optional, up to 5)", "bug-images", async () =>
        {
            var picker = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            foreach (var path in picker.FileNames)
            {
                if (images.Count == BugReportService.MaximumImages) { status.Text = "Up to 5 images can be attached."; break; }
                using var input = File.OpenRead(path); var bytes = await BugReportImages.ReadSelectedAsync(input);
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                var frame = decoder.Frames[0];
                if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new InvalidDataException("Choose an image smaller than 40 megapixels.");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
                using var output = new MemoryStream(); encoder.Save(output); var normalized = BugReportImages.StripMetadata(output.ToArray());
                if (images.Sum(b => (long)b.Length) + normalized.Length > BugReportService.MaximumTotalBytes) throw new InvalidDataException("The images exceed 25 MB. Remove an image or choose a smaller one.");
                images.Add(normalized); DrawImages(); Invalidate();
            }
        }));
        body.Children.Add(Button("Review report", "bug-review", () =>
        {
            prepared = reports.Prepare(new(title.Text, description.Text, expected.Text, actual.Text), diagnostics, images);
            preview.Text = prepared.Json; preview.Visibility = Visibility.Visible; save.IsEnabled = share.IsEnabled = true;
            status.Text = "ZIP contents: bug-report.json below, plus only the images shown above. Review your text and images for private information.";
            return Task.CompletedTask;
        }));
        body.Children.Add(status); body.Children.Add(preview); actions.Children.Add(save); actions.Children.Add(share); body.Children.Add(actions);
        Invalidate(); DarkWindow.Apply(this);
    }
}
