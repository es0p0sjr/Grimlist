using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

public record BugReportDraft(string Title, string Description, string Expected = "", string Actual = "");
public record BugReportDiagnostics(string AppVersion, string Platform, string OsVersion, string DataVersion, string DataHash,
    int? DataSchema, string Faction, List<string> Detachments, List<string> Validation, List<string> RecentErrors, List<BugUnitState> Units);
public record BugUnitState(string Unit, int Models, string Enhancement, List<RosterChoice> Choices, UnitLoadout? Equipment = null, bool Warlord = false);
public record BugImageInfo(string File, int Bytes, string Sha256);
public record BugReportDocument(int FormatVersion, string CreatedUtc, BugReportDraft Report, BugReportDiagnostics Diagnostics, List<BugImageInfo> Images);

// A prepared report owns its bytes. Export/share always uses the exact reviewed content.
public sealed class PreparedBugReport
{
    public string Json { get; }
    private readonly List<byte[]> images;
    internal PreparedBugReport(string json, IEnumerable<byte[]> images) { Json = json; this.images = images.Select(b => b.ToArray()).ToList(); }
    public IReadOnlyList<byte[]> ImageCopies() => images.Select(b => b.ToArray()).ToList();
}
public record BugReportPackage(string Name, string ContentType, byte[] Content);
public interface IBugReportService
{
    PreparedBugReport Prepare(BugReportDraft draft, BugReportDiagnostics diagnostics, IReadOnlyList<byte[]> images);
    BugReportPackage Export(PreparedBugReport report);
}
public interface IBugReportDestination { Task SendAsync(BugReportPackage package, CancellationToken cancellation = default); }
public sealed class ShareBugReport(IFileSharing sharing) : IBugReportDestination
{
    public Task SendAsync(BugReportPackage package, CancellationToken cancellation = default)
        => sharing.ShareAsync(package.Name, package.ContentType, package.Content, cancellation);
}

public sealed class BugReportService : IBugReportService
{
    public const int MaximumImages = 5, MaximumImageBytes = 10 * 1024 * 1024, MaximumTotalBytes = 25 * 1024 * 1024;
    public PreparedBugReport Prepare(BugReportDraft draft, BugReportDiagnostics diagnostics, IReadOnlyList<byte[]> images)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrWhiteSpace(draft.Description)) throw new InvalidDataException("Enter a short title and steps or description.");
        if (draft.Title.Length > 160 || new[] { draft.Description, draft.Expected, draft.Actual }.Any(t => t.Length > 12000))
            throw new InvalidDataException("Use a title of up to 160 characters and up to 12,000 characters per description field.");
        if (images.Count > MaximumImages || images.Sum(b => (long)b.Length) > MaximumTotalBytes) throw new InvalidDataException("Select up to 5 images, totalling at most 25 MB.");
        var safeImages = images.Select(BugReportImages.StripMetadata).ToList();
        var manifest = safeImages.Select((b, i) => new BugImageInfo($"images/image-{i + 1}.png", b.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)))).ToList();
        var document = new BugReportDocument(1, DateTimeOffset.UtcNow.ToString("O"), draft, diagnostics, manifest);
        return new(DataJson.Write(document), safeImages);
    }
    public BugReportPackage Export(PreparedBugReport report)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
            Add("bug-report.json", Encoding.UTF8.GetBytes(report.Json));
            foreach (var (bytes, index) in report.ImageCopies().Select((b, i) => (b, i))) Add($"images/image-{index + 1}.png", bytes);
        }
        return new("grimlist-bug-report-" + Guid.NewGuid().ToString("N")[..12] + ".zip", "application/zip", buffer.ToArray());
    }
}

public static class BugDiagnostics
{
    public static BugReportDiagnostics Capture(Dataset? data, ArmyList? army, string appVersion, string platform, string osVersion)
    {
        var privateNames = army == null ? [] : new[] { army.Name }.Concat(army.Entries.Select(e => e.Name)
            .Where(n => !(data?.Units.Any(u => u.Name == n) ?? false))).Where(n => n.Length > 0).ToArray();
        string Safe(string text)
        {
            foreach (var name in privateNames.OrderByDescending(n => n.Length)) text = text.Replace(name, "[roster name]", StringComparison.OrdinalIgnoreCase);
            return BugReportPrivacy.Redact(text);
        }
        // Explicit allowlist: no roster IDs, names, notes, paths, URLs or unrelated armies.
        EquipmentSelection Equipment(EquipmentSelection e) => new(Safe(e.GroupId), Safe(e.OptionId), e.Count);
        var units = army?.Entries.Select(e => new BugUnitState(Safe(data?.Units.FirstOrDefault(u => u.Key == e.UnitKey)?.Name ?? "Unavailable unit"),
            e.Models, Safe(e.Enhancement?.Name ?? ""), (e.Choices ?? []).Select(c => new RosterChoice(Safe(c.GroupId), Safe(c.OptionId))).ToList(),
            e.Loadout == null ? null : new(e.Loadout.Models.Select(m => new ModelLoadout(Safe(m.RoleId), m.Equipment.Select(Equipment).ToList())).ToList(),
                e.Loadout.Equipment.Select(Equipment).ToList(), e.Loadout.Manual), e.Id == army.WarlordEntryId)).ToList() ?? [];
        return new(Safe(appVersion), Safe(platform), Safe(osVersion), Safe(data?.Source.Version ?? "No library"), Safe(data?.Source.ContentHash ?? ""),
            data?.SchemaVersion, Safe(army?.Faction ?? ""), (army == null ? [] : DetachmentRules.Selected(army)).Select(Safe).ToList(),
            (army == null ? [] : ArmyCalculator.Calculate(army, data).Issues).Take(100).Select(Safe).ToList(),
            ApplicationErrors.Recent().Select(Safe).ToList(), units);
    }
}

public static class BugReportPrivacy
{
    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)\b(?:https?|file)://[^\s<>""']+", "[address]");
        text = Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n""<>|]*|(?<!\w)/(?:[^\s/]+/)+[^\s]*", "[path]");
        text = Regex.Replace(text, @"(?i)\b(?:authorization|bearer|password|passwd|secret|token|api[_-]?key|access[_-]?key)\b\s*[=:]?\s*[^\r\n,;]+", "[redacted credential]");
        text = Regex.Replace(text, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", "[email]");
        if (Environment.UserName is { Length: > 0 } user) text = Regex.Replace(text, @"(?<!\w)" + Regex.Escape(user) + @"(?!\w)", "[user]", RegexOptions.IgnoreCase);
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}

public static class ApplicationErrors
{
    private static readonly Queue<string> errors = new();
    public static void Record(Exception error)
    {
        lock (errors)
        {
            // Exception messages can contain arbitrary imported text or another roster's
            // name. Keep technical frames and error codes, never messages or arguments.
            var frames = new System.Diagnostics.StackTrace(error, false).GetFrames().Take(6)
                .Select(f => f.GetMethod()).Where(m => m != null)
                .Select(m => m!.DeclaringType?.Name + "." + m.Name);
            errors.Enqueue(DateTimeOffset.UtcNow.ToString("O") + " " + error.GetType().Name + " (" + error.HResult + ") " + string.Join(" > ", frames));
            while (errors.Count > 10) errors.Dequeue();
        }
    }
    public static List<string> Recent() { lock (errors) return errors.ToList(); }
}
