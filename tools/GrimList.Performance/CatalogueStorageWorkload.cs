using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform;

public static class CatalogueStorageWorkload
{
    public static void Run(string directory, ISqliteConnectionFactory connections)
    {
        Directory.CreateDirectory(directory);
        var data = BootstrapRelease.ReadBundled().Dataset;
        List<object> results = [];
        foreach (var shared in new[] { false, true })
        {
            var mode = shared ? "shared-objects" : "legacy-full-snapshots";
            var path = Path.Combine(directory, mode + ".db");
            if (File.Exists(path)) throw new InvalidOperationException("Use a fresh disposable benchmark directory.");
            using (var sql = connections.Open(path))
            {
            sql.Run("CREATE TABLE dataset_snapshots(source_id TEXT PRIMARY KEY,content TEXT)");
            sql.Run("CREATE TABLE dataset_chunks(source_id TEXT,position INTEGER,content TEXT,PRIMARY KEY(source_id,position))");
            CatalogueStorage.Create(sql);
            for (var revision = 0; revision < 3; revision++)
            {
                var source = data.Source with { Version = data.Source.Version + "/" + revision };
                var next = data with { Source = source, Units = data.Units.Select((u, i) => u with {
                    ProfileSource = u.ProfileSource == null ? null : u.ProfileSource with { Version = u.ProfileSource.Version + "/" + revision },
                    Options = revision == 2 && i == 0 ? u.Options.Select(p => p with { Points = p.Points + 1 }).ToList() : u.Options
                }).ToList() };
                var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                sql.Transaction(() => { if (shared) CatalogueStorage.Write(sql, revision.ToString(), next); else DatasetStorage.Write(sql, revision.ToString(), next); });
                var writeMs = watch.Elapsed.TotalMilliseconds; var writeAllocations = GC.GetTotalAllocatedBytes(true) - allocated;
                watch.Restart(); var read = DatasetStorage.Read(sql, revision.ToString())!; var readMs = watch.Elapsed.TotalMilliseconds;
                if (DataJson.Write(read) != DataJson.Write(next)) throw new InvalidDataException("Catalogue round trip differs: " + mode);
                var payload = long.Parse(sql.Run(shared ? "SELECT sum(length(content)) FROM catalogue_chunks" : "SELECT sum(length(content)) FROM dataset_chunks").Single()[0]!);
                results.Add(new { Mode = mode, Revision = revision, Units = data.Units.Count, WriteMs = writeMs, ReadMs = readMs, WriteAllocatedBytes = writeAllocations, PayloadBytes = payload,
                    DatabaseBytes = new FileInfo(path).Length, Objects = sql.Run("SELECT count(*) FROM catalogue_objects").Single()[0] });
                Console.WriteLine($"{mode} revision {revision}: write {writeMs:F0} ms, read {readMs:F0} ms, payload {payload} bytes");
            }
            }
            File.Delete(path); // Only this workload's closed, synthetic database.
        }
        File.WriteAllText(Path.Combine(directory, "catalogue-storage.json"), DataJson.Write(new {
            Checks = new[] { "Legacy and shared formats preserve every bundled field across three revisions", "Provenance updates and one price change round trip" }, Measurements = results }));
    }
}
