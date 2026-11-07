using GrimList.Application;
using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed class BugReportPage : ContentPage
{
    private readonly IBugReportService reports = new BugReportService();
    private readonly List<byte[]> images = [];
    private PreparedBugReport? prepared;
    public BugReportPage(Dataset? data, ArmyList? army, IMobileDocumentSaver documents)
    {
        Title = "Report a Bug"; BackgroundColor = Ui.Background;
        var diagnostics = BugDiagnostics.Capture(data, army, AppInfo.Current.VersionString, "Android", DeviceInfo.Current.VersionString);
        IBugReportDestination destination = new ShareBugReport(new MobileFileSharing());
        var body = new VerticalStackLayout { Spacing = 12, Padding = 18 };
        Content = Ui.Scroll(body);
        body.Add(Ui.Text("Report a Bug", 22, true));
        body.Add(Ui.Text("Describe what happened. Review everything before saving or sharing. Nothing is sent automatically.", 14));
        var title = Ui.Entry("Short title *", "", "bug-title"); title.MaxLength = 160; body.Add(title);
        Editor Field(string label, string id, int height)
        {
            body.Add(Ui.Text(label, 14)); var field = new Editor { AutomationId = id, HeightRequest = height, MaxLength = 12000,
                TextColor = Colors.White, BackgroundColor = Ui.Input, AutoSize = EditorAutoSizeOption.TextChanges };
            body.Add(field); return field;
        }
        var description = Field("Description / steps to reproduce *", "bug-description", 110);
        var expected = Field("Expected result (optional)", "bug-expected", 70);
        var actual = Field("Actual result (optional)", "bug-actual", 70);
        var imageList = new VerticalStackLayout { Spacing = 8 }; body.Add(imageList);
        var preview = new Editor { IsReadOnly = true, HeightRequest = 350, TextColor = Colors.White, BackgroundColor = Ui.Input, IsVisible = false, AutomationId = "bug-preview" };
        var status = Ui.Text("Review the report to enable Save and Share.", 14);
        var busy = false;
        Button Action(string text, string id, Func<Task> action) => Ui.Button(text, async () =>
        {
            if (busy) return; busy = true;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ApplicationErrors.Record(ex); await DisplayAlertAsync("Report a Bug", BugReportPrivacy.Redact(ex.Message), "OK"); }
            finally { busy = false; }
        }, id);
        var save = Action("Save ZIP…", "bug-save", async () =>
        {
            if (prepared == null) return; var package = reports.Export(prepared);
            if (await documents.SaveAsync(package.Name, package.ContentType, package.Content)) status.Text = "Report saved. Attach the ZIP to your message.";
        });
        var share = Action("Share…", "bug-share", async () => { if (prepared != null) await destination.SendAsync(reports.Export(prepared)); });
        void Invalidate() { prepared = null; preview.IsVisible = false; save.IsEnabled = share.IsEnabled = false; status.Text = "Review the report to enable Save and Share."; }
        title.TextChanged += (_, _) => Invalidate();
        foreach (var field in new[] { description, expected, actual }) field.TextChanged += (_, _) => Invalidate();
        void DrawImages()
        {
            imageList.Clear();
            foreach (var bytes in images.ToList())
            {
                imageList.Add(new Image { Source = ImageSource.FromStream(() => new MemoryStream(bytes)), HeightRequest = 140, Aspect = Aspect.AspectFit });
                imageList.Add(Action("Remove image", "bug-remove-image", () => { images.Remove(bytes); DrawImages(); Invalidate(); return Task.CompletedTask; }));
            }
        }
        body.Add(Action("Add screenshots / photos (optional, up to 5)", "bug-images", async () =>
        {
            var picked = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Select images to attach", FileTypes = FilePickerFileType.Images });
            foreach (var selected in picked ?? [])
            {
                if (images.Count == BugReportService.MaximumImages) { status.Text = "Up to 5 images can be attached."; break; }
                using var input = await selected.OpenReadAsync(); var bytes = await BugReportImages.ReadSelectedAsync(input);
                var normalized = await Task.Run(() => NormalizeImage(bytes));
                if (images.Sum(b => (long)b.Length) + normalized.Length > BugReportService.MaximumTotalBytes) throw new InvalidDataException("The images exceed 25 MB. Remove an image or choose a smaller one.");
                images.Add(normalized); DrawImages(); Invalidate();
            }
        }));
        body.Add(Action("Review report", "bug-review", () =>
        {
            prepared = reports.Prepare(new(title.Text ?? "", description.Text ?? "", expected.Text ?? "", actual.Text ?? ""), diagnostics, images);
            preview.Text = prepared.Json; preview.IsVisible = true; save.IsEnabled = share.IsEnabled = true;
            status.Text = "ZIP contents: bug-report.json below, plus only the images shown above. Review your text and images for private information.";
            return Task.CompletedTask;
        }));
        body.Add(status); body.Add(preview); body.Add(save); body.Add(share);
        body.Add(Action("Close", "bug-close", async () => await Navigation.PopModalAsync()));
        Invalidate();
    }
    private static byte[] NormalizeImage(byte[] bytes)
    {
        using var options = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, options);
        if (options.OutWidth <= 0 || options.OutHeight <= 0 || (long)options.OutWidth * options.OutHeight > 40_000_000)
            throw new InvalidDataException("Select a valid image smaller than 40 megapixels.");
        using var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length)
            ?? throw new InvalidDataException("This image could not be opened. Images are optional.");
        using var output = new MemoryStream(); bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, output);
        return BugReportImages.StripMetadata(output.ToArray());
    }
}
