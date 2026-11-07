using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    public static Dataset ImportReferences(BsDataDocument document, SourceInfo source)
    {
        var registry = Walk(document.Root).Where(n => Text(n, "id").Length > 0).GroupBy(n => Text(n, "id")).ToDictionary(g => g.Key, g => g.First());
        return ReadReferences(document, registry, source);
    }
    private static Dataset ReadReferences(BsDataDocument document, Dictionary<string, JsonObject> registry, SourceInfo source, List<BsDataDocument>? roots = null, List<ArmyRuleScope>? armyRuleEvidence = null)
    {
        var faction = FactionName(document);
        roots ??= [document];
        List<ReferenceCard> cards = []; List<DetachmentDefinition> detachments = [];
        var choices = new Dictionary<string, string>();
        var nestedChoices = new HashSet<string>();
        var scopedRules = new HashSet<string>();
        var dispositions = new HashSet<string> { "Disruption", "Priority Assets", "Purge the Foe", "Take and Hold", "Reconnaissance" };
        IEnumerable<JsonObject> Values(JsonObject n) => Children(n, "selectionEntries").Concat(Children(n, "selectionEntryGroups")).Concat(Children(n, "entryLinks"))
            .Select(c => Text(c, "targetId").Length == 0 || registry.ContainsKey(Text(c, "targetId")) ? Resolve(c, registry) : c);
        var visited = new HashSet<string>();
        IEnumerable<JsonObject> ReferenceNodes(JsonObject node)
        {
            var id = Text(node, "id");
            if (id.Length > 0 && !visited.Add(id)) yield break;
            yield return node;
            foreach (var child in Values(node))
                foreach (var descendant in ReferenceNodes(child)) yield return descendant;
        }
        var nodes = roots.SelectMany(r => Walk(r.Root)).SelectMany(ReferenceNodes).ToList();
        ReferenceDetails Identity(string sourceId, string detachment = "", string detachmentId = "") => new(
            Id: "ref-" + ReferenceModel.Hash(DataJson.Write(new[] { source.Edition, Text(document.Root, "id"), sourceId, detachmentId })),
            Edition: source.Edition, SourceId: "bsdata:" + source.Edition, SourceCardId: sourceId, SourceType: "BSData",
            Scope: detachment.Length > 0 ? "Detachment" : "Faction", FactionId: Text(document.Root, "id"),
            Detachments: detachment.Length > 0 ? [new(detachment, detachmentId)] : []);
        List<(string Name, string Text, string Id, JsonObject Definition)> Texts(JsonObject node)
        {
            var items = Children(node, "rules").Select(r => (Clean(Text(r, "name")), Clean(Text(r, "description")), Text(r, "id"), r)).ToList();
            items.AddRange(Children(node, "profiles").Where(p => ReferenceModel.Key(Text(p, "typeName")) is not ("STRATAGEM" or "STRATAGEMS"))
                .Select(p => (Clean(Text(p, "name")), string.Join("\n", Children(p, "characteristics").Select(c => Clean(Text(c, "$text")))), Text(p, "id"), p)));
            foreach (var link in Children(node, "infoLinks"))
                if (registry.TryGetValue(Text(link, "targetId"), out var target))
                {
                    if (ReferenceModel.Key(Text(target, "typeName")) is "STRATAGEM" or "STRATAGEMS") continue;
                    var text = Text(link, "type") == "rule" ? Clean(Text(target, "description")) : string.Join("\n", Children(target, "characteristics").Select(c => Clean(Text(c, "$text"))));
                    items.Add((Clean(Text(target, "name")), text, Text(target, "id"), Resolve(link, registry)));
                }
            return items.Where(t => t.Item2.Length > 0).DistinctBy(t => (t.Item1, t.Item2, t.Item3)).ToList();
        }
        foreach (var branch in ReadDetachmentTree(nodes, registry).Choices)
            {
                var node = branch.Entry;
                var name = Clean(Text(node, "name")); var id = Text(node, "sourceLinkId") is { Length: > 0 } linkId ? linkId : Text(node, "id");
                if (!choices.TryAdd(id, name)) continue;
                if (branch.Ancestors.Any(a => !DetachmentContainer(a))) nestedChoices.Add(name);
                choices.TryAdd(Text(node, "id"), name);
                var categories = Children(node, "categoryLinks").Select(c => Text(c, "name") is { Length: > 0 } label ? label
                    : registry.TryGetValue(Text(c, "targetId"), out var category) ? Text(category, "name") : "").Where(n => n.Length > 0).Distinct().ToList();
                var available = categories.Where(dispositions.Contains).ToList();
                var points = Children(node, "costs").FirstOrDefault(c => Text(c, "name").Equals("Detachment Points", StringComparison.OrdinalIgnoreCase)
                    || registry.TryGetValue(Text(c, "typeId"), out var costType) && Text(costType, "name").Equals("Detachment Points", StringComparison.OrdinalIgnoreCase));
                var cost = points == null ? "" : Number(points, "value").ToString(System.Globalization.CultureInfo.InvariantCulture) + " DP";
                detachments.Add(DetachmentRules.Interpret(new(faction, name, available.FirstOrDefault() ?? "", cost, available, source,
                    Id: id, FactionId: Text(document.Root, "id"), Tags: categories.Except(available).ToList(),
                    Applicability: new("and", Children: branch.Ancestors.Append(node).Select(g => DetachmentVisibility(g, registry))
                        .Concat(branch.Unresolved ? [new EffectCondition("unknown", Label: "Unsupported detachment group constraint")] : []).ToList()),
                    GroupLimits: branch.Limits.Count > 0 ? branch.Limits : null), string.Join("\n", Texts(node).Select(t => t.Text))));
                var detachmentTexts = Texts(node);
                foreach (var rule in detachmentTexts)
                {
                    var effects = ReadEnhancementModifiers(rule.Definition, registry, rule.Text, rosterRule: true);
                    if (rule == detachmentTexts[0]) effects.AddRange(ReadEnhancementModifiers(node, registry, "", rosterRule: true));
                    var visibility = branch.Ancestors.Append(node).Append(rule.Definition)
                        .Select(g => EffectVisibility(g, registry, Text(node, "id"))).Aggregate(Both);
                    effects = effects.Select(m => m with { Condition = m.Condition == null ? visibility : Both(visibility, m.Condition) }).ToList();
                    cards.Add(new(faction, "Detachments", rule.Name, rule.Text, name, cost, source, Details: Identity(rule.Id, name, id), Modifiers: effects));
                }
                foreach (var link in Children(node, "infoLinks"))
                    if (Text(link, "targetId") is { Length: > 0 } target) scopedRules.Add(target);
                foreach (var rule in Children(node, "rules"))
                    if (Text(rule, "id") is { Length: > 0 } ruleId) scopedRules.Add(ruleId);
            }
        var armyRules = ReadArmyRuleScope(document, roots, registry, source, scopedRules);
        cards.AddRange(armyRules.Cards);
        armyRuleEvidence?.Add(armyRules);
        IEnumerable<JsonObject> Conditions(JsonObject node) => Children(node, "conditions").Concat(Children(node, "conditionGroups").SelectMany(Conditions));
        // Preserve for attachment work: selectable cards need all three independent
        // metadata fields below. IsUpgrade controls classification, Eligibility
        // controls permitted bearers, and Modifiers controls selected effects.
        // Prose alone does not always contain the source's bearer restrictions.
        // Coverage: SourceEnhancementChecks, UpgradePricingChecks, UpgradeRecoveryChecks.
        foreach (var group in nodes.Where(EnhancementGroup))
            foreach (var node in Values(group).Where(n => Text(n, "type") == "upgrade"))
            {
                var scopes = Modifiers(node).Concat(Modifiers(group)).Where(m => Text(m, "field") == "hidden").SelectMany(Conditions)
                    .Where(c => choices.ContainsKey(Text(c, "childId"))).Select(c => choices[Text(c, "childId")]).Distinct().ToList();
                var name = Clean(Text(node, "name"));
                // The selectable entry owns the enhancement identity. Linked keyword
                // explanations belong inside its card, never as separately priced choices.
                var texts = Texts(node).OrderByDescending(t => UnitIdentity.Normalize(t.Name) == UnitIdentity.Normalize(name)).ToList();
                if (texts.Count == 0) continue;
                // This enhancement grants an additional bearer weapon. Other weapon
                // enhancements may replace equipment or have conditional profiles;
                // do not infer those semantics from a profile's presence alone.
                var weaponGrant = name == "Imperium's Sword" && scopes.Contains("Assault Brethren");
                var weapons = texts.Where(t => weaponGrant && Text(t.Definition, "typeName") == "Melee Weapons")
                    .Where(t => !Flag(t.Definition, "hidden")).Select(t => ReadEnhancementWeapon(t.Definition)).ToList();
                if (weapons.Count > 0) texts.RemoveAll(t => Text(t.Definition, "typeName") == "Melee Weapons" && !Flag(t.Definition, "hidden"));
                if (texts.Count == 0 && weapons.Count == 0) continue;
                var description = string.Join("\n\n", texts.Select((t, i) => i == 0 ? t.Text : t.Name + ":\n" + t.Text));
                if (description.Length == 0) description = EnhancementWeapons.GrantText(weapons);
                var cost = Children(node, "costs").FirstOrDefault(c => Text(c, "name") == "pts");
                foreach (var scope in scopes)
                    cards.Add(new(faction, "Enhancements", name, description, scope, cost == null ? "" : Text(cost, "value") + " pts", source, IsUpgrade: ReadUpgrade(node, registry),
                        Details: Identity(Text(node, "id"), scope),
                        Eligibility: ReadEnhancementEligibility(group, node, registry, choices, document.Id), Modifiers: ReadEnhancementModifiers(node, registry, description),
                        Weapons: weapons.Count == 0 ? null : weapons));
            }
        foreach (var disposition in detachments.SelectMany(d => d.DispositionOptions.Select(option => (Option: option, Detachment: d))).GroupBy(d => d.Option))
            cards.Add(new(faction, "Dispositions", disposition.Key, "Available from:\n" + string.Join("\n", disposition.Select(d => $"• {d.Detachment.Name} ({d.Detachment.Cost})")), Source: source));
        // Stratagems have a separate hierarchy/visibility reader and retain their
        // source identity and detachment scope. Preserve this call and its cards
        // when changing unit attachment parsing; see StratagemTransferChecks.
        cards.AddRange(ReadStratagemReferences(document, registry, source, choices));
        // A subgroup's visibility applies to its rules and selectable enhancements
        // as well as the detachment picker, including saved unavailable selections.
        cards = cards.Select(c => nestedChoices.Contains(c.Detachment)
            ? c with { Applicability = new("and", Children: detachments.Where(d => d.Name == c.Detachment)
                .Select(d => d.Applicability).Append(c.Applicability).OfType<EffectCondition>().ToList()) }
            : c).ToList();
        // Multiple source links to the same definition are alternative selection paths.
        // Union their explicit conditions, retaining conflicts in content/classification
        // as separate evidence rather than silently taking the first same-named entry.
        var enhancements = cards.Where(c => c.Section == "Enhancements")
            .GroupBy(c => (c.Detachment, c.Details!.SourceCardId, c.Name, c.Text, c.Cost, c.IsUpgrade, Weapons: DataJson.Write(c.Weapons)))
            .Select(g =>
            {
                var paths = g.Select(c => c.Eligibility).DistinctBy(c => DataJson.Write(c)).ToList();
                return g.First() with { Eligibility = paths.Contains(null) ? null : paths.Count == 1 ? paths[0]
                    : new("or", Children: paths.Select(c => c!).ToList()) };
            });
        return new(source, [], Detachments: detachments, ReferenceCards: cards.Where(c => c.Section != "Enhancements")
            .DistinctBy(c => (c.Section, c.Detachment, c.Name)).Concat(enhancements).ToList(), ReferenceOnly: true,
            FactionContexts: [new(faction, document.Id, Keywords(document.Root, registry))]);
    }

    private static WeaponProfile ReadEnhancementWeapon(JsonObject definition)
    {
        var node = BaseValues(definition, []);
        var values = Children(node, "characteristics").GroupBy(c => Text(c, "name"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Clean(Text(g.Last(), "$text")), StringComparer.OrdinalIgnoreCase);
        string Value(params string[] names) => names.Select(n => values.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "—";
        return new(Clean(Text(node, "name")), Text(node, "typeName") == "Melee Weapons" ? "Melee" : "Ranged",
            Value("Range"), Value("A", "Attacks"), Value("BS", "WS", "Skill"), Value("S", "Strength"), Value("AP"), Value("D", "Damage"),
            values.GetValueOrDefault("Keywords") ?? "", Text(definition, "id"));
    }
}
