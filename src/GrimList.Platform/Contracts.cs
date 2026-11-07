using GrimList.Core;

namespace GrimList.Platform;

public interface ISqliteConnection : IDisposable
{
    List<string?[]> Run(string sql, params object?[] arguments);
    // Bounded values only. The default supports transparent adapter decorators.
    byte[]? ReadBlob(string sql, params object?[] arguments)
    {
        var hex = Run("SELECT hex(value) FROM (" + sql + ")", arguments).SingleOrDefault()?[0];
        return hex == null ? null : Convert.FromHexString(hex);
    }
    void RunBatch(string sql, IEnumerable<object?[]> rows)
    { foreach (var row in rows) Run(sql, row); }
    void Transaction(Action action);
}

public interface ISqliteConnectionFactory
{
    ISqliteConnection Open(string path);
}

public record EditorContext(string ArmyId = "", string? EntryId = null, int? ModelIndex = null,
    string? LoadoutSection = null, string Screen = "roster");

// Revision identifies the exact persisted snapshot loaded with Dataset. Repositories
// without transactional revision tracking use null and retain uncached recovery.
public sealed record LoadedLibrary(Dataset? Dataset, string? Revision = null);

public interface IArmyRepository : IDisposable
{
    string DirectoryPath { get; }
    Dataset? LoadDataset();
    LoadedLibrary LoadLibrary() => new(LoadDataset());
    string? LibraryRevision() => null;
    bool HasLibraryCheck(string name, string version, string? revision) => false;
    void RecordLibraryCheck(string name, string version, string? revision) { }
    List<ArmyList> LoadArmies();
    void Save(ArmyList army, EditorContext? context = null);
    void Delete(string id);
    ImportPreview PreviewImport(Dataset data, ImportLayer layer, bool completeFactions = false);
    void ApplyImport(Dataset data, string? expectedSnapshot = null);
    // Implementations returning a non-null LoadedLibrary.Revision must check it
    // inside the same transaction that publishes the replacement library.
    void ApplyImportAtRevision(Dataset data, string expectedRevision) => throw new NotSupportedException("Transactional library revisions are not supported.");
    // Callback runs inside the transaction after normalization and before commit.
    // A rejected projection rolls back the entire import. Unsupported adapters
    // retain their existing behavior and do not certify a prepared cache.
    void ApplyPreparedImport(Dataset data, string? expectedSnapshot, string? expectedRevision, Action<LoadedLibrary> prepare)
    {
        if (expectedRevision != null) ApplyImportAtRevision(data, expectedRevision);
        else ApplyImport(data, expectedSnapshot);
    }
    EditorContext LoadContext();
    void SaveContext(EditorContext context);
}

public interface IPlatformPaths
{
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    string ReleasesDirectory => Path.Combine(DataDirectory, "releases");
    // Settings are stored transactionally in the library database.
    string DatabasePath => Path.Combine(DataDirectory, "grimlist.db");
}

// A selected document is a stream, not necessarily a filesystem path (Android content URI).
public sealed record PickedDocument(string Name, Stream Content) : IDisposable
{
    public void Dispose() => Content.Dispose();
}
public interface IFilePicker
{
    Task<PickedDocument?> PickAsync(CancellationToken cancellation = default);
}
public interface IFileSharing
{
    Task ShareAsync(string name, string contentType, byte[] content, CancellationToken cancellation = default);
}
public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellation = default);
}
public interface IPdfTextExtractor
{
    Task<string> ExtractAsync(string path, string temporaryDirectory, CancellationToken cancellation = default);
}
public interface IRosterPdfExporter
{
    byte[] Create(ArmyList army, Dataset? dataset, bool includeRules, bool allWeapons);
}
