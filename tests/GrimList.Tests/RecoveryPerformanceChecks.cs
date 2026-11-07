using System.IO.Compression;
using System.Text.Json;
using GrimList.Application;
using GrimList.Core;

internal static class RecoveryPerformanceChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        test("Indexed recovery equals canonical aggregate; cache hits, corruption and source edits preserve semantics", () =>
        {
            var data = DataJson.Read<Dataset>(File.ReadAllText("tests/fixtures/libraries/GrimList-Library.json"));
            using var file = File.OpenRead("src/GrimList.Application/Resources/effective-datasheets.json.gz");
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var packs = JsonSerializer.Deserialize<List<EffectiveDatasheetRecovery.Pack>>(gzip, DataJson.Options)!;
            Dataset Canonical(Dataset input) => EffectiveDatasheetRecovery.Apply(WeaponMetadataRecovery.ApplyBundled(EffectiveDatasheetRecovery.Apply(input, packs)), packs);
            var expected = DataJson.Write(Canonical(data));
            var before = DataJson.Write(data);
            var cache = Path.Combine(root, "recovery-cache");
            void Equal(Dataset value) { if (DataJson.Write(value) != expected) throw new Exception("Indexed/cached recovery differs from canonical rules"); }
            Equal(EffectiveDatasheetRecovery.ApplyBundled(data));
            Equal(EffectiveDatasheetRecovery.ApplyBundled(data, cache));
            Equal(EffectiveDatasheetRecovery.ApplyBundled(data, cache));
            var stored = Directory.GetFiles(Path.Combine(cache, "derived-cache")).Single();
            File.WriteAllText(stored, "corrupt cache");
            Equal(EffectiveDatasheetRecovery.ApplyBundled(data, cache));
            var modified = data with { Units = data.Units.Select(u => u.Profile == null ? u : u with {
                Profile = u.Profile with { Abilities = [new("User edit", "Never replace this ability.")] } }).ToList() };
            var actual = EffectiveDatasheetRecovery.ApplyBundled(modified, cache);
            if (DataJson.Write(actual) != DataJson.Write(Canonical(modified))) throw new Exception("Stale cache accepted edited library");
            if (DataJson.Write(data) != before) throw new Exception("Recovery mutated original provenance/data");
        });
    }
}
