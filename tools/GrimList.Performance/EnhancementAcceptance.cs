using GrimList.Application;
using GrimList.Core;
using System.IO;

// Linked into the regression harness and native test builds. No live libraries,
// network acquisition or platform-specific eligibility implementation.
public static class EnhancementAcceptance
{
    public record Scenario(Dataset Data, ArmyList Army, UnitDefinition Unit, ReferenceCard Card);
    public record Report(int SelectableCards, int EligiblePairs, List<string> NoEligibleBearer);
    public static Report Run(Action<Scenario>? selected = null)
    {
        var data = BootstrapRelease.ReadBundled().Dataset;
        var errors = EnhancementEvidence.ValidateCandidate(data, EnhancementLibraryRepair.BundledEvidence, out _);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        var evidence = new EnhancementEvidence(EnhancementLibraryRepair.BundledEvidence);
        var pairs = 0; var selectable = 0; List<string> noBearer = [];
        foreach (var group in data.ReferenceCards!.Where(c => c.Section == "Enhancements" && EnhancementEvidence.Known(c.Eligibility))
            .GroupBy(c => (c.Faction, c.Detachment)))
        {
            // Keep relevant rules while avoiding scans through unrelated factions.
            var scoped = data with { Units = data.Units.Where(u => u.Faction == group.Key.Faction).ToList(),
                ReferenceCards = data.ReferenceCards!.Where(c => c.Faction == group.Key.Faction
                && (c.Detachment.Length == 0 || c.Detachment == group.Key.Detachment)).ToList() };
            var witnessed = new HashSet<string>();
            var sourceCards = group.ToDictionary(c => c, c => evidence.Match(c).Card!);
            foreach (var unit in data.Units.Where(u => u.Faction == group.Key.Faction && u.Options.Count > 0))
            {
                var entry = new ArmyEntry("bearer", unit.Key, unit.Name, unit.Options[0].Models);
                var army = ArmyList.New(unit.Faction) with { Limit = 2000, Detachment = group.Key.Detachment, Entries = [entry] };
                var effective = EffectiveDatasheets.Project(army, entry, scoped, false)?.Unit;
                if (effective == null) continue;
                var options = Enhancements.Options(army, scoped, effective).Select(Enhancements.Key).ToHashSet();
                foreach (var card in group)
                {
                    var source = sourceCards[card];
                    var expected = card with { Eligibility = source.Eligibility, IsUpgrade = source.IsUpgrade };
                    var eligible = Enhancements.Eligible(army, effective, expected, scoped);
                    if (options.Contains(Enhancements.Key(card)) != eligible)
                        throw new InvalidDataException($"Picker/source disagreement: {unit.Name}/{card.Detachment}/{card.Name}");
                    if (!eligible) continue;
                    pairs++;
                    if (witnessed.Add(Enhancements.Key(card)))
                    {
                        var chosen = Enhancements.Select(army, scoped, entry.Id, new(card.Detachment, card.Name));
                        if (chosen.Entries[0].Enhancement?.Name != card.Name) throw new InvalidDataException("Selection was lost.");
                        selectable++;
                        selected?.Invoke(new(scoped, army, effective, card));
                    }
                }
            }
            noBearer.AddRange(group.Where(c => !witnessed.Contains(Enhancements.Key(c))).Select(c => $"{c.Faction}/{c.Detachment}/{c.Name}"));
        }
        return new(selectable, pairs, noBearer);
    }
}
