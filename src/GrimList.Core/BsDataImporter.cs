using System.Globalization;
using System.Text.Json.Nodes;
using static GrimList.Core.BsDataDocument;

namespace GrimList.Core;

public static partial class BsDataImporter
{
    public static ImportPreview Import(IReadOnlyList<BsDataDocument> documents, SourceInfo source)
    {
        if (source.Edition is not ("10" or "11")) throw new InvalidDataException("Select BSData edition 10 or 11 explicitly.");
        var primary = documents.SingleOrDefault(d => d.Name == "Chaos - Death Guard")
            ?? throw new InvalidDataException("No Chaos - Death Guard catalogue found. Choose the BSData repository ZIP or its Death Guard catalogue.");
        return ImportCatalogue(documents, primary, source);
    }

    private static ImportPreview ImportCatalogue(IReadOnlyList<BsDataDocument> documents, BsDataDocument primary, SourceInfo source, CancellationToken cancellation = default)
    {
        var faction = FactionName(primary);
        var registry = new Dictionary<string, JsonObject>();
        var loaded = new HashSet<string>();
        void Include(BsDataDocument document)
        {
            if (!loaded.Add(document.Id)) return;
            foreach (var node in Walk(document.Root))
                if (Text(node, "id") is { Length: > 0 } id) registry.TryAdd(id, node);
            foreach (var link in Children(document.Root, "catalogueLinks"))
            {
                var dependency = documents.FirstOrDefault(d => d.Id == Text(link, "targetId"));
                if (dependency != null) Include(dependency);
            }
        }
        Include(primary);
        var game = documents.FirstOrDefault(d => d.Id == Text(primary.Root, "gameSystemId"));
        if (game != null) Include(game);
        var warnings = new List<string> { $"{faction}: community-maintained BSData. Imports native units and explicitly linked entries; Legends are excluded. Conditional equipment prices and full army legality need source review." };
        var units = new List<UnitDefinition>();
        var roots = NativeCatalogues(documents, primary).ToList();
        var seen = new HashSet<string>();
        var names = new Dictionary<string, List<int>>();
        foreach (var catalogue in roots)
        {
            var candidates = new List<(JsonObject Node, string Id, string Name, bool Hidden)>();
            foreach (var candidate in Children(catalogue.Root, "selectionEntries").Concat(Children(catalogue.Root, "entryLinks")))
            {
                cancellation.ThrowIfCancellationRequested();
                JsonObject node;
                try { node = Resolve(candidate, registry); }
                catch (InvalidDataException) { throw new InvalidDataException($"Missing selectable entry '{Text(candidate, "name")}'. Import the repository ZIP to include shared catalogues."); }
                if (Text(node, "type") is not ("unit" or "model")) continue;
                var id = Text(candidate, "targetId"); if (id.Length == 0) id = Text(node, "id");
                if (!seen.Add(id)) continue;
                var displayWarnings = new HashSet<string>();
                var display = BaseValues(node, displayWarnings);
                var name = Text(display, "name");
                if (name.Contains("Legends", StringComparison.OrdinalIgnoreCase)) continue;
                warnings.AddRange(displayWarnings.Select(w => $"{faction} / {name}: {w}"));
                // Keep the original node for profile/pricing readers, which apply their own
                // modifiers. Passing the display clone would apply append modifiers twice.
                candidates.Add((node, id, name, Flag(display, "hidden")));
            }
            foreach (var group in candidates.GroupBy(c => UnitIdentity.Normalize(c.Name)))
            {
                var visible = group.Where(c => !c.Hidden).ToList();
                // Chapters precede their parents. A hidden-only chapter group still blocks
                // the inherited name, but does not block a visible peer in its own catalogue.
                if (names.TryGetValue(group.Key, out var previous))
                {
                    if (previous.Count == 1 && visible.Count == 1)
                    {
                        var index = previous[0];
                        var alias = $"bsdata:{source.Edition}:{primary.Id}:{visible[0].Id}";
                        units[index] = units[index] with { Aliases = units[index].Aliases!.Append(alias).Distinct().ToList() };
                    }
                    warnings.Add($"{faction} / {group.First().Name}: chapter or native catalogue entry takes precedence over the same-name inherited entry from {catalogue.Name}.");
                    continue;
                }
                var indices = new List<int>(); names.Add(group.Key, indices);
                if (visible.Count > 1)
                    warnings.Add($"{faction} / {group.First().Name}: the catalogue contains {visible.Count} same-name variants. Kept each separately, labelled by its source entry ID; review their points, equipment and eligibility before choosing.");
                foreach (var (node, id, sourceName, _) in visible)
                {
                    cancellation.ThrowIfCancellationRequested();
                    // Source IDs keep variant labels stable across ordering and price updates.
                    var name = visible.Count > 1 ? $"{sourceName} (variant {id})" : sourceName;
                    var keywords = Keywords(node, registry);
                    var profile = ReadDatasheet(node, registry, keywords, out var profileWarnings);
                    List<PointOption> options = []; List<PointOption>? alliedOptions = null;
                    List<DetachmentUnitCost> detachmentCosts = []; string copyCategory = "";
                    List<EquipmentCost> modelCosts = []; UnitPriceFormula? formula = null;
                    List<EquipmentCost>? equipmentCosts = null; string warning = "", alliedWarning = "";
                    List<ImportDiagnostic> diagnostics = [];
                    try
                    {
                        equipmentCosts = EquipmentPrices(node, registry, profile?.Loadout);
                        detachmentCosts = DetachmentPrices(node, registry);
                        copyCategory = CopyCategory(node);
                        modelCosts = ModelPrices(node, registry, profile?.Loadout);
                        options = ReadModelPrices(node, registry, equipmentCosts, modelCosts, profile, primary.Id, false, out formula);
                    }
                    catch (InvalidDataException ex)
                    {
                        try
                        {
                            if (profile == null || ex.Message is not ("Conditional model limits need manual review." or "Unsupported unit composition."
                                or "Conditional model minimum needs source review." or "Model bundles require an explicit composition choice." or "Unsupported copy-count condition." or "Unsupported local selection query.")) throw;
                            var recursive = RecursiveLoadout(node, registry);
                            profile = profile with { Loadout = recursive };
                            equipmentCosts = EquipmentPrices(node, registry, recursive);
                            modelCosts = ModelPrices(node, registry, recursive);
                            options = ReadModelPrices(node, registry, equipmentCosts, modelCosts, profile, primary.Id, false, out formula);
                        }
                        catch (InvalidDataException problem)
                        {
                            warning = "Points unavailable: " + problem.Message; warnings.Add(name + ": " + warning);
                            diagnostics.Add(ImportDiagnostics.From(problem, source) ?? new("UnsupportedFeature", primary.Name, name, "", "", "pricing", problem.Message, source.Version, source.ContentHash));
                        }
                    }
                    // Chapter taxes are evaluated in each native chapter catalogue.
                    // Space Marines have no supported permission to join another army.
                    if (!AlliedRules.IsMarineFaction(faction) && Modifiers(node).Any(m => ContainsCatalogueCondition(m)))
                    {
                        try { alliedOptions = ReadModelPrices(node, registry, equipmentCosts, modelCosts, profile, primary.Id, true, out _); }
                        catch (InvalidDataException ex) { alliedOptions = []; alliedWarning = "Allied points unavailable: " + ex.Message; warnings.Add(name + ": " + alliedWarning); }
                    }
                    var manualCosts = ManualEquipmentPrices(node, registry, profile);
                    equipmentCosts = (equipmentCosts ?? []).Where(c => !c.OptionId.StartsWith("manual:")).Concat(manualCosts).ToList();
                    warnings.AddRange(profileWarnings.Select(w => name + ": " + w));
                    indices.Add(units.Count);
                    units.Add(new(faction, name, options, Category: UnitCategories.FromKeywords(keywords), Profile: profile,
                        Aliases: [$"bsdata:{source.Edition}:{primary.Id}:{id}"], PointsSource: source, ProfileSource: profile == null ? null : source, PointsWarning: warning, WargearCosts: equipmentCosts,
                        AlliedOptions: alliedOptions, AlliedPointsWarning: alliedWarning, DetachmentCosts: detachmentCosts, CopyCategory: copyCategory, ModelCosts: modelCosts, PriceFormula: formula, Diagnostics: diagnostics));
                }
            }
        }
        if (units.Count == 0 || units.All(u => u.Options.Count == 0 && u.Profile == null))
            throw new InvalidDataException($"No supported {faction} points or datasheets found. " + string.Join(" ", warnings.Skip(1).Take(3)));
        var references = ReadReferences(primary, registry, source, roots);
        if (references.ReferenceCards?.Any(c => c.Section == "Enhancements" && !Enhancements.HasKnownRestriction(c)) == true)
            warnings.Add($"{faction}: some BSData enhancements have no readable bearer restrictions. They remain reference-only until eligibility is available; existing complete cards are preserved during an update.");
        var data = new Dataset(source, units, Detachments: references.Detachments, ReferenceCards: references.ReferenceCards, FactionContexts: references.FactionContexts); data.Validate();
        if (data.Detachments is { Count: > 0 } && !(data.ReferenceCards ?? []).Any(c => c.Section == "Stratagems"))
            warnings.Add($"{faction}: this BSData catalogue supplies no detachment stratagem cards. Import a matching-edition rules reference to add their text.");
        return new(data, warnings);
    }

    private static IEnumerable<JsonObject> Walk(JsonObject node)
    {
        yield return node;
        foreach (var key in new[] { "selectionEntries", "sharedSelectionEntries", "selectionEntryGroups", "sharedSelectionEntryGroups",
            "entryLinks", "profiles", "sharedProfiles", "rules", "sharedRules", "categoryEntries", "infoGroups", "sharedInfoGroups", "forceEntries", "costTypes" })
            foreach (var child in Children(node, key)) foreach (var descendant in Walk(child)) yield return descendant;
    }
    private static JsonObject Resolve(JsonObject node, Dictionary<string, JsonObject> registry)
    {
        var target = Text(node, "targetId");
        if (target.Length == 0) return node;
        if (!registry.TryGetValue(target, out var definition)) throw new InvalidDataException("Shared entry is missing; choose the repository ZIP.");
        var merged = (JsonObject)definition.DeepClone();
        merged["sourceLinkId"] = Text(node, "id");
        merged["sourceTargetId"] = target;
        // Link type describes the link, not whether its target is a model or unit.
        foreach (var key in new[] { "name", "hidden", "defaultSelectionEntryId" }) if (node[key] != null) merged[key] = node[key]!.DeepClone();
        // Link-local costs override the same cost type, while retaining unrelated target costs.
        if (Children(node, "costs").Count > 0)
            merged["costs"] = new JsonArray(Children(definition, "costs").Concat(Children(node, "costs"))
                .GroupBy(c => Text(c, "typeId") is { Length: > 0 } id ? id : Text(c, "name"), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last().DeepClone()).ToArray());
        foreach (var key in new[] { "constraints", "modifiers", "modifierGroups", "selectionEntries", "selectionEntryGroups", "entryLinks", "profiles", "rules", "infoLinks", "infoGroups", "categoryLinks" })
            if (Children(node, key).Count > 0) merged[key] = new JsonArray(Children(definition, key).Concat(Children(node, key)).Select(n => n.DeepClone()).ToArray());
        return merged;
    }
    // Links can add finite nested links to the same shared definition (e.g. the
    // Chaos Cerastus Warlord links). Only an identical resolved ancestor is a cycle;
    // the target ID alone loses the link's local children and overrides.
    private sealed class SelectionPath
    {
        private readonly Dictionary<string, List<JsonObject>> ancestors = [];
        public bool Enter(JsonObject node)
        {
            var id = Text(node, "id");
            if (!ancestors.TryGetValue(id, out var nodes)) ancestors[id] = nodes = [];
            if (nodes.Any(previous => JsonNode.DeepEquals(previous, node))) return false;
            nodes.Add(node); return true;
        }
        public void Exit(JsonObject node)
        {
            var id = Text(node, "id"); var nodes = ancestors[id];
            nodes.RemoveAt(nodes.Count - 1);
            if (nodes.Count == 0) ancestors.Remove(id);
        }
    }
    private static int Number(JsonObject node, string key)
    {
        if (!decimal.TryParse(Text(node, key), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value != decimal.Truncate(value) || value < 0 || value > 100000)
            throw new InvalidDataException($"Unsupported numeric {key}.");
        return (int)value;
    }
    private static IEnumerable<JsonObject> Modifiers(JsonObject node)
    {
        foreach (var modifier in Children(node, "modifiers")) yield return modifier;
        foreach (var group in Children(node, "modifierGroups"))
            foreach (var modifier in Modifiers(group))
            {
                var copy = (JsonObject)modifier.DeepClone();
                if (Children(group, "repeats").Count > 0 && Children(copy, "repeats").Count == 0)
                    copy["repeats"] = group["repeats"]!.DeepClone();
                else if (Children(group, "repeats").Count > 0) copy["unsupportedParentCondition"] = true;
                if (Text(group, "affects").Length > 0)
                    copy["unsupportedParentCondition"] = true;
                if (Children(group, "conditions").Count + Children(group, "conditionGroups").Count + Children(group, "localConditionGroups").Count > 0)
                {
                    var conditions = new JsonObject { ["type"] = Text(group, "type") is { Length: > 0 } type ? type : "and" };
                    foreach (var key in new[] { "conditions", "conditionGroups", "localConditionGroups" })
                        if (group[key] != null) conditions[key] = group[key]!.DeepClone();
                    var nested = copy["conditionGroups"] as JsonArray ?? new JsonArray();
                    if (copy["conditionGroups"] == null) copy["conditionGroups"] = nested;
                    nested.Add(conditions);
                }
                yield return copy;
            }
    }
    private static bool ContainsCatalogueCondition(JsonObject node) => Text(node, "scope") == "primary-catalogue"
        || new[] { "conditions", "conditionGroups", "localConditionGroups" }.SelectMany(k => Children(node, k)).Any(ContainsCatalogueCondition);
    private static List<PointOption> Prices(JsonObject node, Dictionary<string, JsonObject> registry, List<EquipmentCost>? equipmentCosts = null, string catalogueId = "", bool allied = false, List<EquipmentCost>? modelCosts = null, LoadoutDefinition? loadout = null)
    {
        var descendants = Descendants(node, registry, new SelectionPath()).ToList();
        JsonObject? PointsCost(JsonObject entry) => Children(entry, "costs").SingleOrDefault(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase));
        var cost = PointsCost(node);
        // A sole model role has an unambiguous per-model cost, including group wrappers.
        var modelEntries = descendants.Where(d => Text(d, "type") == "model").ToList();
        var pricedModel = Text(node, "type") == "unit" && modelEntries.Count == 1 ? modelEntries[0] : null;
        var modelCost = pricedModel == null ? null : PointsCost(pricedModel);
        var priceField = cost ?? modelCost ?? throw new InvalidDataException("No base unit or model cost.");
        var field = Text(priceField, "typeId"); var initial = cost == null ? 0 : Number(cost, "value");
        if (modelCost != null && Text(modelCost, "typeId") != field) throw new InvalidDataException("Inconsistent model points field.");
        var perModel = modelCost == null ? 0 : Number(modelCost, "value");
        var maximums = CompositionMaximums(node, registry, loadout);
        var size = Text(node, "type") == "model" ? (Min: 1, Max: 1) : Bounds(node, registry, new SelectionPath(), true, maximums: maximums);
        if (size.Min < 1 || size.Max < size.Min || size.Max > 100) throw new InvalidDataException("Unsupported unit composition.");
        var copyBreaks = new SortedSet<int> { 1 };
        var counts = ModelCounts(node, registry);
        var copyCategory = CopyCategory(node);
        var copyIds = Children(node, "categoryLinks").Where(c => Text(c, "name") == copyCategory)
            .Select(c => Text(c, "targetId")).Append(Text(node, "id")).ToHashSet();
        var rules = NumericModifiers(node, field, Text(node, "id"), registry, copyIds, copyBreaks);
        int Query(SelectionQuery query, int models, int copy)
        {
            if (query.Field == "copy-before") return copy - 1;
            if (query.Scope == "primary-catalogue")
            {
                if (allied && query.ChildId != catalogueId) throw new InvalidDataException("Allied price depends on a specific host catalogue.");
                return !allied && query.ChildId == catalogueId ? 1 : 0;
            }
            if (query.Scope != Text(node, "id") && query.Scope is not ("self" or "unit" or "unit-self"))
                throw new InvalidDataException("Unrecognized price condition.");
            var count = query.ChildId == "model" ? models : counts.TryGetValue(query.ChildId, out var selector) ? selector(models) : (int?)null;
            return count ?? throw new InvalidDataException("Unrecognized price condition.");
        }
        // Never silently treat paid equipment or conditional model prices as free.
        var coveredEquipment = new HashSet<string>();
        coveredEquipment.UnionWith(DetachmentPrices(node, registry).Select(c => c.SourceId));
        coveredEquipment.UnionWith((modelCosts ?? []).Select(c => c.OptionId));
        foreach (var entry in descendants.Where(d => equipmentCosts?.Any(c => c.OptionId == Text(d, "id") && c.Points != null) == true))
            foreach (var item in EquipmentBundle(entry, registry, new SelectionPath())) coveredEquipment.Add(Text(item.Node, "id"));
        foreach (var descendant in descendants)
        {
            if (coveredEquipment.Contains(Text(descendant, "id")) || equipmentCosts?.Any(c => c.OptionId == Text(descendant, "id")) == true) continue;
            if ((descendant != pricedModel && Children(descendant, "costs").Any(c => Text(c, "name").Equals("pts", StringComparison.OrdinalIgnoreCase) && Number(c, "value") != 0))
                || Modifiers(descendant).Any(m => Text(m, "field") == field && !ImpossibleChildCopy(m, descendant, node, registry)))
                throw new InvalidDataException("Paid models or equipment need a loadout-aware importer.");
        }
        int Price(int models, int copy)
        {
            var price = RuleEvaluation.Effective(initial, rules, query => Query(query, models, copy));
            if (price < 0) throw new InvalidDataException("Negative calculated price.");
            return checked(price + models * perModel);
        }
        // Keep the largest model count in each equal-price band (e.g. 5/7/10 Marines).
        var legalSizes = CompositionSizes(node, registry, root: true, maximums: maximums).Order().ToList();
        if (maximums.Count > 0)
        {
            var preview = new UnitDefinition("", "", [], Profile: new(Loadout: loadout));
            legalSizes = legalSizes.Where(n => Loadouts.Issues(preview, new("", "", "", n, Loadout: Loadouts.Create(preview, n))).Count == 0).ToList();
            if (legalSizes.Count == 0) throw new InvalidDataException("No valid default composition for conditional model limits.");
        }
        var sizes = legalSizes.Where((n, index) => modelCosts?.Count > 0 || index == legalSizes.Count - 1 || copyBreaks.Any(c => Price(n, c) != Price(legalSizes[index + 1], c))).ToList();
        var result = new List<PointOption>();
        foreach (var models in sizes)
        {
            int? previous = null;
            foreach (var copy in copyBreaks)
            {
                var price = Price(models, copy);
                if (price != previous) result.Add(new(models, price, copy));
                previous = price;
            }
        }
        return result;
    }
    private static Func<int, int, bool> Predicate(JsonObject node, string unitId, SortedSet<int> copyBreaks, string catalogueId = "", bool allied = false, Dictionary<string, Func<int, int>>? counts = null, HashSet<string>? copyIds = null)
    {
        var predicates = new List<Func<int, int, bool>>();
        foreach (var condition in Children(node, "conditions"))
        {
            if (Text(condition, "scope") == "primary-catalogue" && Text(condition, "field") == "selections"
                && Text(condition, "type") is "instanceOf" or "notInstanceOf" && Text(condition, "value") == "1" && catalogueId.Length > 0)
            {
                var target = Text(condition, "childId");
                if (allied && target != catalogueId) throw new InvalidDataException("Allied price depends on a specific host catalogue.");
                var matches = !allied && target == catalogueId;
                var applies = Text(condition, "type") == "instanceOf" ? matches : !matches;
                predicates.Add((_, _) => applies); continue;
            }
            var count = Text(condition, "childId") == "model" ? (Func<int, int>)(n => n) : counts?.GetValueOrDefault(Text(condition, "childId"));
            if (count == null || Text(condition, "field") != "selections" || Text(condition, "scope") != unitId && Text(condition, "scope") is not ("self" or "unit-self"))
                throw new InvalidDataException("Unrecognized price condition.");
            var value = Number(condition, "value"); var type = Text(condition, "type");
            if (type is not ("greaterThan" or "atLeast" or "lessThan" or "atMost" or "equalTo" or "notEqualTo")) throw new InvalidDataException("Unsupported model-count condition.");
            predicates.Add((models, _) => type switch { "greaterThan" => count(models) > value, "atLeast" => count(models) >= value, "lessThan" => count(models) < value, "atMost" => count(models) <= value, "equalTo" => count(models) == value, _ => count(models) != value });
        }
        foreach (var local in Children(node, "localConditionGroups"))
        {
            var conditions = Children(local, "conditions");
            if (Text(local, "type") != "atLeast" || Text(local, "field") != "selections" || Text(local, "scope") != "parent" || Text(local, "repeats") is not ("" or "1")
                || Children(local, "conditionGroups").Count > 0 || Children(local, "localConditionGroups").Count > 0 || conditions.Count != 2
                || !conditions.Any(c => Text(c, "type") == "before" && Text(c, "childId") == "any" && Text(c, "scope") == "self" && Number(c, "value") == 1)
                || !conditions.Any(c => Text(c, "type") == "instanceOf" && (Text(c, "childId") == unitId || copyIds?.Contains(Text(c, "childId")) == true) && Text(c, "scope") == "self"))
                throw new InvalidDataException("Unsupported copy-count condition.");
            var fromCopy = Number(local, "value") + 1; copyBreaks.Add(fromCopy);
            predicates.Add((_, copy) => copy >= fromCopy);
        }
        foreach (var group in Children(node, "conditionGroups"))
        {
            var type = Text(group, "type");
            if (type is not ("and" or "or")) throw new InvalidDataException("Unsupported price condition group.");
            predicates.Add(Predicate(group, unitId, copyBreaks, catalogueId, allied, counts, copyIds));
        }
        return Text(node, "type") == "or" ? (models, copy) => predicates.Any(p => p(models, copy)) : (models, copy) => predicates.All(p => p(models, copy));
    }
    private static IEnumerable<JsonObject> DirectChildren(JsonObject node, Dictionary<string, JsonObject> registry)
        => Children(node, "selectionEntries").Concat(Children(node, "selectionEntryGroups")).Concat(Children(node, "entryLinks"))
            .Where(n => !SeparateSelection(n)).Select(n => Resolve(n, registry)).Where(n => !SeparateSelection(n));
    private static bool EnhancementGroup(JsonObject node) => Text(node, "name") == "Enhancements" || Text(node, "name").EndsWith(" Enhancements", StringComparison.Ordinal)
        || Text(node, "name").StartsWith("Enhancements - ", StringComparison.Ordinal);
    private static bool SeparateSelection(JsonObject node) => EnhancementGroup(node) || Text(node, "name") is "Crusade" or "Weapon Modifications"
        || Children(node, "costs").Any(c => Text(c, "name") == "Enhancements" && Text(c, "value") == "1");
    private static IEnumerable<JsonObject> Descendants(JsonObject node, Dictionary<string, JsonObject> registry, SelectionPath path)
    {
        if (!path.Enter(node)) throw new InvalidDataException("Cyclic selection reference.");
        foreach (var child in DirectChildren(node, registry))
        {
            yield return child;
            foreach (var descendant in Descendants(child, registry, path)) yield return descendant;
        }
        path.Exit(node);
    }
    private static (int Min, int Max) Bounds(JsonObject node, Dictionary<string, JsonObject> registry, SelectionPath path, bool root = false, bool bundle = false, Dictionary<string, int>? maximums = null)
    {
        if (!path.Enter(node)) throw new InvalidDataException("Cyclic model selection.");
        var model = Text(node, "type") == "model";
        var children = model ? [] : DirectChildren(node, registry).ToList();
        if (!model && ReplacementSize(node, registry) is int replacementSize)
        {
            path.Exit(node); return (replacementSize, replacementSize);
        }
        // Group totals constrain model-role substitutions, including nested weapon
        // specialists. An upgrade containing models is a bundle, not one model.
        bool ModelGroup(JsonObject entry, int depth = 0) => Text(entry, "type") == "model" || depth < 30 && Text(entry, "type") == ""
            && DirectChildren(entry, registry).ToList() is { Count: > 0 } nested && nested.All(c => ModelGroup(c, depth + 1));
        int Minimum(JsonObject entry, int depth = 0)
        {
            if (depth > 30) throw new InvalidDataException("Cyclic model selection.");
            var minima = Children(entry, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent" && Text(c, "type") == "min").ToList();
            if (Modifiers(entry).Any(m => minima.Any(c => Text(c, "id") == Text(m, "field")) && !InactiveGameMode(m, registry)))
                throw new InvalidDataException("Conditional model minimum needs source review.");
            return Math.Max(minima.Select(c => Number(c, "value")).DefaultIfEmpty(0).Max(), Text(entry, "type") == "model" ? 0 : DirectChildren(entry, registry).Sum(c => Minimum(c, depth + 1)));
        }
        var fixedLimits = Children(node, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent").ToList();
        if (!root && !model && Text(node, "type") == "" && children.Count > 0 && children.All(c => ModelGroup(c))
            && fixedLimits.Count(c => Text(c, "type") == "min") <= 1 && fixedLimits.Count(c => Text(c, "type") == "max") == 1
            && !Modifiers(node).Any(m => fixedLimits.Any(c => Text(c, "id") == Text(m, "field")) && !InactiveGameMode(m, registry)))
        {
            var minimum = fixedLimits.SingleOrDefault(c => Text(c, "type") == "min") is { } lower ? Number(lower, "value") : children.Sum(c => Minimum(c));
            var maximum = Number(fixedLimits.Single(c => Text(c, "type") == "max"), "value");
            if (minimum > 0 && maximum >= minimum) { path.Exit(node); return (minimum, maximum); }
        }
        var choice = !root && !model && Text(node, "type") == "" && children.Count > 0 && children.All(c => Text(c, "type") == "upgrade")
            && fixedLimits.Any(c => Text(c, "type") == "min" && Text(c, "value") == "1")
            && fixedLimits.Any(c => Text(c, "type") == "max" && Text(c, "value") == "1")
            && !Modifiers(node).Any(m => fixedLimits.Any(c => Text(c, "id") == Text(m, "field")));
        var childBounds = children.Select(c => Bounds(c, registry, path, bundle: choice, maximums: maximums)).ToList();
        path.Exit(node);
        if (!model && childBounds.All(b => b.Max == 0)) return (0, 0);
        var min = model ? 0 : childBounds.Sum(b => b.Min); var max = model ? 1 : childBounds.Sum(b => b.Max);
        if (choice)
            return (childBounds.Min(b => b.Min), childBounds.Max(b => b.Max));
        if (!model && Text(node, "type") == "upgrade")
        {
            if (!bundle) throw new InvalidDataException("Model bundles require an explicit composition choice.");
            return (min, max);
        }
        if (!root)
        {
            var constraints = Children(node, "constraints").Where(c => Text(c, "field") == "selections" && Text(c, "scope") == "parent").ToList();
            foreach (var c in constraints)
                if (Text(c, "type") == "min") min = Math.Max(min, Number(c, "value"));
                else if (Text(c, "type") == "max") max = maximums?.GetValueOrDefault(Text(c, "id"), Number(c, "value")) ?? Number(c, "value");
            var ids = constraints.Select(c => Text(c, "id")).ToHashSet();
            if (Modifiers(node).Any(m => ids.Contains(Text(m, "field")) && maximums?.ContainsKey(Text(m, "field")) != true && !InactiveGameMode(m, registry))) throw new InvalidDataException("Conditional model limits need manual review.");
        }
        return (min, max);
    }
}
