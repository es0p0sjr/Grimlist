using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GrimList.Core;

public record ImportPreview(Dataset Dataset, List<string> Warnings)
{
    public List<ReferenceChange> ReferenceChanges { get; init; } = [];
}
public static class MfmImporter
{
    public static readonly string[] Factions = ["Adepta Sororitas", "Adeptus Custodes", "Adeptus Mechanicus", "Aeldari", "Astra Militarum", "Black Templars", "Blood Angels", "Chaos Daemons", "Chaos Knights", "Chaos Space Marines", "Chaos Titan Legions", "Dark Angels", "Death Guard", "Deathwatch", "Drukhari", "Emperor’s Children", "Genestealer Cults", "Grey Knights", "Imperial Agents", "Imperial Knights", "Leagues of Votann", "Necrons", "Orks", "Space Marines", "Space Wolves", "T’au Empire", "Thousand Sons", "Titan Legions", "Tyranids", "World Eaters", "Unaligned Forces"];
    private static string Normalize(string s) => Regex.Replace(s.Replace('’', '\'').Replace('‘', '\''), @"\s+", " ").Trim().ToUpperInvariant();
    private static string? MatchFaction(string s) => Factions.FirstOrDefault(f => Normalize(f) == Normalize(Regex.Replace(s, @"^(INDEX|CODEX):\s*", "", RegexOptions.IgnoreCase)));
    private static readonly Regex Cost = new(@"^(\d+)\s+models?\s*[.·…\s]*(?:\([+-]?\d+\)\s*)?(\d[\d,]*)\s*pts?\s*$", RegexOptions.IgnoreCase);

    public static ImportPreview ParseText(string text, SourceInfo source, string? fallbackFaction = null)
    {
        var units = new Dictionary<string, UnitDefinition>();
        List<string> warnings = [];
        string? faction = fallbackFaction, name = null, candidate = null;
        var fromCopy = 1;
        var excluded = false;
        var webStyle = text.Contains("YOUR UNIT COST", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"YOUR \d+(ST|ND|RD|TH)", RegexOptions.IgnoreCase);
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = Regex.Replace(raw.Trim().TrimStart('*', '•', '#').Trim(), @"\s+", " ");
            if (line.Length == 0) continue;
            var foundFaction = MatchFaction(line);
            if (foundFaction != null) { faction = foundFaction; name = candidate = null; fromCopy = 1; excluded = false; continue; }
            if (Regex.IsMatch(line, @"^(DETACHMENTS?|ENHANCEMENTS?|WARGEAR OPTIONS|LEADER|SUPPORT)(\b|Image)", RegexOptions.IgnoreCase))
            { excluded = true; name = candidate = null; continue; }
            if (Regex.IsMatch(line, @"^(UNITS|FORGE WORLD POINTS VALUES|POINTS VALUES|SPACE MARINES)$", RegexOptions.IgnoreCase))
            { excluded = false; name = candidate = null; continue; }
            if (line.StartsWith("YOUR ", StringComparison.OrdinalIgnoreCase))
            {
                if (candidate != null) { name = candidate; candidate = null; excluded = false; }
                var tier = Regex.Match(line, @"YOUR (\d+)(?:ST|ND|RD|TH)", RegexOptions.IgnoreCase);
                fromCopy = tier.Success ? int.Parse(tier.Groups[1].Value) : 1;
                continue;
            }
            var cost = Cost.Match(line);
            if (cost.Success)
            {
                if (!webStyle && candidate != null) { name = candidate; candidate = null; }
                if (excluded || faction == null || name == null)
                { warnings.Add($"Skipped points without a clear unit/faction: {line}"); continue; }
                var unit = new UnitDefinition(faction, name, []);
                if (!units.TryGetValue(unit.Key, out var existing)) units[unit.Key] = existing = unit;
                var option = new PointOption(int.Parse(cost.Groups[1].Value), int.Parse(cost.Groups[2].Value.Replace(",", "")), fromCopy);
                if (existing.Options.Any(o => o.Models == option.Models && o.FromCopy == option.FromCopy))
                    throw new InvalidDataException($"Ambiguous duplicate points for {name}, {option.Models} models. Import one faction/source at a time or correct the text.");
                existing.Options.Add(option);
                continue;
            }
            if (Regex.IsMatch(line, @"\d+\s*pts?\b", RegexOptions.IgnoreCase))
            { warnings.Add($"Not imported (non-unit cost or unsupported format): {line}"); continue; }
            if (Regex.IsMatch(line, @"^(\d+|v?\d+\.\d+|VERSION .+|MUNITORUM FIELD MANUAL.*|WARHAMMER 40,?000.*|Image.*|Show Legends.*|YOUR .*)$", RegexOptions.IgnoreCase)) continue;
            // Names are the short line immediately preceding a size or cost heading.
            // Prose and attachment lists must never become unit names.
            if (line.Length < 90 && !line.Contains(',') && !line.EndsWith('.') && !line.Contains("http"))
            {
                candidate = !webStyle && !excluded && candidate != null ? candidate + " " + line : line;
                if (!webStyle) { fromCopy = 1; }
            }
        }
        var data = new Dataset(source, units.Values.OrderBy(u => u.Faction).ThenBy(u => u.Name).ToList());
        data.Validate();
        warnings.Insert(0, "Review the extracted names, sizes and points against the source. Only unit model costs are imported; wargear, enhancements and detachment costs are excluded.");
        return new(data, warnings.Distinct().ToList());
    }

    public static string ReadColumns(string layout)
    {
        var result = new StringBuilder();
        var price = new Regex(@"\d+\s+models?\s*[.·…\s]*(?:\([+-]?\d+\)\s*)?\d[\d,]*\s*pts?", RegexOptions.IgnoreCase);
        foreach (var page in layout.Split('\f'))
        {
            var lines = page.Split('\n');
            List<int> anchors = [];
            foreach (var x in lines.SelectMany(l => price.Matches(l).Select(m => m.Index)).Order())
                if (!anchors.Any(a => Math.Abs(a - x) < 7)) anchors.Add(x);
            if (anchors.Count == 0)
            {
                // Covers and contents contain no units. Retain only source version metadata.
                foreach (var line in lines.Where(l => Regex.IsMatch(l, @"\bVERSION\s+\d+", RegexOptions.IgnoreCase))) result.AppendLine(line.Trim());
                continue;
            }
            var faction = lines.Select(l => MatchFaction(l.Trim())).FirstOrDefault(f => f != null);
            if (faction != null) result.AppendLine(faction);
            for (var column = 0; column < anchors.Count; column++)
            {
                result.AppendLine("UNITS");
                var start = column == 0 ? 0 : Math.Max(0, anchors[column] - 2);
                var end = column + 1 < anchors.Count ? Math.Max(start, anchors[column + 1] - 2) : int.MaxValue;
                foreach (var line in lines)
                {
                    if (MatchFaction(line.Trim()) != null || line.Length <= start) continue;
                    var chunk = line[start..Math.Min(end, line.Length)].Trim();
                    if (chunk.Length > 0) result.AppendLine(chunk);
                }
            }
        }
        return result.ToString();
    }
    public static string GuessVersion(string text) => Regex.Match(text, @"\b(?:VERSION\s+|v)(\d+\.\d+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "Unspecified";
    public static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<(script|style|noscript)\b[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--[\s\S]*?-->", "");
        html = Regex.Replace(html, @"</?(?:div|p|h[1-6]|li|section|br|tr|article)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
    }
}

public static class OfficialSources
{
    public const string DownloadsUrl = "https://www.warhammer-community.com/en-gb/downloads/warhammer-40000/";
    public const string MfmUrl = "https://mfm.warhammer-community.com/en";
}
