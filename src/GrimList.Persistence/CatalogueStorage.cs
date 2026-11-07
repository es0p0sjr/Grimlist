using System.Globalization;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Persistence;

// Immutable objects shared by historical source snapshots. Only the manifest binds
// content to exact provenance and ordering. Nothing here deletes source history.
internal static class CatalogueStorage
{
    internal const string Prefix = "GLCM1:";
    internal const int ChunkBytes = 48 * 1024;
    private const int MaximumBytes = 128_000_000;
    private static readonly JsonSerializerOptions Json = new(DataJson.Options) { WriteIndented = false };
    private sealed record UnitRef(string Hash, int Index, int? Profile, int? Points);
    private sealed record CardRef(string Hash, int Index, int? Source);
    private sealed record Manifest(Dataset Header, List<SourceInfo> Sources, List<UnitRef> Units, List<CardRef>? Cards,
        List<int?> DetachmentSources, List<int?> CoverageSources);

    public static void Create(ISqliteConnection db)
    {
        db.Run("CREATE TABLE IF NOT EXISTS catalogue_objects(hash TEXT PRIMARY KEY,bytes INTEGER NOT NULL,compressed_bytes INTEGER NOT NULL,digest TEXT NOT NULL)");
        db.Run("CREATE TABLE IF NOT EXISTS catalogue_chunks(object_hash TEXT NOT NULL REFERENCES catalogue_objects(hash),position INTEGER NOT NULL,content BLOB NOT NULL,PRIMARY KEY(object_hash,position))");
    }
    public static void Write(ISqliteConnection db, string id, Dataset data)
    {
        using var writer = new ObjectWriter(db);
        List<SourceInfo> sources = []; Dictionary<SourceInfo, int> ids = [];
        int? Source(SourceInfo? source)
        {
            if (source == null) return null;
            if (!ids.TryGetValue(source, out var index)) { index = sources.Count; sources.Add(source); ids.Add(source, index); }
            return index;
        }
        // Stable hash buckets, independent of source ordering or faction size. Small
        // pages restore cross-definition compression and avoid thousands of tiny SQL
        // reads; an edited definition replaces only its bucket (256 per kind).
        static int Bucket(string key) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))[0];
        Dictionary<int, (string Hash, int Index)> unitLocations = [], cardLocations = [];
        foreach (var page in data.Units.Select((u, i) => (Value: u, Position: i)).GroupBy(p => Bucket(p.Value.Key)))
        {
            var items = page.OrderBy(p => p.Value.Key, StringComparer.Ordinal).ToArray();
            var hash = writer.Store(items.Select(p => p.Value with { ProfileSource = null, PointsSource = null }).ToList());
            for (var i = 0; i < items.Length; i++) unitLocations[items[i].Position] = (hash, i);
        }
        foreach (var page in (data.ReferenceCards ?? []).Select((c, i) => (Value: c, Position: i)).GroupBy(p => Bucket(ReferenceModel.LogicalKey(p.Value))))
        {
            var items = page.OrderBy(p => ReferenceModel.LogicalKey(p.Value), StringComparer.Ordinal).ToArray();
            var hash = writer.Store(items.Select(p => p.Value with { Source = null }).ToList());
            for (var i = 0; i < items.Length; i++) cardLocations[items[i].Position] = (hash, i);
        }
        var units = data.Units.Select((u, i) => new UnitRef(unitLocations[i].Hash, unitLocations[i].Index,
            Source(u.ProfileSource), Source(u.PointsSource))).ToList();
        var cards = data.ReferenceCards?.Select((c, i) => new CardRef(cardLocations[i].Hash, cardLocations[i].Index, Source(c.Source))).ToList();
        var detachmentSources = (data.Detachments ?? []).Select(d => Source(d.Source)).ToList();
        var coverageSources = (data.ReferenceCoverage ?? []).Select(c => Source(c.Source)).ToList();
        var manifest = new Manifest(data with { Units = [], ReferenceCards = null,
            Detachments = data.Detachments?.Select(d => d with { Source = null }).ToList(),
            ReferenceCoverage = data.ReferenceCoverage?.Select(c => c with { Source = null }).ToList()
        }, sources, units, cards, detachmentSources, coverageSources);
        var manifestHash = writer.Store(manifest);
        db.Run("INSERT INTO dataset_snapshots VALUES(?,?)", id, Prefix + manifestHash);
    }
    public static Dataset Read(ISqliteConnection db, string hash)
    {
        var manifest = Read<Manifest>(db, hash);
        SourceInfo? Source(int? index) => index == null ? null : index >= 0 && index < manifest.Sources.Count
            ? manifest.Sources[index.Value] : throw new InvalidDataException("Invalid catalogue provenance binding.");
        var units = manifest.Units.Select(u => u.Hash).Distinct().ToDictionary(h => h, h => Read<List<UnitDefinition>>(db, h));
        var cards = (manifest.Cards ?? []).Select(c => c.Hash).Distinct().ToDictionary(h => h, h => Read<List<ReferenceCard>>(db, h));
        static T Item<T>(List<T> page, int index) => index >= 0 && index < page.Count ? page[index] : throw new InvalidDataException("Invalid catalogue definition binding.");
        if (manifest.DetachmentSources.Count != (manifest.Header.Detachments?.Count ?? 0)
            || manifest.CoverageSources.Count != (manifest.Header.ReferenceCoverage?.Count ?? 0)) throw new InvalidDataException("Invalid catalogue header bindings.");
        var data = manifest.Header with {
            Units = manifest.Units.Select(u => Item(units[u.Hash], u.Index) with { ProfileSource = Source(u.Profile), PointsSource = Source(u.Points) }).ToList(),
            ReferenceCards = manifest.Cards?.Select(c => Item(cards[c.Hash], c.Index) with { Source = Source(c.Source) }).ToList(),
            Detachments = manifest.Header.Detachments?.Select((d, i) => d with { Source = Source(manifest.DetachmentSources[i]) }).ToList(),
            ReferenceCoverage = manifest.Header.ReferenceCoverage?.Select((c, i) => c with { Source = Source(manifest.CoverageSources[i]) }).ToList()
        };
        data.Validate(); return data;
    }
    // Buffers belong to this transaction's writer, never a cache of unverified DB
    // contents. Reuse serializes once and compares the stored decompressed bytes to
    // that exact input while checking both persisted hashes and all chunk bounds.
    private sealed class ObjectWriter(ISqliteConnection db) : IDisposable
    {
        private readonly ArrayBufferWriter<byte> buffer = new(64 * 1024);
        private readonly MemoryStream compressed = new();
        private readonly byte[] verification = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        private Utf8JsonWriter? jsonWriter;
        public string Store<T>(T value)
        {
            buffer.ResetWrittenCount();
            if (jsonWriter == null) jsonWriter = new(buffer); else jsonWriter.Reset(buffer);
            JsonSerializer.Serialize(jsonWriter, value, Json); jsonWriter.Flush();
            var json = buffer.WrittenSpan;
            if (json.Length > MaximumBytes) throw new InvalidDataException("Catalogue object is too large.");
            var hash = Convert.ToHexString(SHA256.HashData(json));
            var row = Metadata(db, hash);
            if (row != null)
            {
                var stored = ReadCompressed(db, hash, row, compressed);
                if (int.Parse(row[0]!, CultureInfo.InvariantCulture) != json.Length)
                    throw new InvalidDataException("Catalogue object length mismatch.");
                using var gzip = new GZipStream(stored, CompressionMode.Decompress, leaveOpen: true);
                for (var offset = 0; offset < json.Length;)
                {
                    var part = verification.AsSpan(0, Math.Min(ChunkBytes, json.Length - offset));
                    gzip.ReadExactly(part);
                    if (!part.SequenceEqual(json.Slice(offset, part.Length)))
                        throw new InvalidDataException("Catalogue object checksum mismatch.");
                    offset += part.Length;
                }
                if (gzip.ReadByte() != -1) throw new InvalidDataException("Catalogue object length mismatch.");
                return hash;
            }
            compressed.SetLength(0); compressed.Position = 0;
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(json);
            var size = checked((int)compressed.Length);
            if (size > MaximumBytes) throw new InvalidDataException("Compressed catalogue object is too large.");
            var bytes = compressed.GetBuffer();
            db.Run("INSERT INTO catalogue_objects VALUES(?,?,?,?)", hash, json.Length, size,
                Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, size))));
            IEnumerable<object?[]> Chunks()
            {
                for (var offset = 0; offset < size; offset += ChunkBytes)
                    yield return [hash, offset / ChunkBytes, bytes.AsSpan(offset, Math.Min(ChunkBytes, size - offset)).ToArray()];
            }
            db.RunBatch("INSERT INTO catalogue_chunks VALUES(?,?,?)", Chunks());
            return hash;
        }
        public void Dispose()
        {
            jsonWriter?.Dispose(); compressed.Dispose(); ArrayPool<byte>.Shared.Return(verification);
        }
    }
    private static T Read<T>(ISqliteConnection db, string hash)
        => JsonSerializer.Deserialize<T>(ReadBytes(db, hash), Json) ?? throw new InvalidDataException("Empty catalogue object.");
    private static byte[] ReadBytes(ISqliteConnection db, string hash)
    {
        var row = Metadata(db, hash) ?? throw new InvalidDataException("Missing catalogue object.");
        using var compressed = ReadCompressed(db, hash, row);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        var json = new byte[int.Parse(row[0]!, CultureInfo.InvariantCulture)]; gzip.ReadExactly(json);
        if (gzip.ReadByte() != -1 || Convert.ToHexString(SHA256.HashData(json)) != hash)
            throw new InvalidDataException("Catalogue object checksum mismatch.");
        return json;
    }
    private static string?[]? Metadata(ISqliteConnection db, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid catalogue object identity.");
        return db.Run("SELECT bytes,compressed_bytes,digest,(SELECT count(*) FROM catalogue_chunks WHERE object_hash=hash) FROM catalogue_objects WHERE hash=?", hash).SingleOrDefault();
    }
    private static MemoryStream ReadCompressed(ISqliteConnection db, string hash, string?[] row, MemoryStream? destination = null)
    {
        if (!int.TryParse(row[0], out var length) || length <= 0 || length > MaximumBytes
            || !int.TryParse(row[1], out var size) || size <= 0 || size > MaximumBytes)
            throw new InvalidDataException("Missing or oversized catalogue object.");
        var count = (size - 1) / ChunkBytes + 1;
        if (row[3] != count.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Catalogue chunk count mismatch.");
        var compressed = destination ?? new MemoryStream(size);
        try
        {
            compressed.SetLength(0); compressed.Position = 0;
            for (var position = 0; position < count; position++)
            {
                // WHERE rejects oversized corrupt rows before materializing a Java value.
                var bytes = db.ReadBlob("SELECT content AS value FROM catalogue_chunks WHERE object_hash=? AND position=? AND typeof(content)='blob' AND length(content)<=49152", hash, position);
                if (bytes == null || bytes.Length != Math.Min(ChunkBytes, size - position * ChunkBytes))
                    throw new InvalidDataException("Missing or truncated catalogue chunk.");
                compressed.Write(bytes);
            }
            if (Convert.ToHexString(SHA256.HashData(compressed.GetBuffer().AsSpan(0, size))) != row[2])
                throw new InvalidDataException("Catalogue compressed checksum mismatch.");
            compressed.Position = 0;
            return compressed;
        }
        catch { if (destination == null) compressed.Dispose(); throw; }
    }
}
