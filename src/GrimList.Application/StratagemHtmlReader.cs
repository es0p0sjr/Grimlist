using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GrimList.Core;

namespace GrimList.Application;

// Deliberately bounded adapter for the published 11e faction format. Unsupported
// layouts are acquisition failures, never authoritative empty rule collections.
public static class StratagemHtmlReader
{
    public const string Version = "grimlist-html-2";
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Tokens = new("<!--.*?-->|<![^>]*>|<[^>]+>|[^<]+", RegexOptions.Singleline, Timeout);
    private static readonly Regex Attributes = new("([\\w:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.None, Timeout);
    private sealed class Node(string tag, Dictionary<string, string>? attributes = null, Node? parent = null)
    {
        public string Tag = tag;
        public Dictionary<string, string> Attributes = attributes ?? [];
        public Node? Parent = parent;
        public List<object> Children = [];
        public bool Closed;
        public bool Has(string value) => Attributes.GetValueOrDefault("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value);
        public IEnumerable<Node> Nodes() { yield return this; foreach (var child in Children.OfType<Node>()) foreach (var node in child.Nodes()) yield return node; }
        public string Text(bool skipPoints = false)
        {
            if (Tag is "script" or "style" or "noscript" || Has("faq") || Has("faqErrataStrat") || Has("faqErrataSpoiler")
                || Has("tooltip-content") || Has("tooltip_content") || Attributes.GetValueOrDefault("id", "").StartsWith("tooltip_content")
                || skipPoints && Has("dpPts")) return "";
            if (Tag == "br") return "\n";
            var text = string.Concat(Children.Select(c => c is Node n ? n.Text(skipPoints) : (string)c));
            if (Tag == "li")
            {
                var marker = "*";
                if (Parent?.Tag == "ol")
                {
                    var number = int.TryParse(Parent.Attributes.GetValueOrDefault("start"), out var start) ? start : 1;
                    foreach (var sibling in Parent.Children.OfType<Node>().Where(n => n.Tag == "li"))
                    { if (int.TryParse(sibling.Attributes.GetValueOrDefault("value"), out var value)) number = value; if (sibling == this) break; number++; }
                    marker = number + ".";
                }
                return "\n" + marker + " " + text.Trim() + "\n";
            }
            return Tag is "p" or "div" or "h1" or "h2" or "h3" or "section" or "article" or "tr" ? "\n" + text + "\n" : text;
        }
    }

    public static Dataset Parse(string html, Dataset catalogue, string faction, SourceInfo source, bool allowPartial = false)
    {
        if (catalogue.Source.Edition != "11" || source.Edition != "11" || Encoding.UTF8.GetByteCount(html) > MaximumBytes)
            throw new InvalidDataException("Unsupported edition or oversized reference page.");
        // Script markup is not document structure. It must never manufacture rule nodes.
        html = Regex.Replace(html, @"<(script|style|noscript)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, Timeout);
        var root = new Node("root"); var current = root; var depth = 0; var count = 0;
        foreach (Match token in Tokens.Matches(html))
        {
            if (++count > 250000) throw new InvalidDataException("Reference page exceeds the parsing limit.");
            var value = token.Value;
            if (value.StartsWith("<!") || value.StartsWith("<?")) continue;
            if (!value.StartsWith('<')) { current.Children.Add(WebUtility.HtmlDecode(value)); continue; }
            var end = value.StartsWith("</");
            var name = Regex.Match(value, @"^</?\s*([\w:-]+)", RegexOptions.None, Timeout).Groups[1].Value.ToLowerInvariant();
            if (name.Length == 0) throw new InvalidDataException("Malformed reference markup.");
            if (end)
            {
                var node = current;
                while (node != root && node.Tag != name) node = node.Parent!;
                if (node == root) continue;
                node.Closed = true; current = node.Parent!;
                depth = 0; for (var ancestor = current; ancestor != root; ancestor = ancestor.Parent!) depth++;
                continue;
            }
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attr in Attributes.Matches(value)) attributes[attr.Groups[1].Value] = WebUtility.HtmlDecode(
                attr.Groups[2].Success ? attr.Groups[2].Value : attr.Groups[3].Success ? attr.Groups[3].Value : attr.Groups[4].Value);
            var child = new Node(name, attributes, current); current.Children.Add(child);
            if (value.EndsWith("/>") || name is "br" or "hr" or "img" or "meta" or "link" or "input" or "wbr" or "source" or "area" or "base" or "embed" or "param" or "col") child.Closed = true;
            else { current = child; if (++depth > 256) throw new InvalidDataException("Reference markup is nested too deeply."); }
        }
        var nodes = root.Nodes().ToList();
        if (!nodes.Any(n => n.Tag == "html" && n.Closed) || !nodes.Any(n => n.Tag == "title" && n.Closed && n.Text().Contains("11th", StringComparison.OrdinalIgnoreCase))
            || ReferenceModel.Key(nodes.FirstOrDefault(n => n.Tag == "h1")?.Text() ?? "") != ReferenceModel.Key(faction))
            throw new InvalidDataException("Reference page is truncated or its faction/edition cannot be verified.");
        var definitions = (catalogue.Detachments ?? []).Where(d => ReferenceModel.Key(d.Faction) == ReferenceModel.Key(faction)).ToList();
        if (definitions.Count == 0 || definitions.GroupBy(d => ReferenceModel.Key(d.Name)).Any(g => g.Count() != 1))
            throw new InvalidDataException("Reference catalogue scopes are missing or ambiguous.");
        var found = new Dictionary<string, List<ReferenceCard>>();
        DetachmentDefinition? detachment = null; bool inSection = false;
        var sections = new HashSet<string>();
        var invalid = new Dictionary<string, string>();
        void Invalid(string name, string reason)
        {
            if (!allowPartial) throw new InvalidDataException(reason);
            invalid[name] = reason;
        }
        var ownedTitles = new HashSet<Node>();
        foreach (var node in nodes)
        {
            var heading = ReferenceModel.Clean(node.Tag is "h1" or "h2" ? node.Text() : "");
            if (heading is "Boarding Actions" or "Crusade Rules" or "Combat Patrol") break;
            if (node.Tag == "h2")
            {
                inSection = false;
                if (node.Has("outline_header"))
                {
                    detachment = null;
                    if (node.Nodes().Any(n => n.Has("dpPts")))
                    {
                        var name = ReferenceModel.Clean(node.Text(true));
                        detachment = definitions.SingleOrDefault(d => ReferenceModel.Key(d.Name) == ReferenceModel.Key(name));
                        if (detachment != null && (!node.Closed || !node.Attributes.ContainsKey("id") || !found.TryAdd(detachment.Name, [])))
                            Invalid(detachment.Name, "Duplicate or incomplete detachment heading: " + name);
                        if (detachment != null)
                        {
                            var points = node.Nodes().Where(n => n.Has("dpPts")).Select(n => ReferenceModel.Key(n.Text())).ToList();
                            if (Regex.IsMatch(ReferenceModel.Key(detachment.Cost), @"^\d+DP$", RegexOptions.None, Timeout)
                                && (points.Count != 1 || points[0] != ReferenceModel.Key(detachment.Cost)))
                                Invalid(detachment.Name, "Source detachment cost does not match the current catalogue: " + name);
                        }
                    }
                }
                if (heading is "Stratagems" or "Stratagem" && detachment != null)
                {
                    if (!node.Closed || !sections.Add(detachment.Name)) Invalid(detachment.Name, "Duplicate or incomplete stratagem section.");
                    inSection = true;
                }
            }
            if (node.Has("str11Name") && detachment != null && !ownedTitles.Contains(node))
                Invalid(detachment.Name, "Stratagem title outside a complete card in " + detachment.Name);
            if (!node.Has("str11Wrap")) continue;
            if (detachment != null && !inSection) Invalid(detachment.Name, "Stratagem outside a recognized section in " + detachment.Name);
            if (!inSection || detachment == null) continue;
            foreach (var title in node.Nodes().Where(n => n.Has("str11Name"))) ownedTitles.Add(title);
            if (invalid.ContainsKey(detachment.Name)) continue;
            try
            {
            string Field(string css)
            {
                var fields = node.Nodes().Where(n => n.Has(css)).ToList();
                if (!node.Closed || fields.Count != 1 || !fields[0].Closed) throw new InvalidDataException("Incomplete stratagem card in " + detachment.Name);
                return fields[0].Text().Trim();
            }
            var nameText = ReferenceModel.Clean(Field("str11Name"));
            var cost = ReferenceModel.Clean(Field("str11CP"));
            var type = ReferenceModel.Clean(Field("str11Type"));
            var text = Field("str11Text");
            var scope = Regex.Match(type, @"^(.+?)\s+[\u2013\u2014-]\s+.+?\bStratagem$|^(.+?)\s+Stratagem$", RegexOptions.IgnoreCase, Timeout);
            if (nameText.Length == 0 || !Regex.IsMatch(cost, @"^\d+\s*CP$", RegexOptions.IgnoreCase, Timeout)
                || !scope.Success || ReferenceModel.Key(scope.Groups[1].Success ? scope.Groups[1].Value : scope.Groups[2].Value) != ReferenceModel.Key(detachment.Name))
                throw new InvalidDataException("Unrecognized stratagem fields in " + detachment.Name);
            // Keep the operative block order, including repeated WHEN/EFFECT pairs.
            var labels = Regex.Matches(text, @"\b(WHEN|TARGET|EFFECT|RESTRICTIONS?)\s*:", RegexOptions.None, Timeout).Cast<Match>().ToList();
            var blocks = labels.Select((label, i) => (Name: label.Groups[1].Value, Value: text[(label.Index + label.Length)..(i + 1 < labels.Count ? labels[i + 1].Index : text.Length)].Trim())).ToList();
            if (!blocks.Any(b => b.Name == "EFFECT" && b.Value.Length > 0) || blocks.Any(b => b.Value.Length == 0))
                throw new InvalidDataException("Missing stratagem operative text in " + detachment.Name);
            var body = string.Join("\n", blocks.Select(b => b.Name + ": " + b.Value));
            var card = new ReferenceCard(faction, "Stratagems", nameText, body, detachment.Name, cost, source,
                Details: new(Edition: "11", SourceId: ReferenceModel.Owner(source), SourceCardId: SemanticId(faction, detachment.Name, nameText),
                    SourceType: "WahapediaHtml", Scope: "Detachment", FactionId: detachment.FactionId, Detachments: [new(detachment.Name, detachment.Id)]));
            found[detachment.Name].Add(ReferenceModel.Canonical(card, source));
            }
            catch (InvalidDataException ex) when (allowPartial) { Invalid(detachment.Name, ex.Message); }
        }
        foreach (var scope in found)
            if (scope.Value.GroupBy(c => c.Details!.SourceCardId).Any(g => g.Count() != 1)) Invalid(scope.Key, "Duplicate stratagem identity in " + scope.Key);
        var missing = definitions.Where(d => invalid.ContainsKey(d.Name) || !sections.Contains(d.Name) || !found.TryGetValue(d.Name, out var cards) || cards.Count == 0).Select(d => d.Name).ToList();
        if (!allowPartial && missing.Count > 0) throw new InvalidDataException("No verified stratagem section for: " + string.Join(", ", missing));
        var all = found.Where(s => !missing.Contains(s.Key)).SelectMany(s => s.Value).ToList();
        return catalogue with { ReferenceOnly = true, Units = [], ReferenceCards = all,
            ReferenceCoverage = definitions.Select(d => missing.Contains(d.Name)
                ? new ReferenceCoverage(ReferenceModel.Owner(source), "11", "Stratagems", faction, d.Name, "Partial", "Partial", false,
                    ReviewReason: invalid.GetValueOrDefault(d.Name, "No verified stratagem section for " + d.Name) + ". Existing references retained for review.")
                : new ReferenceCoverage(ReferenceModel.Owner(source), "11", "Stratagems", faction, d.Name,
                    "Complete", "CompleteForDetachment", true, source, ReferenceCatalogueCompatibility.Fingerprint(catalogue, faction, d.Name))).ToList() };
    }

    // Compatible with the existing Python acquisition pipeline's semantic IDs.
    public static string SemanticId(string faction, string detachment, string name)
    {
        static string Key(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit));
        var values = new[] { faction, detachment, "Stratagems", name }.Select(Key);
        var json = "[" + string.Join(", ", values.Select(v => JsonSerializer.Serialize(v, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))) + "]";
        return "semantic-" + ReferenceModel.Hash(json);
    }
}
