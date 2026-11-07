using System.Text;
using System.Text.Json.Nodes;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

public interface IReferenceSourceAdapter
{
    string Format { get; }
    bool CanRead(string name, string text);
    Dataset Read(string name, string text, string edition);
}

public sealed class StructuredJsonReferenceAdapter : IReferenceSourceAdapter
{
    public string Format => "GrimList reference JSON";
    public bool CanRead(string name, string text) => text.TrimStart().StartsWith('{') && JsonNode.Parse(text)?["catalogue"] == null;
    public Dataset Read(string name, string text, string edition)
    {
        var data = DataJson.Read<Dataset>(text);
        if (data.RulesPacks is { Count: > 0 }) throw new InvalidDataException("Rules packs can change army-building rules. Import this file through the library data import with Datasheets only.");
        if (!data.ReferenceOnly || data.Units.Count > 0) throw new InvalidDataException("Choose a reference-only collection, or use the full dataset import.");
        if (edition.Length == 0 || data.Source.Edition != edition) throw new InvalidDataException("Reference edition must be specified and match the active library.");
        if ((data.ReferenceCards ?? []).Any(c => !ReferenceModel.DisplayOnly(c)))
            throw new InvalidDataException("Supplemental display references cannot alter enhancement or disposition rules. Use the existing dataset workflow for army-building data.");
        var cards = (data.ReferenceCards ?? []).Select(c => ReferenceModel.Canonical(c, data.Source)).ToList();
        data = data with { SchemaVersion = 2, ReferenceCards = cards };
        data.Validate();
        return data;
    }
}

public sealed class BsDataReferenceAdapter : IReferenceSourceAdapter
{
    public string Format => "BSData catalogue";
    public bool CanRead(string name, string text) => text.TrimStart().StartsWith('<') || text.TrimStart().StartsWith('{') && JsonNode.Parse(text)?["catalogue"] != null;
    public Dataset Read(string name, string text, string edition)
    {
        var document = BsDataDocument.Parse(name, text);
        var source = new SourceInfo("BSData reference catalogue", BsDataDocument.Text(document.Root, "revision"), "", "",
            Edition: edition, ContentHash: ReferenceModel.Hash(text));
        var data = BsDataImporter.ImportReferences(document, source);
        return data with { SchemaVersion = 2, ReferenceCards = (data.ReferenceCards ?? []).Where(ReferenceModel.DisplayOnly)
            .Select(c => ReferenceModel.Canonical(c, source)).ToList() };
    }
}

public static class ReferenceSourceReader
{
    private static long reads, parses;
    public static long ReadCount => Interlocked.Read(ref reads);
    public static long ParseCount => Interlocked.Read(ref parses);
    private static readonly IReferenceSourceAdapter[] adapters = [new BsDataReferenceAdapter(), new StructuredJsonReferenceAdapter()];
    public static async Task<Dataset> ReadAsync(PickedDocument document, string edition, CancellationToken cancellation = default)
    {
        Interlocked.Increment(ref reads);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536]; int count;
        while ((count = await document.Content.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > LibraryWorkflow.MaximumDocumentBytes) throw new InvalidDataException("Reference source exceeds 128 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellation).ConfigureAwait(false);
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
        return await Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var adapter = adapters.FirstOrDefault(a => a.CanRead(document.Name, text))
                ?? throw new InvalidDataException("Unsupported reference format. Choose structured GrimList JSON or a BSData catalogue.");
            Interlocked.Increment(ref parses);
            var data = adapter.Read(document.Name, text, edition);
            cancellation.ThrowIfCancellationRequested(); return data;
        }, cancellation).ConfigureAwait(false);
    }
}
