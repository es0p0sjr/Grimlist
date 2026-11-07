using System.Text;

namespace GrimList.Core;

public static class RosterExport
{
    // Keep each saved selection exactly once, with attached characters immediately after its bodyguard.
    public static List<ArmyEntry> OrderedEntries(ArmyList army, Dataset? data)
    {
        List<ArmyEntry> result = []; HashSet<string> seen = [];
        void Add(ArmyEntry entry)
        {
            if (!seen.Add(entry.Id)) return;
            result.Add(entry);
            foreach (var child in army.Entries.Where(e => e.AttachedToId == entry.Id).OrderBy(e => e, RosterOrder.ByNameThenSize)) Add(child);
        }
        foreach (var entry in army.Entries.Where(e => e.AttachedToId.Length == 0 || army.Entries.All(b => b.Id != e.AttachedToId))
            .OrderBy(e => UnitCategories.Order(Category(army, data, e))).ThenBy(e => e, RosterOrder.ByNameThenSize)) Add(entry);
        foreach (var entry in army.Entries) Add(entry); // Retain invalid/orphaned selections in a reviewable export.
        return result;
    }
    public static string Category(ArmyList army, Dataset? data, ArmyEntry entry) => MusterEffects.EffectiveUnit(army, entry, data) is { } u
        ? AlliedRules.Category(army, u, data) : "Others";
    public static List<string> AttachmentNotes(ArmyList army, Dataset? data, ArmyEntry entry)
    {
        List<string> notes = [];
        if (entry.AttachedToId.Length > 0) notes.Add("Attached to: " + LeaderRules.EntryLabel(army, entry.AttachedToId));
        foreach (var child in army.Entries.Where(e => e.AttachedToId == entry.Id))
            notes.Add($"Attached {LeaderRules.RoleLabel(data?.FindUnit(child.UnitKey))}: {LeaderRules.EntryLabel(army, child.Id)}");
        return notes;
    }
    public static string Text(ArmyList army, Dataset? data, bool includeSourceLinks = false)
    {
        var result = ArmyCalculator.Calculate(army, data);
        var priced = result.Entries.ToDictionary(e => e.Entry.Id);
        var text = new StringBuilder();
        text.AppendLine(army.Name).AppendLine($"{army.Game} · {army.Faction}");
        text.AppendLine($"{result.Total} / {army.Limit} pts{(result.Complete ? "" : " (incomplete — unknown points)")}");
        text.AppendLine(result.ValidationStatus);
        if (data != null) text.AppendLine($"Edition: {(data.Source.Edition.Length > 0 ? data.Source.Edition : "unknown")}");
        if (DetachmentRules.Selected(army).Count > 0) text.AppendLine("Detachment: " + string.Join(", ", DetachmentRules.Selected(army)));
        if (DetachmentRules.UsesPoints(army.Faction, data)) text.AppendLine(army.Limit == 1000
            ? "Detachments: " + DetachmentRules.SelectionSummary(army, data)
            : $"Detachments: {DetachmentRules.Spent(army, data)?.ToString() ?? "?"}/{DetachmentRules.Budget(army.Limit)?.ToString() ?? "?"} DP");
        if (army.Disposition.Length > 0) text.AppendLine("Disposition: " + army.Disposition);
        text.AppendLine("Warlord: " + (army.Entries.FirstOrDefault(e => e.Id == army.WarlordEntryId) is { } warlord ? LeaderRules.EntryLabel(army, warlord.Id) : "Not selected"));
        string? category = null;
        foreach (var entry in OrderedEntries(army, data))
        {
            var attached = entry.AttachedToId.Length > 0;
            var nextCategory = Category(army, data, entry);
            if (!attached && nextCategory != category) { category = nextCategory; text.AppendLine().AppendLine(category.ToUpperInvariant()).AppendLine(new string('─', 36)); }
            var prefix = attached ? "  ↳ " : "";
            var indent = attached ? "      " : "  ";
            var p = priced[entry.Id]; var unit = MusterEffects.EffectiveUnit(army, entry, data);
            text.AppendLine().AppendLine($"{prefix}{LeaderRules.EntryLabel(army, entry.Id)} ({p.Points?.ToString() ?? "UNKNOWN POINTS"}{(p.Points.HasValue ? " pts" : "")})");
            text.AppendLine($"{indent}{entry.Models} model{(entry.Models == 1 ? "" : "s")}{(entry.Id == army.WarlordEntryId ? " · WARLORD" : "")}");
            if (unit != null && AlliedRules.IsAlly(army, unit)) text.AppendLine($"{indent}Ally: {AlliedRules.OriginFaction(unit)} · {AlliedRules.Permission(army, unit, data)?.Name ?? "not permitted"}");
            foreach (var note in AttachmentNotes(army, data, entry)) text.AppendLine(indent + note);
            var granted = MusterEffects.GrantedKeywords(army, entry, data);
            if (granted.Count > 0) text.AppendLine(indent + "Muster keywords: " + string.Join(", ", granted));
            foreach (var group in EffectiveDatasheets.Choices(army, entry, data))
                if (group.Options.FirstOrDefault(o => entry.Choices?.Any(c => c.GroupId == group.Id && c.OptionId == o.Id) == true) is { } choice)
                    text.AppendLine(indent + group.Name + ": " + choice.Name);
            if (p.CopyTax != 0 || p.WargearPoints != 0 || p.EnhancementPoints != 0) text.AppendLine(indent + p.Breakdown);
            if (entry.Enhancement is { } enhancement)
            {
                var card = Enhancements.Find(army, data, enhancement);
                text.AppendLine($"{indent}{(Enhancements.Upgrade(card) ? "Upgrade" : "Enhancement")}: {enhancement.Name} ({enhancement.Detachment}) · {Enhancements.Cost(card)?.ToString() ?? "UNKNOWN"} pts");
            }
            if (unit != null && entry.Loadout != null)
                foreach (var equipment in Loadouts.Equipment(unit, entry.Loadout).GroupBy(e => e.Option.Name))
                    text.AppendLine($"{indent}{equipment.Sum(e => e.Count)} × {equipment.Key}");
            else text.AppendLine(indent + "Equipment not configured");
            if (!string.IsNullOrWhiteSpace(entry.Notes)) text.AppendLine(indent + "Loadout notes: " + entry.Notes);
        }
        if (result.Issues.Count > 0) text.AppendLine().AppendLine("ROSTER CHECKS");
        foreach (var issue in result.Issues) text.AppendLine("! " + issue);
        foreach (var source in Sources(army, data))
        {
            text.AppendLine($"\nSource: {source.Name} · {source.Version} · {source.Date} · edition {source.Edition}");
            if (includeSourceLinks && !string.IsNullOrWhiteSpace(source.Url)) text.AppendLine(source.Url);
        }
        text.AppendLine("\nExported by GrimList.");
        return text.ToString();
    }
    public static List<SourceInfo> Sources(ArmyList army, Dataset? data) => data == null ? [] : army.Entries
        .Select(e => data.FindUnit(e.UnitKey)).OfType<UnitDefinition>()
        .SelectMany(u => new[] { u.PointsSource ?? data.Source, u.ProfileSource ?? data.Source }).DefaultIfEmpty(data.Source).Distinct().ToList();
    public static List<ReferenceCard> Rules(ArmyList army, Dataset? data) => ArmyReferences.ForArmy(army, data)
        .Where(c => c.Section is "Army Rules" or "Detachments" or "Detachment Rules" or "Stratagems").ToList();
}
