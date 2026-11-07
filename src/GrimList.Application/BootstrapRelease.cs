using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GrimList.Core;
using GrimList.Persistence;

namespace GrimList.Application;

public sealed record BootstrapManifest(string ReleaseId, int FormatVersion, int DatasetSchemaVersion,
    int DatabaseSchemaVersion, string Sha256, int ContentBytes, int Units, int Factions, int Detachments,
    int ReferenceCards, string InputSha256);

/// <summary>A neutral library only: no database, armies, settings or source archives.</summary>
public sealed record BootstrapRelease(BootstrapManifest Manifest, Dataset Dataset)
{
    public static BootstrapRelease ReadBundled()
    {
        Stream Resource(string name) => typeof(BootstrapRelease).Assembly.GetManifestResourceStream(
            "GrimList.Application.Resources.bootstrap." + name)
            ?? throw new InvalidDataException("The bundled GrimList library is missing. Reinstall the application.");
        using var manifest = Resource("manifest.json");
        using var payload = Resource("library.json.gz");
        return Read(manifest, payload);
    }

    public static BootstrapRelease Read(Stream manifestStream, Stream compressedDataset)
    {
        var manifest = JsonSerializer.Deserialize<BootstrapManifest>(manifestStream, DataJson.Options)
            ?? throw new InvalidDataException("The bootstrap manifest is empty.");
        if (manifest.FormatVersion != 1 || manifest.DatabaseSchemaVersion is < 1 or > ArmyStore.SchemaVersion
            || manifest.DatasetSchemaVersion is < 1 or > 4 || string.IsNullOrWhiteSpace(manifest.ReleaseId))
            throw new InvalidDataException("This bundled library is incompatible with this version of GrimList.");
        if (manifest.ContentBytes is <= 0 or > LibraryWorkflow.MaximumDocumentBytes)
            throw new InvalidDataException("Invalid bundled library size.");
        using var gzip = new GZipStream(compressedDataset, CompressionMode.Decompress, leaveOpen: true);
        var bytes = new byte[manifest.ContentBytes];
        gzip.ReadExactly(bytes);
        if (gzip.ReadByte() != -1 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The bundled library failed its integrity check. Reinstall the application.");
        // Check the envelope before typed deserialization can silently ignore private data.
        using (var json = JsonDocument.Parse(bytes))
        {
            string[] fields = ["Source", "Units", "Game", "Detachments", "ReferenceCards", "ReferenceOnly", "SchemaVersion", "ReferenceCoverage", "FactionContexts", "RulesPacks"];
            if (json.RootElement.EnumerateObject().Any(p => !fields.Contains(p.Name)))
                throw new InvalidDataException("The bootstrap must contain only library data.");
        }
        var dataset = JsonSerializer.Deserialize<Dataset>(bytes, DataJson.Options)
            ?? throw new InvalidDataException("The bundled library is empty.");
        var release = new BootstrapRelease(manifest, dataset);
        release.Validate();
        return release;
    }

    public void Validate()
    {
        Dataset.Validate();
        if (Manifest.FormatVersion != 1 || Manifest.DatabaseSchemaVersion is < 1 or > ArmyStore.SchemaVersion
            || Manifest.DatasetSchemaVersion != Dataset.SchemaVersion || string.IsNullOrWhiteSpace(Manifest.ReleaseId)
            || Manifest.Units != Dataset.Units.Count || Manifest.Factions != Dataset.Units.Select(u => u.Faction).Distinct().Count()
            || Manifest.Detachments != Dataset.Detachments?.Count || Manifest.ReferenceCards != Dataset.ReferenceCards?.Count
            || Dataset.ReferenceOnly || Dataset.Units.Count == 0 || Dataset.Detachments.Count == 0 || Dataset.ReferenceCards.Count == 0)
            throw new InvalidDataException("The bundled library does not match its release manifest.");
    }
}
