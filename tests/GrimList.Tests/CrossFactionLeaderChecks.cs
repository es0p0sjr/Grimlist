using System.Text.Json.Nodes;
using System.Xml.Linq;
using GrimList.Core;

internal static class CrossFactionLeaderChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ArmyList Army(Dataset data) => ArmyList.New(data.Units[0].Faction) with
    {
        Entries = data.Units.Select((u, i) => new ArmyEntry(i.ToString(), u.Key, u.Name, u.Options[0].Models)).ToList()
    };
    private static void BothOrders(Dataset data)
    {
        foreach (var order in new[] { new[] { "1", "2" }, new[] { "2", "1" } })
        {
            var army = LeaderRules.Attach(LeaderRules.Attach(Army(data), data, order[0], "0"), data, order[1], "0");
            Check(LeaderRules.Issues(army, data).Count == 0, "Extra Leader depended on attachment order");
        }
    }
    public static void Run(Action<string, Action> test, string[] args)
    {
        var source = new SourceInfo("Synthetic cross-faction attachment fixtures", "1", "", "", Edition: "11");
        // Deliberately use identical fictional unit names in different factions: only ability text grants permission.
        foreach (var faction in new[] { "Death Guard", "Aeldari", "Space Marines", "Genestealer Cults" })
        test($"{faction}: Leader exceptions survive XML/JSON and direct/shared ability imports", () =>
        {
            const string list = "This model can be attached to the following units:\n\u25a0 ^^Fixture Bodyguard^^\n\n";
            const string exception = "You can attach this model to a __Fixture Bodyguard__ unit, even if one other **Leader** unit has already been attached to it (you cannot attach more than one of the same Leader to the same unit)";
            foreach (var mode in new[] { "direct", "profile", "rule", "infoGroup" })
            foreach (var format in new[] { "xml", "json" })
            {
                XElement Profile(string text) => new("profile", new XAttribute("id", "permission"), new XAttribute("name", "Leader"), new XAttribute("typeName", "Abilities"),
                    new XElement("characteristics", new XElement("characteristic", new XAttribute("name", "Description"), text)));
                XElement Unit(string id, string name) => new("selectionEntry", new XAttribute("id", id), new XAttribute("name", name), new XAttribute("type", "model"),
                    new XElement("costs", new XElement("cost", new XAttribute("name", "pts"), new XAttribute("typeId", "points"), new XAttribute("value", "50"))));
                var bodyguard = Unit("bodyguard", "Fixture Bodyguard");
                var officer = Unit("officer", "Fixture Officer");
                var officerRule = Profile(list); officerRule.SetAttributeValue("id", "officer-rule");
                officer.Add(new XElement("profiles", officerRule));
                var adjunct = Unit("adjunct", "Fixture Adjunct");
                var catalogue = new XElement("catalogue", new XAttribute("id", "fixture"), new XAttribute("name", faction), new XAttribute("gameSystemId", "fixture-game"),
                    new XElement("selectionEntries", bodyguard, officer, adjunct));
                if (mode == "direct") adjunct.Add(new XElement("profiles", Profile(list + exception)));
                else
                {
                    adjunct.Add(new XElement("infoLinks", new XElement("infoLink", new XAttribute("name", "Leader"), new XAttribute("targetId", "permission"), new XAttribute("type", mode))));
                    if (mode == "profile") catalogue.Add(new XElement("sharedProfiles", Profile(list + exception)));
                    if (mode == "rule") catalogue.Add(new XElement("sharedRules", new XElement("rule", new XAttribute("id", "permission"), new XAttribute("name", "Leader"), new XElement("description", list + exception))));
                    if (mode == "infoGroup")
                    {
                        var nested = Profile(list + exception); nested.SetAttributeValue("id", "nested-permission");
                        catalogue.Add(new XElement("sharedInfoGroups", new XElement("infoGroup", new XAttribute("id", "permission"), new XElement("profiles", nested))));
                    }
                }
                var document = BsDataDocument.Parse("fixture.cat", catalogue.ToString());
                if (format == "json") document = BsDataDocument.Parse("fixture.json", new JsonObject { ["catalogue"] = document.Root.DeepClone() }.ToJsonString());
                var data = BsDataImporter.ImportAll([document], source with { Edition = format == "xml" ? "10" : "11" }).Dataset;
                data = data with { Units = new[] { "Fixture Bodyguard", "Fixture Officer", "Fixture Adjunct" }.Select(n => data.Units.Single(u => u.Name == n)).ToList() };
                BothOrders(data);
                var army = Army(data);
                var duplicate = army with { Entries = [.. army.Entries, army.Entries[2] with { Id = "duplicate" }] };
                var attached = LeaderRules.Attach(duplicate, data, "2", "0");
                Check(LeaderRules.Problem(attached, data, "duplicate", "0")?.Contains("same Leader") == true, "Imported duplicate prohibition lost");
                // A plain Leader ability cannot acquire permission from its name or faction.
                var missing = data with { Units = data.Units.Select(u => u.Name == "Fixture Adjunct" ? u with
                    { Profile = u.Profile! with { Abilities = [new(" Leader ", list)] } } : u).ToList() };
                Check(LeaderRules.Problem(LeaderRules.Attach(army, missing, "1", "0"), missing, "2", "0") != null, "Missing exception granted a second Leader");
            }
        });

        var index = Array.IndexOf(args, "--all-factions-attachment-source");
        if (index < 0 || index + 1 >= args.Length) return;
        var imported = DataJson.Read<Dataset>(File.ReadAllText(args[index + 1]));
        foreach (var example in new[]
        {
            ("Aeldari", "Guardian Defenders", "Farseer", "Warlock"),
            ("Aeldari", "Guardian Defenders", "Yvraine", "The Visarch"),
            ("Space Marines", "Bladeguard Veteran Squad", "Captain", "Bladeguard Ancient"),
            ("Black Templars", "Intercessor Squad", "Captain", "Castellan"),
            ("Blood Angels", "Intercessor Squad", "Captain", "Sanguinary Priest"),
            ("Genestealer Cults", "Neophyte Hybrids", "Primus", "Biophagus")
        })
        test($"Imported {example.Item1}: {example.Item3} + {example.Item4} read their extra attachment rule", () =>
        {
            var units = new[] { example.Item2, example.Item3, example.Item4 }.Select(n => imported.Units.Single(u => u.Faction == example.Item1 && u.Name == n)).ToList();
            BothOrders(imported with { Units = units });
            // Isolate the datasheet exception so the shared Leader + Support rule cannot hide parser failures.
            var isolated = units.Select(u => u with { Profile = u.Profile! with { Abilities = u.Profile.Abilities!
                .Where(a => !a.Text.Contains("one leader unit and one support unit", StringComparison.OrdinalIgnoreCase)).ToList() } }).ToList();
            BothOrders(imported with { Units = isolated });
        });
    }
}
