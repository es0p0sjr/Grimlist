using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

internal static class ImportProjection
{
    // Check opening recovery against a private snapshot. Cache preparation must
    // never alter the reviewed import or silently replace a separate source repair.
    internal static Dataset RecoverPersisted(Dataset data, string directory)
    {
        using var proposed = new ProposedRepository(directory, data);
        var loaded = ReferenceLibraryRecovery.RestoreLibrary(proposed, proposed.LoadLibrary()).Library;
        loaded = EnhancementLibraryRepair.RestoreLibrary(proposed, loaded);
        return loaded.Dataset!;
    }
    internal static Dataset View(LoadedLibrary loaded, string directory)
    {
        var data = EffectiveDatasheetRecovery.ApplyPersisted(loaded, directory);
        return EquipmentPriceRecovery.ApplyBundled(DetachmentMetadataRecovery.ApplyBundled(data));
    }
    internal static IDisposable? Prepare(IArmyRepository repository, LoadedLibrary loaded, string variant)
    {
        if (loaded.Dataset == null) return null;
        var recovered = RecoverPersisted(loaded.Dataset, repository.DirectoryPath);
        // A points-only or externally constructed preview must not persist unrelated
        // recovery or certify a cache that bypasses required authoritative repair.
        // That legacy case retains the normal recovery-on-open path.
        if (!ReferenceEquals(recovered, loaded.Dataset) && DataJson.Write(recovered) != DataJson.Write(loaded.Dataset)) return null;
        return FactionLibrary.Prepare(repository, loaded with { Dataset = View(loaded, repository.DirectoryPath) }, variant);
    }
    private sealed class ProposedRepository(string directory, Dataset data) : IArmyRepository
    {
        public string DirectoryPath => directory;
        public Dataset LoadDataset() => data;
        public LoadedLibrary LoadLibrary() => new(data);
        public void ApplyImport(Dataset next, string? expectedSnapshot = null)
        {
            if (expectedSnapshot != null && expectedSnapshot != DataJson.Write(data)) throw new InvalidDataException("Proposed import changed during recovery.");
            data = ReferenceData.Normalize(LeaderRules.ImportRules(next));
        }
        public List<ArmyList> LoadArmies() => [];
        public ImportPreview PreviewImport(Dataset next, ImportLayer layer, bool completeFactions = false) => DatasetMerge.Prepare(data, next, layer, completeFactions);
        public void Save(ArmyList army, EditorContext? context = null) => throw new NotSupportedException();
        public void Delete(string id) => throw new NotSupportedException();
        public EditorContext LoadContext() => new();
        public void SaveContext(EditorContext context) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
