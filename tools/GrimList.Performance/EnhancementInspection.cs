using System.Security.Cryptography;
using GrimList.Application;
using GrimList.Core;

// Read-only diagnosis against public source fixtures. Never opens a live store.
internal static class EnhancementInspection
{
    internal static void Run(string input, string faction, string unitName, string cardName, string output)
    {
        Dataset data;
        if (input is "bootstrap" or "recovered-bootstrap")
        {
            data = BootstrapRelease.ReadBundled().Dataset;
            if (input == "recovered-bootstrap") data = EffectiveDatasheetRecovery.ApplyBundled(data);
        }
        else if (Path.GetExtension(input).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(input);
            var documents = BsDataDocument.ReadArchive(bytes);
            foreach (var document in documents.Where(d => BsDataImporter.FactionName(d) != faction)) document.Root["library"] = true;
            var source = new SourceInfo("BSData diagnostic archive", Path.GetFileName(Path.GetDirectoryName(input))!, "", "", Edition: "11",
                ContentHash: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            data = BsDataImporter.ImportAll(documents, source).Dataset;
        }
        else data = DataJson.Read<Dataset>(File.ReadAllText(input));
        var unit = data.Units.Single(u => u.Faction == faction && u.Name == unitName);
        var card = data.ReferenceCards!.Single(c => c.Faction == faction && c.Section == "Enhancements" && c.Name == cardName);
        var army = ArmyList.New(faction) with { Detachment = card.Detachment,
            Entries = [new("inspection", unit.Key, unit.Name, unit.Options.FirstOrDefault()?.Models ?? 1)] };
        army = Enhancements.Select(army, data, army.Entries[0].Id, new(card.Detachment, card.Name));
        var state = EffectiveDatasheets.Project(army, army.Entries[0], data)!;
        object? metadataOnlyRepair = null;
        if (Path.GetExtension(input).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = BootstrapRelease.ReadBundled().Dataset;
            var oldUnit = legacy.Units.SingleOrDefault(u => u.Faction == faction && u.Name == unitName);
            if (oldUnit?.Profile is { Weapons: { } oldWeapons } oldProfile && unit.Profile?.Weapons is { } newWeapons
                && oldUnit.ProfileSource?.ContentHash is { Length: > 0 } oldHash && oldHash == unit.ProfileSource?.ContentHash)
            {
                // Diagnostic counterfactual only: exact visible profiles and pinned source
                // must agree before copying identity/category metadata in memory.
                static string Visible(WeaponProfile w) => DataJson.Write(w with { SourceId = null, SourceCategories = null });
                var replacements = oldWeapons.Select(w => newWeapons.Where(n => Visible(n) == Visible(w)).ToList()).ToList();
                if (replacements.All(matches => matches.Count == 1))
                {
                    var repaired = oldUnit with { Profile = oldProfile with { Weapons = replacements.Select(matches => matches[0]).ToList() } };
                    legacy = legacy with { Units = legacy.Units.Select(u => u == oldUnit ? repaired : u).ToList() };
                    var oldArmy = army with { Entries = [army.Entries[0] with { UnitKey = oldUnit.Key }] };
                    var projected = EffectiveDatasheets.Project(oldArmy, oldArmy.Entries[0], legacy)!;
                    metadataOnlyRepair = new { projected.EffectiveWeaponProfiles, projected.AppliedModifierSources };
                }
            }
        }
        var parsed = WeaponRules.WithTextFallback(card.Modifiers, card.Name, card.Text).ToList();
        var exportArmy = army with { Entries = [army.Entries[0] with { Loadout = Loadouts.Create(unit, army.Entries[0].Models) }] };
        var exported = System.Xml.Linq.XDocument.Parse(YellowscribeExport.Roster(exportArmy, data)).Descendants()
            .Where(e => e.Name.LocalName == "profile" && ((string?)e.Attribute("typeName")) is "Ranged Weapons" or "Melee Weapons")
            .Select(e => new { Name = (string?)e.Attribute("name"), Characteristics = e.Descendants().Where(c => c.Name.LocalName == "characteristic")
                .ToDictionary(c => (string)c.Attribute("name")!, c => c.Value) }).ToList();
        var report = new { Input = input, unit.Name, Source = unit.ProfileSource, Card = card,
            CachedEqualsUncached = DataJson.Write(parsed) == DataJson.Write(CompiledRuleText.ForCard(card)),
            ProseOnlyModifiers = DatasheetRuleText.Enhancement(card.Name, card.Text),
            Base = unit.Profile!.Weapons, Effective = state.EffectiveWeaponProfiles,
            state.AppliedModifierSources, MetadataOnlyRepair = metadataOnlyRepair, ExportedWeapons = exported };
        File.WriteAllText(output, DataJson.Write(report));
        Console.WriteLine("Enhancement inspection saved: " + output);
    }
}
