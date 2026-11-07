namespace GrimList.Application;

public sealed partial class FactionLibrary
{
    private FileStream? lease;
    ~FactionLibrary() { lease?.Dispose(); }
    private static FileStream? TryCacheLock(string directory)
    { try { return CacheLock(directory); } catch (Exception error) when (CacheError(error)) { return null; } }

    // Serialize manifest publication, acquisition of a reader lease and collection
    // across processes. No source, repository or non-faction cache is collected.
    private static FileStream CacheLock(string directory)
    {
        var root = Path.Combine(directory, "derived-cache", "factions");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "maintenance.lock");
        var until = Environment.TickCount64 + 30_000;
        while (true)
        {
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < until) { Thread.Sleep(25); }
        }
    }
    private bool Lease()
    {
        if (fallback != null) return true;
        try { lease = new(ManifestPath(Revision) + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite); return true; }
        catch (Exception error) when (CacheError(error)) { return false; }
    }
    private void Retain()
    {
        try
        {
            var stamp = Path.Combine(Root, "retention-stamp.txt");
            var marked = Directory.EnumerateFiles(Root, "*.lease").Select(p => p[..^6]).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
            bool Unleased(string path)
            {
                try { using var probe = new FileStream(path + ".lease", FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
                catch (IOException) { return false; }
            }
            // Warm startup must not deserialize several megabytes of old manifests
            // just to discover that nothing is collectible. Probe only excess leases;
            // do a complete sweep after a revision change or once per day for orphans.
            if (File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) > DateTime.UtcNow.AddDays(-1)
                && File.ReadAllText(stamp) == CacheIdentity
                && !marked.Skip(2).Where(p => p != ManifestPath(Revision)).Any(Unleased)) return;
            var manifests = new List<(string Path, Manifest Value, DateTime Time)>();
            foreach (var path in Directory.EnumerateFiles(Root, "*.json.gz"))
            {
                // Only manifests have a lease marker. Content files are addressed
                // by hash and cannot be mistaken for a manifest or original source.
                if (!File.Exists(path + ".lease")) continue;
                try { manifests.Add((path, path == ManifestPath(Revision) ? manifest : Read<Manifest>(path), File.GetLastWriteTimeUtc(path))); }
                catch (Exception e) when (CacheError(e)) { return; } // Cannot prove its references safe to collect.
            }
            var retained = manifests.OrderByDescending(m => m.Time).Take(2).Select(m => m.Path).ToHashSet();
            retained.Add(ManifestPath(Revision));
            foreach (var item in manifests)
            {
                try { using var probe = new FileStream(item.Path + ".lease", FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { retained.Add(item.Path); }
            }
            var live = manifests.Where(m => retained.Contains(m.Path)).SelectMany(m => m.Value.Parts.Values.Select(p => p.File)
                .Append(m.Value.AbilitiesFile)).ToHashSet();
            foreach (var item in manifests.Where(m => !retained.Contains(m.Path)))
            { File.Delete(item.Path); File.Delete(item.Path + ".lease"); }
            foreach (var path in Directory.EnumerateFiles(Root, "*.json.gz"))
            {
                var name = Path.GetFileName(path)[..^8];
                if (!ValidFile(name) || retained.Contains(path) || live.Contains(name)) continue;
                // Prepared-but-not-yet-active imports cannot overlap collection:
                // both hold maintenance.lock. Allow one day for crash leftovers.
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.Delete(path);
            }
            File.WriteAllText(stamp, CacheIdentity);
        }
        catch (Exception e) when (CacheError(e)) { }
    }
}
