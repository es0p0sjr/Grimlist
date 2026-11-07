using System.Text.Json;
using System.Text.RegularExpressions;
using GrimList.Core;

namespace GrimList.Application;

// Adapter for the scraper's deterministic YAML output, not a general YAML loader.
// Only the unit pricing section is consumed. Unknown price shapes fail closed;
// descriptions/add-ons require composition mapping and are explicitly skipped.
public static class MfmMirrorParser
{
    public static ImportPreview Parse(string yaml, SourceInfo snapshot)
    {
        var lines = yaml.Replace("\r", "").Split('\n');
        string Header(string key)
        {
            var values = lines.Where(l => l.StartsWith(key + ": ", StringComparison.Ordinal)).ToList();
            if (values.Count != 1) throw new InvalidDataException($"MFM mirror: missing or duplicate {key}.");
            return Scalar(values[0][(key.Length + 2)..]);
        }
        var faction = Header("name"); var slug = Header("slug"); var version = Header("version"); var date = Header("firstSeen");
        if (!Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$") || !DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
            throw new InvalidDataException("MFM mirror: invalid faction metadata.");
        var source = snapshot with { Version = $"MFM {version} · {snapshot.Version}", Date = date,
            Url = snapshot.Url.TrimEnd('/') + "/data/" + slug + ".yaml" };
        var start = Array.IndexOf(lines, "units:");
        if (start < 0) throw new InvalidDataException($"{faction}: MFM mirror has no units section.");
        List<(UnitDefinition Unit, string Group)> records = []; List<string> warnings = [];
        for (var i = start + 1; i < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].TrimStart().StartsWith('#')) { i++; continue; }
            if (!lines[i].StartsWith(' ')) throw new InvalidDataException($"{faction}: unexpected content in MFM units section.");
            if (!lines[i].StartsWith("  - name: ", StringComparison.Ordinal)) throw new InvalidDataException($"{faction}: unrecognized MFM unit header.");
            var name = Scalar(lines[i][10..]); var end = i + 1;
            while (end < lines.Length && (lines[end].StartsWith("    ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(lines[end]))) end++;
            var block = lines[(i + 1)..end]; i = end;
            if (block.Contains("    legends: true")) continue;
            var group = block.FirstOrDefault(l => l.StartsWith("    groupTitle: ", StringComparison.Ordinal));
            var pricing = Array.IndexOf(block, "    pricing:");
            if (pricing < 0 || block.Count(l => l == "    pricing:") != 1) throw new InvalidDataException($"{name}: missing or duplicate MFM pricing.");
            var priceEnd = pricing + 1;
            while (priceEnd < block.Length && (block[priceEnd].StartsWith("      ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(block[priceEnd]))) priceEnd++;
            var priceLines = block[(pricing + 1)..priceEnd].Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            // These are valid scraper records, but cannot safely become simple size prices.
            if (priceLines.Any(l => Regex.IsMatch(l, @"\b(?:addon|desc):")))
            { warnings.Add($"{faction} / {name}: MFM has composition descriptions or add-ons; catalogue pricing retained."); continue; }
            List<PointOption> options = []; var expectedStart = 1; var unbounded = false;
            for (var p = 0; p < priceLines.Length;)
            {
                var line = priceLines[p++];
                if (!line.StartsWith("      - range: ", StringComparison.Ordinal)) throw new InvalidDataException($"{name}: unrecognized MFM tier.");
                var range = Regex.Match(Scalar(line[15..]), @"^\[(\d+),(?:(\d+)\]|\))$");
                if (!range.Success || unbounded || !int.TryParse(range.Groups[1].Value, out var from) || from != expectedStart)
                    throw new InvalidDataException($"{name}: MFM copy tiers overlap or have gaps.");
                if (range.Groups[2].Success)
                {
                    if (!int.TryParse(range.Groups[2].Value, out var last) || last < from || last == int.MaxValue)
                        throw new InvalidDataException($"{name}: invalid MFM copy range.");
                    expectedStart = last + 1;
                }
                else unbounded = true;
                if (p >= priceLines.Length || !priceLines[p++].StartsWith("        label: ", StringComparison.Ordinal)
                    || p >= priceLines.Length || priceLines[p++] != "        costs:")
                    throw new InvalidDataException($"{name}: unrecognized MFM tier fields.");
                var before = options.Count;
                while (p < priceLines.Length && !priceLines[p].StartsWith("      - range: ", StringComparison.Ordinal))
                {
                    var cost = Regex.Match(priceLines[p++], @"^          - \{ models: (\d+), points: (\d+) \}$");
                    if (!cost.Success || !int.TryParse(cost.Groups[1].Value, out var models) || models < 1
                        || !int.TryParse(cost.Groups[2].Value, out var points))
                        throw new InvalidDataException($"{name}: unrecognized MFM cost row; nothing applied.");
                    options.Add(new(models, points, from));
                }
                if (options.Count == before) throw new InvalidDataException($"{name}: empty MFM tier.");
            }
            if (!unbounded) throw new InvalidDataException($"{name}: incomplete MFM copy tiers.");
            var sizes = options.Where(o => o.FromCopy == 1).Select(o => o.Models).ToHashSet();
            if (options.GroupBy(o => o.FromCopy).Any(g => !sizes.SetEquals(g.Select(o => o.Models))))
                throw new InvalidDataException($"{name}: MFM sizes differ between tiers.");
            records.Add((new(faction, name, options, PointsSource: source), group == null ? "" : Scalar(group[16..])));
        }
        List<UnitDefinition> units = [];
        foreach (var group in records.GroupBy(r => UnitIdentity.Normalize(r.Unit.Name)))
        {
            if (group.Count() == 1) { units.Add(group.Single().Unit); continue; }
            // The same datasheet may have separate native and allied prices. Only
            // the ungrouped native record can replace base prices; allied costs
            // retain their structured catalogue conditions and source values.
            var native = group.Where(r => r.Group.Length == 0).ToList();
            if (native.Count == 1) units.Add(native[0].Unit);
            warnings.Add($"{faction} / {group.First().Unit.Name}: multiple MFM contexts; {(native.Count == 1 ? "using native unit prices only" : "ambiguous prices retained from catalogue")}.");
        }
        var data = new Dataset(source, units); data.Validate();
        return new(data, warnings);
    }

    private static string Scalar(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("Empty MFM scalar."); }
            catch (JsonException ex) { throw new InvalidDataException("Unsupported quoted MFM scalar.", ex); }
        }
        if (value.StartsWith('\''))
        {
            if (!value.EndsWith('\'')) throw new InvalidDataException("Unclosed MFM scalar.");
            return value[1..^1].Replace("''", "'");
        }
        if (value.Length == 0 || "&*!>|[{".Contains(value[0]) || value.Contains(" #"))
            throw new InvalidDataException("Unsupported MFM scalar.");
        return value;
    }
}
