using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace GrimList.Core;

// Normalize BattleScribe XML and New Recruit JSON to the same tree.
public sealed record BsDataDocument(string FileName, JsonObject Root)
{
    public string Id => Text(Root, "id");
    public string Name => Text(Root, "name");
    public static string Text(JsonObject node, string key) => node[key]?.ToString() ?? "";
    public static List<JsonObject> Children(JsonObject node, string key) => node[key] is JsonArray array ? array.OfType<JsonObject>().ToList() : [];
    public static bool Flag(JsonObject node, string key) => Text(node, key).Equals("true", StringComparison.OrdinalIgnoreCase);
    public static BsDataDocument Parse(string fileName, string text)
    {
        if (text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('<'))
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
            var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Empty BattleScribe XML.");
            if (root.Name.LocalName is not ("catalogue" or "gameSystem")) throw new InvalidDataException("Choose a BattleScribe catalogue or game system.");
            return new(fileName, XmlNode(root));
        }
        var json = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Empty BSData file.");
        var catalogue = json["catalogue"] as JsonObject ?? json["gameSystem"] as JsonObject;
        if (catalogue == null || Text(catalogue, "id").Length == 0) throw new InvalidDataException("This JSON is not a BSData catalogue/game system.");
        return new(fileName, catalogue);
    }
    private static JsonObject XmlNode(XElement element)
    {
        JsonObject result = [];
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration)) result[attribute.Name.LocalName] = attribute.Value;
        if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value)) result["$text"] = element.Value;
        foreach (var group in element.Elements())
            result[group.Name.LocalName] = group.HasElements ? new JsonArray(group.Elements().Select(e => (JsonNode)XmlNode(e)).ToArray()) : JsonValue.Create(group.Value);
        return result;
    }
    public static List<BsDataDocument> ReadArchive(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var documents = new List<BsDataDocument>(); long total = 0;
        foreach (var entry in archive.Entries)
        {
            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (extension is not (".json" or ".cat" or ".gst")) continue;
            var parts = entry.FullName.Replace('\\', '/').Split('/');
            if (parts.Any(p => p is "." or "..") || parts.Length > 2 || entry.FullName.StartsWith('/')) continue;
            total += entry.Length;
            if (entry.Length > 20_000_000 || total > 250_000_000 || documents.Count >= 150) throw new InvalidDataException("BSData archive is too large.");
            using var input = entry.Open(); using var reader = new StreamReader(input, Encoding.UTF8);
            var content = reader.ReadToEnd();
            if (extension == ".json" && !content.Contains("\"catalogue\"") && !content.Contains("\"gameSystem\"")) continue;
            documents.Add(Parse(entry.Name, content));
        }
        if (documents.Count == 0) throw new InvalidDataException("The ZIP contains no supported BSData catalogues.");
        return documents;
    }
}
