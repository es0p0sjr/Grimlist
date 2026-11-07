using System.IO.Compression;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;

internal static class BugReportChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Unsafe report accepted"); }
        var service = new BugReportService(); var draft = new BugReportDraft("Missing price", "Open the detachment picker", "A cost", "Unknown");
        var data = new Dataset(new("BSData", "source-release", "", "https://private.example/secret", ContentHash: "archive-hash"),
            [new("Example", "Canonical unit", [new(1, 50)], Id: "source-unit")]);
        var army = ArmyList.New("Example") with { Name = "Private campaign name", Entries = [new("private-guid", "source-unit", "My secret hero", 1, Notes: "Private notes")] };
        var diagnostic = BugDiagnostics.Capture(data, army, "0.2.1", "Test platform", "Test OS");
        test("Bug report without images exports reviewed JSON and diagnostic metadata", () =>
        {
            var prepared = service.Prepare(draft, diagnostic, []); var package = service.Export(prepared);
            using var zip = new ZipArchive(new MemoryStream(package.Content));
            Check(zip.Entries.Count == 1 && zip.Entries[0].FullName == "bug-report.json", "Wrong package files");
            using var reader = new StreamReader(zip.Entries[0].Open()); Check(reader.ReadToEnd() == prepared.Json, "Preview differs from package");
            var report = DataJson.Read<BugReportDocument>(prepared.Json);
            Check(report.Diagnostics.AppVersion == "0.2.1" && report.Diagnostics.DataVersion == "source-release" && report.Diagnostics.Platform == "Test platform", "Missing metadata");
            Check(report.Diagnostics.Units.Single().Unit == "Canonical unit", "Missing anonymized technical state");
            foreach (var secret in new[] { "private-guid", "Private campaign name", "My secret hero", "Private notes", "private.example" }) Check(!prepared.Json.Contains(secret), "Leaked " + secret);
            File.WriteAllBytes(Path.Combine(root, "bug-report-without-images.zip"), package.Content);
        });
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6rj8AAAAASUVORK5CYII=");
        test("Bug report selected images have safe names and immutable reviewed bytes", () =>
        {
            var input = png.ToArray(); var prepared = service.Prepare(draft, diagnostic, [input, png]); input[0] = 0;
            prepared.ImageCopies()[0][0] = 0;
            using var zip = new ZipArchive(new MemoryStream(service.Export(prepared).Content));
            Check(zip.Entries.Count == 3 && zip.GetEntry("images/image-1.png") != null && zip.GetEntry("images/image-2.png") != null, "Wrong images");
            using var stream = zip.GetEntry("images/image-1.png")!.Open(); Check(stream.ReadByte() == 137, "Caller mutated prepared report");
            var report = DataJson.Read<BugReportDocument>(prepared.Json);
            Check(report.Images.All(i => i.Bytes == png.Length && i.Sha256.Length == 64), "Missing image preview inventory");
            File.WriteAllBytes(Path.Combine(root, "bug-report-with-images.zip"), service.Export(prepared).Content);
        });
        test("Bug report privacy redacts paths usernames addresses and credentials", () =>
        {
            var text = "user " + Environment.UserName + "\nC:\\Users\\Alice Smith\\secrets.txt\n/home/alice/token.txt\n\\\\server\\share\\private.db\nfile:///sdcard/private.jpg\n" +
                "https://host/path?token=sensitive\nAuthorization: Bearer secret123\napi_key=key123\npassword: hunter2\nalice@example.com";
            var safe = BugReportPrivacy.Redact(text);
            foreach (var secret in new[] { Environment.UserName, "Alice", "alice", "secrets.txt", "private.db", "sensitive", "secret123", "key123", "hunter2", "@example" }) Check(!safe.Contains(secret), "Redaction failed: " + secret);
            ApplicationErrors.Record(new IOException("Cannot open C:\\Users\\Alice\\private.db"));
            Check(!string.Join("", ApplicationErrors.Recent()).Contains("Alice"), "Error ring leaked path");
        });
        test("Bug report images reject oversized or invalid content and strip metadata", () =>
        {
            Reject(() => service.Prepare(draft, diagnostic, [new byte[40]]));
            Reject(() => service.Prepare(draft, diagnostic, Enumerable.Repeat(png, 6).ToList()));
            Reject(() => BugReportImages.ReadSelectedAsync(new MemoryStream(new byte[BugReportService.MaximumImageBytes + 1])).GetAwaiter().GetResult());
            var chunk = new byte[] { 0, 0, 0, 6, 116, 69, 88, 116, 115, 101, 99, 114, 101, 116, 0, 0, 0, 0 };
            var withMetadata = png[..33].Concat(chunk).Concat(png[33..]).ToArray();
            Check(BugReportImages.StripMetadata(withMetadata).SequenceEqual(png), "Image metadata retained");
            Reject(() => service.Prepare(new("", ""), diagnostic, []));
            Check(service.Export(service.Prepare(draft, diagnostic, [])).Content.Length > 0, "Images became required");
        });
        test("Bug report platform share adapter sends only the reviewed package", () =>
        {
            var sharing = new RecordingSharing(); IBugReportDestination destination = new ShareBugReport(sharing);
            var prepared = service.Prepare(draft, diagnostic, [png]); Check(sharing.Package == null, "Preparing submitted a report");
            var package = service.Export(prepared); destination.SendAsync(package).GetAwaiter().GetResult();
            Check(sharing.Package?.Name == package.Name && sharing.Package.ContentType == "application/zip" && sharing.Package.Content.SequenceEqual(package.Content), "Share contents changed");
            var token = new CancellationToken(true);
            try { destination.SendAsync(package, token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        });
    }
    private sealed class RecordingSharing : IFileSharing
    {
        public BugReportPackage? Package;
        public Task ShareAsync(string name, string type, byte[] bytes, CancellationToken cancellation = default)
        { cancellation.ThrowIfCancellationRequested(); Package = new(name, type, bytes); return Task.CompletedTask; }
    }
}
