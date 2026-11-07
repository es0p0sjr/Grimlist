using System.Text;
using Android.Graphics.Pdf;
using Android.OS;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Mobile;

public sealed class AndroidPdfTextExtractor : IPdfTextExtractor
{
    public Task<string> ExtractAsync(string path, string temporaryDirectory, CancellationToken cancellation = default)
        => Task.Run(() =>
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
                throw new NotSupportedException("PDF text import requires Android 15 or newer. On this device import MFM text or a library JSON exported from Windows instead.");
            using var file = new Java.IO.File(path);
            using var descriptor = ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly)!;
            using var renderer = new PdfRenderer(descriptor);
            var layout = new StringBuilder();
            if (renderer.PageCount > 500) throw new InvalidDataException("Choose a PDF with at most 500 pages.");
            for (var p = 0; p < renderer.PageCount; p++)
            {
                cancellation.ThrowIfCancellationRequested();
                using var page = renderer.OpenPage(p);
                // API 35 returns page text without content bounds. Search supplies word
                // positions, allowing the shared MFM column reader to retain columns.
                var fragments = new List<(string Text, float X, float Y)>();
                var pageStrings = new List<string>();
                foreach (var content in page.TextContents ?? []) pageStrings.Add(content.Text ?? "");
                var pageText = string.Join("\n", pageStrings);
                var words = System.Text.RegularExpressions.Regex.Matches(pageText, @"\S+").Select(m => m.Value).Distinct().ToList();
                foreach (var word in words)
                {
                    cancellation.ThrowIfCancellationRequested();
                    foreach (var match in page.SearchText(word) ?? [])
                    {
                        var start = match.TextStartIndex; var end = start + word.Length;
                        if (start < 0 || end > pageText.Length || start > 0 && !char.IsWhiteSpace(pageText[start - 1])
                            || end < pageText.Length && !char.IsWhiteSpace(pageText[end])) continue;
                        if (match.Bounds is { Count: > 0 } bounds) fragments.Add((word, bounds[0].Left, bounds[0].Bottom));
                    }
                }
                var lines = new List<List<(string Text, float X, float Y)>>();
                foreach (var fragment in fragments.OrderBy(f => f.Y))
                {
                    var line = lines.FirstOrDefault(l => Math.Abs(l[0].Y - fragment.Y) < 4);
                    if (line == null) lines.Add(line = []);
                    line.Add(fragment);
                }
                foreach (var line in lines)
                {
                    var row = new StringBuilder();
                    foreach (var part in line.OrderBy(c => c.X))
                    {
                        row.Append(' ', Math.Max(1, (int)(part.X / 2) - row.Length)); row.Append(part.Text);
                    }
                    layout.AppendLine(row.ToString());
                }
                layout.Append('\f');
            }
            cancellation.ThrowIfCancellationRequested();
            var text = MfmImporter.ReadColumns(layout.ToString());
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("No MFM points text found. Image-only PDFs need text extraction before import.");
            return text;
        }, cancellation);
}
