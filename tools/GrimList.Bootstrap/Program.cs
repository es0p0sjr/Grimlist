using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;

try
{
if (args is ["--audit-equipment", var equipmentLibrary])
{
    using var inputLibrary = File.OpenRead(equipmentLibrary);
    using var decompressedLibrary = new GZipStream(inputLibrary, CompressionMode.Decompress);
    var before = JsonSerializer.Deserialize<Dataset>(decompressedLibrary, DataJson.Options) ?? throw new InvalidDataException("Empty library.");
    var after = EquipmentPriceRecovery.ApplyBundled(before);
    var affected = before.Units.Where(WargearPricing.MissingPrices).ToList();
    var remaining = after.Units.SelectMany(WargearPricing.CoverageIssues).ToList();
    var rows = affected.Select(u =>
    {
        var repaired = after.Units.Single(r => r.Key == u.Key);
        var options = WargearPricing.Options(repaired).ToList();
        return new { u.Faction, u.Name, Known = options.Count(o => WargearPricing.OptionCost(repaired, o.Id) != null),
            Unknown = options.Count(o => WargearPricing.OptionCost(repaired, o.Id) == null) };
    }).ToList();
    Console.WriteLine(DataJson.Write(new { Units = before.Units.Count, Affected = affected.Count,
        Restored = rows.Count(r => r.Unknown == 0), CoverageErrors = remaining.Count, Rows = rows, Errors = remaining }));
    if (remaining.Count > 0) Environment.ExitCode = 1;
    return;
}
if (args is ["--build-equipment-evidence", var equipmentArchive, var equipmentOutput])
{
    var equipmentBytes = File.ReadAllBytes(equipmentArchive);
    var hash = Convert.ToHexString(SHA256.HashData(equipmentBytes));
    var sourceInfo = new SourceInfo("BSData equipment recovery", "pinned-archive", "", "", Edition: "11", ContentHash: hash);
    var imported = BsDataImporter.ImportAll(BsDataDocument.ReadArchive(equipmentBytes), sourceInfo).Dataset;
    List<EquipmentPriceRecovery.Pack> packs = [];
    if (File.Exists(equipmentOutput))
    {
        using var inputEvidence = File.OpenRead(equipmentOutput);
        using var gzip = new GZipStream(inputEvidence, CompressionMode.Decompress);
        packs = JsonSerializer.Deserialize<List<EquipmentPriceRecovery.Pack>>(gzip, DataJson.Options) ?? [];
    }
    packs.RemoveAll(p => p.Source.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase));
    var pack = EquipmentPriceRecovery.Create(imported); packs.Add(pack);
    using var outputEvidence = File.Create(equipmentOutput);
    using var compressed = new GZipStream(outputEvidence, CompressionLevel.SmallestSize);
    JsonSerializer.Serialize(compressed, packs, new JsonSerializerOptions(DataJson.Options) { WriteIndented = false });
    Console.WriteLine($"Equipment evidence: {pack.Units.Count} units, {pack.Units.Sum(u => u.Costs.Count(c => c.Points != null))} known manual prices, {pack.Units.Sum(u => u.Costs.Count(c => c.Points == null))} unresolved; {hash}.");
    return;
}
if (args is ["--build-army-rule-evidence", var archives, var evidencePath])
{
    List<ArmyRuleScope> scopes = [];
    if (File.Exists(evidencePath))
    {
        using var inputEvidence = File.OpenRead(evidencePath);
        using var gzip = new GZipStream(inputEvidence, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        scopes = DataJson.Read<List<ArmyRuleScope>>(reader.ReadToEnd());
    }
    foreach (var archive in Directory.EnumerateFiles(archives, "repository.zip", SearchOption.AllDirectories))
    {
        var metadata = Path.Combine(Path.GetDirectoryName(archive)!, "source.json");
        if (!File.Exists(metadata)) continue;
        var provenance = DataJson.Read<SourceInfo>(File.ReadAllText(metadata)) with { ImportedAt = "" };
        if (provenance.Edition != "11" || !provenance.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase)) continue;
        var extracted = ArmyRuleLibraryRecovery.ReadArchive(archive, provenance);
        scopes.RemoveAll(s => s.Source.Edition == provenance.Edition && s.Source.ContentHash.Equals(provenance.ContentHash, StringComparison.OrdinalIgnoreCase));
        scopes.AddRange(extracted);
        Console.WriteLine($"Army rules {provenance.Version}: {extracted.Count} scopes, {extracted.Sum(s => s.Cards.Count)} cards, {extracted.Sum(s => s.Unresolved.Count)} unresolved definitions.");
    }
    using var outputEvidence = File.Create(evidencePath);
    using var compressed = new GZipStream(outputEvidence, CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(compressed);
    writer.Write(DataJson.Write(scopes.OrderBy(s => s.Source.ContentHash).ThenBy(s => s.Faction).ToList()));
    return;
}
if (args is ["--validate-bundled"])
{
    ValidateEnhancements(BootstrapRelease.ReadBundled().Dataset);
    ValidateArmyRules(BootstrapRelease.ReadBundled().Dataset);
    var equipmentErrors = EquipmentPriceRecovery.ValidateBundled(BootstrapRelease.ReadBundled().Dataset);
    if (equipmentErrors.Count > 0) throw new InvalidDataException("Equipment candidate validation failed:\n" + string.Join("\n", equipmentErrors));
    Console.WriteLine("Equipment candidate validation passed: every selectable option has a price or an explicit unresolved reason.");
    return;
}
if (args.Length is not (3 or 4))
    throw new ArgumentException("Usage: GrimList.Bootstrap <validated-library.json|--regenerate-bundled> <output-directory> <release-id> [pinned-archive-directory], --validate-bundled, --build-equipment-evidence <11e-repository.zip> <evidence.json.gz>, or --audit-equipment <library.json.gz>");
if (args[0] != "--regenerate-bundled" && !Path.GetExtension(args[0]).Equals(".json", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Generate from a validated library JSON export, never a user database.");
var input = args[0] == "--regenerate-bundled" ? JsonSerializer.SerializeToUtf8Bytes(BootstrapRelease.ReadBundled().Dataset, DataJson.Options) : File.ReadAllBytes(args[0]);
var source = JsonSerializer.Deserialize<Dataset>(input, DataJson.Options) ?? throw new InvalidDataException("Empty library.");
source.Validate();
if (args.Length == 4)
{
    // Rebuild source-owned enhancement cards from pinned bytes, never fetch current
    // rules or infer source identity from old profile/display names.
    foreach (var archive in Directory.EnumerateFiles(args[3], "repository.zip", SearchOption.AllDirectories))
    {
        var raw = File.ReadAllBytes(archive); var hash = Convert.ToHexString(SHA256.HashData(raw));
        var existing = source.ReferenceCards!.Where(c => c.Section == "Enhancements"
            && c.Source?.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (existing.Count == 0) continue;
        var provenance = existing[0].Source!;
        var scopes = existing.Select(c => (c.Faction, c.Detachment)).ToHashSet();
        var rebuilt = BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(raw), provenance)
            .Where(c => scopes.Contains((c.Faction, c.Detachment))).ToList();
        source = source with { ReferenceCards = source.ReferenceCards!.Except(existing).Concat(rebuilt).ToList() };
        Console.WriteLine($"Rebuilt {rebuilt.Count} enhancement cards from pinned source {hash}.");
    }
}
// Include the same compatibility metadata already applied by production sessions,
// so a clean install never needs a developer source cache to repair enhancements.
source = EnhancementLibraryRepair.ApplyBundled(source);
source = ArmyRuleLibraryRecovery.ApplyBundled(source);
var evidence = new EnhancementEvidence(EnhancementLibraryRepair.BundledEvidence);
source = source with { ReferenceCards = source.ReferenceCards?.Select(card =>
{
    if (card.Section != "Enhancements" || (card.Details?.SourceCardId ?? "").Length > 0) return card;
    var match = evidence.Match(card).Card;
    return match == null || match.SourceEntryId.Length == 0 ? card : card with {
        Details = (card.Details ?? new ReferenceDetails()) with { SourceCardId = match.SourceEntryId,
            SourceId = "bsdata:" + card.Source!.Edition, Edition = card.Source.Edition, SourceType = "BSData",
            Scope = "Detachment", Detachments = [new(card.Detachment)] } };
}).ToList() };
ValidateEnhancements(source);
ValidateArmyRules(source);
var json = JsonSerializer.SerializeToNode(source, DataJson.Options)!;
void Neutralize(JsonNode? node)
{
    if (node is JsonObject obj)
    {
        if (obj.ContainsKey("ImportedAt")) obj["ImportedAt"] = "";
        foreach (var child in obj.ToArray()) Neutralize(child.Value);
    }
    else if (node is JsonArray array) foreach (var child in array) Neutralize(child);
}
Neutralize(json);
var bytes = JsonSerializer.SerializeToUtf8Bytes(json);
var neutral = JsonSerializer.Deserialize<Dataset>(bytes, DataJson.Options)!;
var manifest = new BootstrapManifest(args[2], 1, neutral.SchemaVersion, ArmyStore.SchemaVersion,
    Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, neutral.Units.Count,
    neutral.Units.Select(u => u.Faction).Distinct().Count(), neutral.Detachments!.Count,
    neutral.ReferenceCards!.Count, Convert.ToHexString(SHA256.HashData(input)));
new BootstrapRelease(manifest, neutral).Validate();
Directory.CreateDirectory(args[1]);
var manifestPath = Path.Combine(args[1], "manifest.json");
var payloadPath = Path.Combine(args[1], "library.json.gz");
File.WriteAllText(manifestPath, DataJson.Write(manifest));
using (var output = File.Create(payloadPath))
using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize)) gzip.Write(bytes);
using (var metadata = File.OpenRead(manifestPath))
using (var payload = File.OpenRead(payloadPath)) BootstrapRelease.Read(metadata, payload);
Console.WriteLine(DataJson.Write(manifest));
Console.WriteLine($"Compressed library: {new FileInfo(payloadPath).Length:N0} bytes");
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}

static void ValidateEnhancements(Dataset data)
{
    var errors = EnhancementEvidence.ValidateCandidate(data, EnhancementLibraryRepair.BundledEvidence, out var unsupported);
    if (errors.Count > 0) throw new InvalidDataException("Enhancement candidate validation failed:\n" + string.Join("\n", errors));
    Console.WriteLine($"Enhancement candidate validation passed: {data.ReferenceCards!.Count(c => c.Section == "Enhancements")} cards; {unsupported} source conditions remain unsupported (not guessed).");
}

static void ValidateArmyRules(Dataset data)
{
    var errors = ArmyRuleLibraryRecovery.ValidateCandidate(data, ArmyRuleLibraryRecovery.BundledEvidence);
    if (errors.Count > 0) throw new InvalidDataException("Army-rule candidate validation failed:\n" + string.Join("\n", errors));
    Console.WriteLine($"Army-rule source/import/display validation passed: {data.Units.Select(u => u.Faction).Distinct().Count()} factions.");
}
