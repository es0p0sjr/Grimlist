using System.IO.Compression;
using System.Security.Cryptography;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform.Windows;

internal static class ImportEvaluationOptimizationChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static UnitDefinition Unit(string name, params DatasheetModifier[] modifiers) => new("Dependency test", name, [new(1, 50)],
        Profile: new(Keywords: [name], Characteristics: new() { ["OC"] = "1" }, Semantics: new([], modifiers.ToList(), 3)));
    private static ArmyEntry Entry(UnitDefinition unit, string? id = null) => new(id ?? unit.Name, unit.Key, unit.Name, 1);
    public static void Run(Action<string, Action> test, string root)
    {
        IncrementalEvaluationWorkload.Run(test);
        foreach (var damage in new[] { "bytes", "digest", "missing", "extra", "position", "oversized", "valid-gzip-wrong-json", "trailing-json" })
            test("Reused catalogue verification rejects corruption and rolls back: " + damage, () =>
            {
                using var sql = new WindowsSqliteConnection(Path.Combine(root, "reuse-" + damage + ".db"));
                sql.Run("CREATE TABLE dataset_snapshots(source_id TEXT PRIMARY KEY,content TEXT)"); CatalogueStorage.Create(sql);
                var unit = Unit("Stored"); var data = new Dataset(new("Synthetic", "1", "", ""), [unit]);
                sql.Transaction(() => CatalogueStorage.Write(sql, "original", data));
                var hash = sql.Run("SELECT hash FROM catalogue_objects WHERE hash != substr((SELECT content FROM dataset_snapshots),7)")[0][0]!;
                if (damage == "bytes") sql.Run("UPDATE catalogue_objects SET bytes=bytes+1 WHERE hash=?", hash);
                if (damage == "digest") sql.Run("UPDATE catalogue_objects SET digest=? WHERE hash=?", new string('0', 64), hash);
                if (damage == "missing") sql.Run("DELETE FROM catalogue_chunks WHERE object_hash=?", hash);
                if (damage == "extra") sql.Run("INSERT INTO catalogue_chunks VALUES(?,99,x'00')", hash);
                if (damage == "position") sql.Run("UPDATE catalogue_chunks SET position=99 WHERE object_hash=?", hash);
                if (damage == "oversized") sql.Run("UPDATE catalogue_chunks SET content=zeroblob(49153) WHERE object_hash=?", hash);
                if (damage is "valid-gzip-wrong-json" or "trailing-json")
                {
                    var bytes = sql.ReadBlob("SELECT content AS value FROM catalogue_chunks WHERE object_hash=?", hash)!;
                    using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var output = new MemoryStream(); input.CopyTo(output); var json = output.ToArray();
                    if (damage == "valid-gzip-wrong-json") json[0] = (byte)' ';
                    else json = [.. json, (byte)' '];
                    using var packed = new MemoryStream(); using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, true)) gzip.Write(json);
                    var changed = packed.ToArray();
                    sql.Run("UPDATE catalogue_chunks SET content=? WHERE object_hash=?", changed, hash);
                    sql.Run("UPDATE catalogue_objects SET compressed_bytes=?,digest=? WHERE hash=?", changed.Length, Convert.ToHexString(SHA256.HashData(changed)), hash);
                }
                try { sql.Transaction(() => CatalogueStorage.Write(sql, "reused", data)); throw new Exception("Corruption accepted: " + damage); }
                catch (InvalidDataException) { }
                catch (EndOfStreamException) { }
                Check(sql.Run("SELECT count(*) FROM dataset_snapshots").Single()[0] == "1", "Failed verification published a revision.");
            });
    }
}
