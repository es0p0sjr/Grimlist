using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

// Disposable, versioned projections of the authoritative repository. Import/export
// continue to use the complete library; an editing view must never be imported.
public sealed partial class FactionLibrary
{
    internal record Shard(List<UnitDefinition> Units, List<ReferenceCard> Cards);
    internal record UnitBinding(int Position, int? ProfileSource, int? PointsSource);
    internal record CardBinding(int Position, int? Source);
    internal record Part(string File, List<UnitBinding> Units, List<CardBinding> Cards);
    internal record HeaderBindings(List<int?> Detachments, List<int?> Coverage);
    internal record Manifest(string Version, string Revision, Dataset Header, List<string> Factions, int UnitCount,
        Dictionary<string, string> UnitFactions, Dictionary<string, string[]> Dependencies,
        Dictionary<string, Part> Parts, List<SourceInfo> Sources, string AbilitiesFile,
        Dictionary<string, string> MetadataRevisions, Dictionary<string, string> RuleRevisions, string GlobalRevision,
        HeaderBindings? HeaderSources = null);
    // Content layout is independent of recovery policy. A changed recovery still
    // rebuilds the manifest, but identical recovered content can be reused.
    private const string ContentFormat = "faction-content-1/";
    private static readonly JsonSerializerOptions Json = new(DataJson.Options) { WriteIndented = false,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(CacheJsonContext.Default, new DefaultJsonTypeInfoResolver()) };
    private readonly IArmyRepository repository;
    private readonly Func<LoadedLibrary> load;
    private readonly string version;
    private Manifest manifest;
    private Dataset? fallback;
    private Dictionary<string, List<AbilityProfile>>? sharedAbilities;
    private readonly Dictionary<string, (Dataset Data, long Used, string[] Files)> views = [];
    private readonly Dictionary<string, Shard> loadedParts = [];
    private readonly Dictionary<string, string> summaryRevisions = [];
    private long clock;
    public Dataset Header => manifest.Header;
    public IReadOnlyList<string> Factions => manifest.Factions;
    public int UnitCount => manifest.UnitCount;
    public string Revision => manifest.Revision;
    public string CacheIdentity => Hash(version + "/" + Revision);
    public int FullReads { get; private set; }
    public int ShardReads { get; private set; }
    public int ShardReuses { get; private set; }
    public int CachedViews => views.Count;
    public int ContentWrites { get; private set; }
    public int ContentReuses { get; private set; }
    public int SharedAbilityReads { get; private set; }
    public int DependencyCalculations { get; private set; }
    public int DependencyReuses { get; private set; }
    public bool MigratedManifest { get; private set; }
    public string? FactionContentRevision(string faction) => manifest.Parts.GetValueOrDefault(Key(faction))?.File;
    private string Root => Path.Combine(repository.DirectoryPath, "derived-cache", "factions");
    private string ManifestPath(string revision) => Path.Combine(Root, Hash(version + "/" + revision) + ".json.gz");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Key(string faction) => ReferenceModel.Key(faction);

    private FactionLibrary(IArmyRepository repository, Func<LoadedLibrary> load, string version, Manifest manifest)
    { this.repository = repository; this.load = load; this.version = version; this.manifest = manifest; }

    public static FactionLibrary? Open(IArmyRepository repository, Func<LoadedLibrary> load, string variant = "", FactionLibrary? previous = null)
    {
        var version = Version(repository, variant);
        var revision = repository.LibraryRevision();
        // Repositories without a reliable revision cannot safely use a disk cache.
        if (revision == null) return null;
        using var maintenance = TryCacheLock(repository.DirectoryPath);
        if (maintenance == null) return null;
        var root = Path.Combine(repository.DirectoryPath, "derived-cache", "factions");
        foreach (var layout in new[] { version, "factions-3/" + version["factions-4/".Length..] })
        {
            try
            {
                var path = Path.Combine(root, Hash(layout + "/" + revision) + ".json.gz");
                var cached = LocalPerformance.Measure("factions.manifest-read", () => Read<Manifest>(path));
                if (cached.Version != layout || cached.Revision != revision || cached.Header.Units.Count != 0
                    || cached.MetadataRevisions == null || cached.RuleRevisions == null
                    || !ValidFile(cached.AbilitiesFile) || cached.Parts.Values.Any(p => !ValidFile(p.File)
                        || p.Units.Any(u => !ValidSource(u.ProfileSource, cached) || !ValidSource(u.PointsSource, cached))
                        || p.Cards.Any(c => !ValidSource(c.Source, cached)))) throw new InvalidDataException("Faction cache identity");
                if (layout == version && cached.HeaderSources == null) throw new InvalidDataException("Missing pooled header sources");
                cached = ExpandHeader(cached) with { Version = version };
                var warm = new FactionLibrary(repository, load, version, cached with { Header = cached.Header with { IsScoped = true } });
                if (!warm.Lease()) return null;
                warm.ReuseContent(previous);
                if (layout != version)
                {
                    warm.MigratedManifest = true;
                    try { Write(warm.ManifestPath(revision), PackHeader(warm.manifest)); } catch (Exception error) when (CacheError(error)) { }
                }
                warm.Retain(); return warm;
            }
            catch (Exception e) when (CacheError(e)) { }
        }
        maintenance.Dispose(); // Never wait for SQLite while holding the cache lock.
        var loaded = load();
        if (loaded.Dataset == null || loaded.Revision == null) return null;
        using var buildLock = TryCacheLock(repository.DirectoryPath);
        if (buildLock == null) return null;
        var result = new FactionLibrary(repository, load, version, null!);
        LocalPerformance.Measure("factions.build", () => result.Build(loaded));
        if (!result.Lease()) return null;
        result.ReuseContent(previous);
        result.Retain(); return result;
    }

    private static string Version(IArmyRepository repository, string variant) => "factions-4/" + LibraryRecoveryVersion.Current
        + "/" + LibraryRecoveryVersion.Availability + "/" + variant + "/" + RecoveryEvidenceRevision(repository.DirectoryPath);

    internal static IDisposable? Prepare(IArmyRepository repository, LoadedLibrary loaded, string variant)
    {
        if (loaded.Dataset == null || loaded.Revision == null) return null;
        using var maintenance = CacheLock(repository.DirectoryPath);
        var prepared = new FactionLibrary(repository, () => loaded, Version(repository, variant), null!);
        prepared.Build(loaded);
        if (prepared.fallback != null) throw new IOException("The update's faction projections could not be prepared.");
        // Verify every output before the enclosing repository transaction commits.
        foreach (var part in prepared.manifest.Parts.Values)
        {
            var shard = Read<Shard>(Path.Combine(prepared.Root, part.File + ".json.gz"));
            if (shard.Units.Count != part.Units.Count || shard.Cards.Count != part.Cards.Count)
                throw new InvalidDataException("Prepared faction projection is incomplete.");
        }
        Read<Dictionary<string, List<AbilityProfile>>>(Path.Combine(prepared.Root, prepared.manifest.AbilitiesFile + ".json.gz"));
        ExpandHeader(Read<Manifest>(prepared.ManifestPath(loaded.Revision)));
        // Retention runs after committed activation on the next Open. A rolled-back
        // revision can leave only unreferenced, disposable cache files.
        return new FileStream(prepared.ManifestPath(loaded.Revision) + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
    }

    private void ReuseContent(FactionLibrary? previous)
    {
        if (previous == null || fallback != null) return;
        var live = manifest.Parts.Values.Select(p => p.File).ToHashSet();
        foreach (var (file, part) in previous.loadedParts) if (live.Contains(file)) loadedParts[file] = part;
        if (manifest.AbilitiesFile == previous.manifest.AbilitiesFile) sharedAbilities = previous.sharedAbilities;
    }

    private static bool ValidFile(string file) => file.Length == 64 && file.All(Uri.IsHexDigit);
    private static bool ValidSource(int? source, Manifest manifest) => source == null || source >= 0 && source < manifest.Sources.Count;

    private void Build(LoadedLibrary loaded)
    {
        FullReads++;
        var data = loaded.Dataset!;
        var factions = data.Units.Select(u => u.Faction).Distinct().Order().ToList();
        Dictionary<string, Shard> shards = new(StringComparer.Ordinal);
        Dictionary<string, Part> bindings = new(StringComparer.Ordinal);
        List<SourceInfo> sources = []; Dictionary<SourceInfo, int> sourceIds = [];
        int? Source(SourceInfo? source)
        {
            if (source == null) return null;
            if (!sourceIds.TryGetValue(source, out var id)) { id = sources.Count; sourceIds.Add(source, id); sources.Add(source); }
            return id;
        }
        Shard GetPart(string key)
        {
            if (!shards.TryGetValue(key, out var part)) { shards[key] = part = new([], []); bindings[key] = new("", [], []); }
            return part;
        }
        Dictionary<string, string> identities = new(StringComparer.Ordinal);
        foreach (var (unit, position) in data.Units.Select((u, i) => (u, i)))
        {
            var key = Key(unit.Faction); GetPart(key).Units.Add(unit with { ProfileSource = null, PointsSource = null });
            bindings[key].Units.Add(new(position, Source(unit.ProfileSource), Source(unit.PointsSource)));
            identities.TryAdd(unit.Key, key); identities.TryAdd(unit.NameKey, key);
            foreach (var alias in unit.Aliases ?? []) identities.TryAdd(alias, key);
        }
        foreach (var (card, position) in (data.ReferenceCards ?? []).Select((c, i) => (c, i)))
        {
            var canonical = ReferenceMatching.Match(ReferenceModel.Canonical(card, data.Source), data);
            var key = canonical.Details!.Scope == "Global" ? "" : Key(canonical.Faction);
            GetPart(key).Cards.Add(card with { Source = null }); bindings[key].Cards.Add(new(position, Source(card.Source)));
        }
        GetPart(""); // Shared/global references are always available.
        var abilities = data.Units.GroupBy(u => u.ProfileSource?.Edition ?? data.Source.Edition)
            .ToDictionary(g => g.Key, g => g.SelectMany(u => u.Profile?.Abilities ?? []).Distinct().ToList());
        var dependencies = LocalPerformance.Measure("factions.dependencies", () => BuildDependencies(data, factions));
        var scopes = shards.Keys.Concat(factions.Select(Key)).Concat((data.Detachments ?? []).Select(d => Owner(d.Faction)))
            .Concat((data.FactionContexts ?? []).Select(c => Owner(c.Faction))).Concat((data.DetachmentSources ?? []).Select(s => Owner(s.Faction)))
            .Concat((data.ReferenceCoverage ?? []).Select(c => Owner(c.Faction))).Concat((data.RulesPacks ?? []).SelectMany(p => p.Factions.Select(Owner)))
            .Append("").Distinct().ToArray();
        var metadata = scopes.ToDictionary(k => k, k => ContentHash(MetadataFor(data, k)));
        var rules = shards.ToDictionary(p => p.Key, p => ContentHash(p.Value with {
            Units = data.Units.Where(u => Key(u.Faction) == p.Key).Select(u => u with {
                ProfileSource = SemanticSource(u.ProfileSource), PointsSource = SemanticSource(u.PointsSource) }).ToList(),
            Cards = p.Value.Cards // Card provenance bindings are tracked separately below.
        }));
        foreach (var key in rules.Keys.ToArray()) rules[key] = Hash(rules[key] + ContentHash(bindings[key].Cards
            .Select(c => SemanticSource(c.Source == null ? null : sources[c.Source.Value])).ToArray()));
        var global = ContentHash(data with { Source = SemanticSource(data.Source)!, Units = [], ReferenceCards = [],
            Detachments = null, FactionContexts = null, DetachmentSources = null, ReferenceCoverage = null, RulesPacks = null });
        manifest = new(version, loaded.Revision!, data with { Units = [], ReferenceCards = [], IsScoped = true }, factions, data.Units.Count,
            identities, dependencies, bindings, sources, "", metadata, rules, global);
        views.Clear(); loadedParts.Clear(); summaryRevisions.Clear(); fallback = null; sharedAbilities = null;
        try
        {
            foreach (var (key, part) in shards) bindings[key] = bindings[key] with { File = WriteContent(part) };
            manifest = manifest with { AbilitiesFile = WriteContent(abilities) };
            // Publish last: interrupted builds cannot expose a partial manifest.
            Write(ManifestPath(Revision), PackHeader(manifest));
        }
        catch (Exception e) when (CacheError(e)) { fallback = data; }
    }

    private string WriteContent<T>(T content)
    {
        var identity = Hash(ContentFormat + ContentHash(content));
        var path = Path.Combine(Root, identity + ".json.gz");
        try
        {
            using var existing = File.OpenRead(path); VerifyChecksum(existing);
            ContentReuses++; return identity;
        }
        catch (Exception e) when (CacheError(e)) { }
        Write(path, content); ContentWrites++; return identity;
    }

    private static string ContentHash<T>(T content)
    {
        using var sha = SHA256.Create();
        using (var sink = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        { JsonSerializer.Serialize(sink, content, Json); sink.FlushFinalBlock(); }
        return Convert.ToHexString(sha.Hash!);
    }

    private string[] KeysFor(ArmyList army) => (manifest.Dependencies.GetValueOrDefault(Key(army.Faction)) ?? [Key(army.Faction)])
        .Concat(army.Entries.Select(e => manifest.UnitFactions.GetValueOrDefault(e.UnitKey)).OfType<string>())
        .Append(Key(army.Faction)).Append("").Distinct().Order(StringComparer.Ordinal).ToArray();

    // Definitions used only for displaying weapon explanations are separate from
    // inputs to ArmyCalculator. Relevant roster/shared rules are in the part hashes.
    public string SummaryRevision(ArmyList army)
    {
        if (Header.SchemaVersion is < 1 or > 4) return Hash(version + "/" + Revision); // Unknown contracts invalidate conservatively.
        var keys = KeysFor(army);
        if (keys.Any(k => !manifest.MetadataRevisions.ContainsKey(k))) return Hash(version + "/" + Revision);
        var key = string.Join("|", keys);
        if (summaryRevisions.TryGetValue(key, out var revision)) return revision;
        revision = Hash(LibraryRecoveryVersion.Evaluation + "/" + LibraryRecoveryVersion.Compilation + "/" + LibraryRecoveryVersion.Catalogue
            + "/" + manifest.GlobalRevision + "/" + RelativeOrder(keys) + "/" + string.Join("/", keys.Select(k => k + ":"
                + manifest.RuleRevisions.GetValueOrDefault(k, "missing") + ":" + manifest.MetadataRevisions.GetValueOrDefault(k, "missing"))));
        if (summaryRevisions.Count >= 64) summaryRevisions.Clear();
        summaryRevisions[key] = revision; return revision;
    }

    public string ViewRevision(ArmyList army) => Hash(SummaryRevision(army) + "/" + manifest.AbilitiesFile + "/" + ContentHash(
        KeysFor(army).Select(k => new {
            Metadata = MetadataFor(Header, k, semantic: false),
            Units = manifest.Parts.GetValueOrDefault(k)?.Units.Select(u => new {
                Profile = u.ProfileSource == null ? null : manifest.Sources[u.ProfileSource.Value],
                Points = u.PointsSource == null ? null : manifest.Sources[u.PointsSource.Value] }).ToArray(),
            Cards = manifest.Parts.GetValueOrDefault(k)?.Cards.Select(c => c.Source == null ? null : manifest.Sources[c.Source.Value]).ToArray()
        }).ToArray()));

    public Dataset ForArmy(ArmyList army)
    {
        if (fallback != null) return fallback;
        var keys = KeysFor(army).Where(manifest.Parts.ContainsKey).ToArray();
        var key = string.Join("|", keys);
        if (views.TryGetValue(key, out var cached)) { views[key] = (cached.Data, ++clock, cached.Files); return cached.Data; }
        try
        {
            var parts = LocalPerformance.Measure("factions.shards-read", () => keys.Select(k =>
            {
                var binding = manifest.Parts[k];
                if (loadedParts.TryGetValue(binding.File, out var part)) ShardReuses++;
                else { part = Read<Shard>(Path.Combine(Root, binding.File + ".json.gz")); loadedParts[binding.File] = part; ShardReads++; }
                if (part.Units.Count != binding.Units.Count || part.Cards.Count != binding.Cards.Count) throw new InvalidDataException("Faction binding mismatch");
                return (Part: part, Binding: binding);
            }).ToArray());
            SourceInfo? Source(int? id) => id == null ? null : manifest.Sources[id.Value];
            if (sharedAbilities == null)
            {
                sharedAbilities = LocalPerformance.Measure("factions.abilities-read", () => Read<Dictionary<string, List<AbilityProfile>>>(Path.Combine(Root, manifest.AbilitiesFile + ".json.gz")));
                SharedAbilityReads++;
            }
            var view = Header with {
                Units = parts.SelectMany(p => p.Part.Units.Zip(p.Binding.Units)).OrderBy(u => u.Second.Position)
                    .Select(u => u.First with { ProfileSource = Source(u.Second.ProfileSource), PointsSource = Source(u.Second.PointsSource) }).ToList(),
                ReferenceCards = parts.SelectMany(p => p.Part.Cards.Zip(p.Binding.Cards)).OrderBy(c => c.Second.Position)
                    .Select(c => c.First with { Source = Source(c.Second.Source) }).ToList(),
                SharedAbilities = sharedAbilities
            };
            // Retain at most two recent views. Current callers may still own their
            // active snapshot, but visiting factions never grows an unbounded cache.
            if (views.Count >= 2) views.Remove(views.MinBy(p => p.Value.Used).Key);
            views[key] = (view, ++clock, parts.Select(p => p.Binding.File).ToArray());
            var live = views.Values.SelectMany(v => v.Files).ToHashSet();
            foreach (var unused in loadedParts.Keys.Where(k => !live.Contains(k)).ToArray()) loadedParts.Remove(unused);
            return view;
        }
        catch (Exception e) when (CacheError(e))
        {
            var loaded = load();
            if (loaded.Revision != Revision) throw new InvalidDataException("The library changed. Reload the library before continuing.", e);
            Build(loaded);
            // A damaged/unwritable derived cache must not make units disappear.
            fallback = loaded.Dataset; return fallback!;
        }
    }

    internal static bool CacheError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or ArgumentException or NullReferenceException;
    internal static T Read<T>(string path)
    {
        using var file = File.OpenRead(path);
        LocalPerformance.Measure("cache.checksum", () => VerifyChecksum(file));
        file.Position = 32;
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var info = LocalPerformance.Measure("cache.json-metadata", () => (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T)));
        return LocalPerformance.Measure("cache.deserialize", () => JsonSerializer.Deserialize(gzip, info)) ?? throw new InvalidDataException("Empty faction cache");
    }
    private static void VerifyChecksum(Stream file)
    {
        var checksum = new byte[32]; file.ReadExactly(checksum);
        if (!CryptographicOperations.FixedTimeEquals(checksum, SHA256.HashData(file))) throw new InvalidDataException("Faction cache checksum");
    }
    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                file.Write(new byte[32]);
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true)) JsonSerializer.Serialize(gzip, value, Json);
                file.Position = 32; var checksum = SHA256.HashData(file); file.Position = 0; file.Write(checksum); file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
