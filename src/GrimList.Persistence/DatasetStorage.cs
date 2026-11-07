using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Persistence;

// Keep every database value crossing Android's Java bridge bounded. The JSON is
// streamed through gzip; neither a complete JSON string nor a complete compressed
// byte array is needed. Existing uncompressed snapshots remain readable in pieces.
internal static class DatasetStorage
{
    private const string Prefix = "GLDS1:";
    private const int ChunkBytes = 48 * 1024;
    private static readonly JsonSerializerOptions Json = new(DataJson.Options) { WriteIndented = false };

    public static void Write(ISqliteConnection db, string id, Dataset data)
    {
        // Caller owns the transaction, including publication of active_source.
        using var output = new ChunkWriter(db, id);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(gzip, data, Json);
        var header = output.Complete();
        db.Run("INSERT INTO dataset_snapshots VALUES(?,?)", id, header);
    }

    public static Dataset? Read(ISqliteConnection db, string id)
    {
        var header = db.Run("SELECT substr(content,1,256) FROM dataset_snapshots WHERE source_id=?", id).FirstOrDefault()?[0];
        if (header == null) return null;
        if (header.StartsWith(CatalogueStorage.Prefix, StringComparison.Ordinal))
            return CatalogueStorage.Read(db, header[CatalogueStorage.Prefix.Length..]);
        if (!header.StartsWith(Prefix, StringComparison.Ordinal))
            return ReadLegacy<Dataset>(db, "dataset_snapshots", id);
        var fields = header[Prefix.Length..].Split(':');
        if (fields.Length != 3 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var chunks) || chunks <= 0
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0
            || chunks != (bytes - 1) / ChunkBytes + 1 || fields[2].Length != 64 || !fields[2].All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid library snapshot header.");
        using var input = new ChunkReader(db, id, chunks, bytes, fields[2]);
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        var result = JsonSerializer.Deserialize<Dataset>(gzip, Json) ?? throw new InvalidDataException("Empty library snapshot.");
        // Deserialize consumes through EOF; explicitly verify all compressed bytes,
        // including the trailer, so missing/truncated chunks never become a library.
        input.Verify();
        return result;
    }

    public static T? ReadLegacy<T>(ISqliteConnection db, string table, string id)
    {
        if (table is not ("dataset_snapshots" or "source_details")) throw new ArgumentException("Unknown library table.", nameof(table));
        var length = db.Run($"SELECT length(CAST(content AS BLOB)) FROM {table} WHERE source_id=?", id).FirstOrDefault()?[0];
        if (length == null) return default;
        using var input = new LegacyReader(db, table, id, long.Parse(length, CultureInfo.InvariantCulture));
        return JsonSerializer.Deserialize<T>(input, Json) ?? throw new InvalidDataException("Empty legacy library snapshot.");
    }

    private abstract class SequentialStream : Stream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private sealed class ChunkWriter(ISqliteConnection db, string id) : SequentialStream
    {
        private readonly byte[] buffer = new byte[ChunkBytes];
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int buffered, chunks;
        private long bytes;
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] source, int offset, int count) => Write(source.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> source)
        {
            hash.AppendData(source); bytes += source.Length;
            while (!source.IsEmpty)
            {
                var count = Math.Min(buffer.Length - buffered, source.Length);
                source[..count].CopyTo(buffer.AsSpan(buffered)); buffered += count; source = source[count..];
                if (buffered == buffer.Length) SaveChunk();
            }
        }
        private void SaveChunk()
        {
            db.Run("INSERT INTO dataset_chunks(source_id,position,content) VALUES(?,?,?)", id, chunks++, Convert.ToBase64String(buffer, 0, buffered));
            buffered = 0;
        }
        public string Complete()
        {
            if (buffered > 0) SaveChunk();
            return FormattableString.Invariant($"{Prefix}{chunks}:{bytes}:{Convert.ToHexString(hash.GetHashAndReset())}");
        }
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }

    private abstract class BufferedReader : SequentialStream
    {
        private byte[] buffer = [];
        private int offset;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected abstract byte[] Next();
        protected bool HasUnreadBytes => offset != buffer.Length;
        public override int Read(byte[] destination, int start, int count) => Read(destination.AsSpan(start, count));
        public override int Read(Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;
            if (offset == buffer.Length) { buffer = Next(); offset = 0; }
            var count = Math.Min(destination.Length, buffer.Length - offset);
            buffer.AsSpan(offset, count).CopyTo(destination); offset += count; return count;
        }
    }

    private sealed class ChunkReader(ISqliteConnection db, string id, int chunks, long bytes, string expectedHash) : BufferedReader
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int position;
        private bool verified;
        protected override byte[] Next()
        {
            if (position == chunks) return [];
            // Bound even a corrupt row before it can cross the Java bridge.
            var row = db.Run("SELECT length(content),substr(content,1,65536) FROM dataset_chunks WHERE source_id=? AND position=?", id, position).SingleOrDefault();
            if (row == null || !int.TryParse(row[0], out var length) || length > 65536 || length <= 0 || length != row[1]?.Length)
                throw new InvalidDataException("Missing or oversized library snapshot chunk.");
            byte[] result;
            try { result = Convert.FromBase64String(row[1]!); }
            catch (FormatException e) { throw new InvalidDataException("Invalid library snapshot chunk.", e); }
            if (result.Length != Math.Min(ChunkBytes, bytes - (long)position * ChunkBytes))
                throw new InvalidDataException("Truncated library snapshot chunk.");
            hash.AppendData(result); position++; return result;
        }
        public void Verify()
        {
            if (verified) return;
            if (position != chunks || HasUnreadBytes
                || db.Run("SELECT count(*) FROM dataset_chunks WHERE source_id=?", id).Single()[0] != chunks.ToString(CultureInfo.InvariantCulture)
                || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The library snapshot failed its integrity check.");
            verified = true;
        }
        protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    }

    private sealed class LegacyReader(ISqliteConnection db, string table, string id, long length) : BufferedReader
    {
        private long position;
        protected override byte[] Next()
        {
            if (position == length) return [];
            var count = (int)Math.Min(256 * 1024, length - position);
            // Byte slicing preserves Unicode across boundaries and keeps a legacy
            // row below CursorWindow and Java allocation limits, even at 150+ MB.
            var hex = db.Run($"SELECT hex(substr(CAST(content AS BLOB),?,?)) FROM {table} WHERE source_id=?", position + 1, count, id).SingleOrDefault()?[0];
            if (hex == null || hex.Length != count * 2) throw new InvalidDataException("Truncated legacy library snapshot.");
            position += count; return Convert.FromHexString(hex);
        }
    }
}
