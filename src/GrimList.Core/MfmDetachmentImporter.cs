using System.Text.RegularExpressions;

namespace GrimList.Core;

// Imports the MFM detachment table independently of unit prices and datasheet rules.
public static class MfmDetachmentImporter
{
    private static readonly string[] Names = ["Disruption", "Priority Assets", "Purge the Foe", "Take and Hold", "Reconnaissance"];
    public static Dataset ParseText(string text, string faction, SourceInfo source)
    {
        text = text.Replace('\r', '\n').Replace('\u00a0', ' ');
        var section = Regex.Match(text, @"(?im)^\s*\#*\s*DETACHMENTS\s*$");
        if (section.Success) text = text[(section.Index + section.Length)..];
        var headings = Regex.Matches(text, @"(?im)^\s*\#*\s*(?<name>[^\r\n]+?)\s+(?<cost>\d+)\s*DP\s*$");
        if (headings.Count == 0) throw new InvalidDataException("No MFM detachment names and DP costs were found.");
        List<DetachmentDefinition> detachments = [];
        for (var i = 0; i < headings.Count; i++)
        {
            var heading = headings[i]; var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            var body = text[(heading.Index + heading.Length)..end];
            var stop = Regex.Match(body, @"(?im)^\s*\#*\s*(?:ENHANCEMENTS\b|UNIQUE:)");
            if (stop.Success) body = body[..stop.Index];
            body = Regex.Replace(body, @"\s+", " ").Trim();
            var choices = Regex.Matches(body, string.Join("|", Names.Select(Regex.Escape)), RegexOptions.IgnoreCase)
                .Select(m => Names.Single(n => n.Equals(m.Value, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            var remaining = Regex.Replace(body, string.Join("|", Names.Select(Regex.Escape)), "", RegexOptions.IgnoreCase);
            remaining = Regex.Replace(remaining, @"\b(?:FORCE|DISPOSITIONS?|OR|AND)\b|[\s/:,;|*•-]", "", RegexOptions.IgnoreCase);
            if (choices.Count == 0 || remaining.Length > 0)
                throw new InvalidDataException($"{heading.Groups["name"].Value}: disposition text is missing or unrecognised. Nothing imported.");
            detachments.Add(new(faction, heading.Groups["name"].Value.Trim(), choices[0], heading.Groups["cost"].Value + " DP", choices, source));
        }
        if (detachments.DistinctBy(d => UnitIdentity.Normalize(d.Name)).Count() != detachments.Count)
            throw new InvalidDataException("Duplicate MFM detachment entries. Import one faction at a time.");
        var cards = detachments.SelectMany(d => d.DispositionOptions.Select(option => (Option: option, Detachment: d))).GroupBy(d => d.Option)
            .Select(g => new ReferenceCard(faction, "Dispositions", g.Key,
                "Available from:\n" + string.Join("\n", g.Select(d => $"• {d.Detachment.Name} ({d.Detachment.Cost})")), Source: source)).ToList();
        var result = new Dataset(source, [], Detachments: detachments, ReferenceCards: cards, ReferenceOnly: true);
        result.Validate(); return result;
    }
}
