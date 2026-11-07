using System.Diagnostics;
using GrimList.Core;

namespace GrimList.Platform.Windows;

public sealed class WindowsPdfTextExtractor : IPdfTextExtractor
{
    public static string? FindPdfTool()
    {
        var custom = Environment.GetEnvironmentVariable("GRIMLIST_PDFTOTEXT");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;
        var paths = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "mingw64", "bin", "pdftotext.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dnGrep", "Plugins", "PdfSearch", "pdftotext.exe")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => p.Trim().Trim('"')).Where(Path.IsPathFullyQualified).Select(p => Path.Combine(p, "pdftotext.exe")));
        return paths.FirstOrDefault(File.Exists);
    }
    public async Task<string> ExtractAsync(string path, string temporaryDirectory, CancellationToken cancellation = default)
    {
        var tool = FindPdfTool() ?? throw new InvalidOperationException("PDF import needs pdftotext (Xpdf or Poppler). Install it or set GRIMLIST_PDFTOTEXT to its full path. You can also import copied MFM text or JSON.");
        Directory.CreateDirectory(temporaryDirectory);
        var output = Path.Combine(temporaryDirectory, Guid.NewGuid() + ".txt");
        try
        {
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-layout", "-enc", "UTF-8", path, output }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Could not start PDF extraction.");
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                cancellation.ThrowIfCancellationRequested();
                throw new IOException("PDF extraction timed out. Try a smaller document.");
            }
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidDataException("PDF extraction failed: " + error);
            var layout = await File.ReadAllTextAsync(output);
            if (string.IsNullOrWhiteSpace(layout)) throw new InvalidDataException("No text was found. Scanned PDFs need OCR before import.");
            return MfmImporter.ReadColumns(layout);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
