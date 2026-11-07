using System.Net;
using System.Xml.Linq;
using GrimList.Core;

internal static class ExportChecks
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    public static void Run(Action<string, Action> test, string root)
    {
        var stats = new Dictionary<string, string> { ["M"] = "6\"", ["T"] = "4", ["SV"] = "3+", ["W"] = "2", ["LD"] = "6+", ["OC"] = "2", ["INV"] = "4+" };
        var rifle = new WeaponProfile("Rifle", "Ranged", "24\"", "2", "3+", "4", "0", "1", "Rapid Fire 1");
        var plasma = rifle with { Name = "Plasma gun — standard", Strength = "7" };
        var hot = plasma with { Name = "Plasma gun — supercharge", Strength = "8", Damage = "2", Keywords = "Hazardous" };
        EquipmentGroup Group(string id, string name, params string[] weapons) => new(id, name,
            [new(id, name, weapons.ToList(), [], new(1), new(1))], new(1), new(1), id);
        var loadout = new LoadoutDefinition([new("champion", "Champion", new(1), new(1), [Group("plasma", "Plasma gun", plasma.Name, hot.Name)]),
            new("trooper", "Guardian", new(1), new(1), [Group("rifle", "Rifle", rifle.Name)])], [], []);
        var body = new UnitDefinition("Space Marines", "Guardians", [new(2, 80)], Category: "Battleline", Profile: new(stats,
            [rifle, plasma, hot, rifle with { Name = "Unequipped flamer" }], Keywords: ["Infantry", "Battleline"],
            Abilities: [new("Long rule", string.Concat(Enumerable.Repeat("A readable rule with enough detail to exercise pagination. ", 400)) + "END OF LONG RULE")], Loadout: loadout));
        var characterLoadout = new LoadoutDefinition([new("model", "Commander", new(1), new(1), [Group("rifle", "Rifle", rifle.Name)])], [], []);
        var character = new UnitDefinition("Space Marines", "Commander", [new(1, 50)], Category: "Characters", Profile: new(stats, [rifle], Keywords: ["Character", "Leader"],
            Abilities: [new("Leader", "This model can be attached to the following unit: Guardians. You can attach this model to one of the above units even if one other Leader unit has already been attached to it.")], Loadout: characterLoadout));
        var medic = character with { Name = "Medic" };
        var source = new SourceInfo("Export fixture", "1", "2026-10-03", "https://example.invalid/source", Edition: "11");
        var data = new Dataset(source, [body, character, medic], Detachments: [new("Space Marines", "Selected"), new("Space Marines", "Other")], ReferenceCards: [new("Space Marines", "Army Rules", "Army reference", "IMPORTED ARMY RULE"),
            new("Space Marines", "Detachments", "Selected rule", "SELECTED DETACHMENT RULE", "Selected"),
            new("Space Marines", "Detachment Rules", "Unselected rule", "UNSELECTED DETACHMENT RULE", "Other")]);
        var army = ArmyList.New("Space Marines") with { Name = "Δοκιμή & <Army>", Detachment = "Selected", WarlordEntryId = "leader", Entries =
            [new("leader", character.Key, character.Name, 1, Loadout: Loadouts.Create(character, 1), AttachedToId: "body"),
             new("body", body.Key, body.Name, 2, Loadout: Loadouts.Create(body, 2)),
             new("medic", medic.Key, medic.Name, 1, Loadout: Loadouts.Create(medic, 1), AttachedToId: "body")] };
        File.WriteAllText(Path.Combine(root, "export-fixture-data.json"), DataJson.Write(data));
        File.WriteAllText(Path.Combine(root, "export-fixture-army.json"), DataJson.Write(army));
        test("Text export groups attached Leaders once, preserves selected equipment and does not double-count points", () =>
        {
            var text = RosterExport.Text(army, data);
            Check(text.Contains("180 / 2000 pts") && text.Contains("↳ Commander (50 pts)") && text.Contains("↳ Medic (50 pts)"), "Group or total missing");
            Check(text.IndexOf("Guardians (80 pts)") < text.IndexOf("↳ Commander"), "Attached characters not grouped beneath bodyguard");
            Check(text.Contains("1 × Plasma gun") && !text.Contains("Unequipped flamer") && text.Contains("Warlord: Commander"), "Loadout or warlord lost");
            Check(RosterExport.OrderedEntries(army, data).Select(e => e.Id).SequenceEqual(new[] { "body", "leader", "medic" }), "Duplicated or omitted selection");
            Check(RosterExport.Rules(army, data).Count(c => !ArmyReferences.IsCore(c)) == 2, "Unselected detachment leaked into PDF rules");
            Check(RosterExport.Rules(army, data).Count(ArmyReferences.IsCore) == 10, "PDF rules omitted universal stratagems");
            File.WriteAllText(Path.Combine(root, "export.txt"), text);
        });
        test("Yellowscribe XML retains 11e identity, model ownership, alternate profiles and attachment notes", () =>
        {
            var xml = YellowscribeExport.Roster(army, data); File.WriteAllText(Path.Combine(root, "export.ros"), xml);
            Check(XDocument.Parse(xml).Root!.Elements().Single(e => e.Name.LocalName == "customNotes").Value
                .StartsWith(ArmyCalculator.Calculate(army, data).ValidationStatus), "Yellowscribe omitted validation status");
            var document = XDocument.Parse(xml); var ns = document.Root!.Name.Namespace;
            Check(document.Root.Attribute("gameSystemId")!.Value == "sys-352e-adc2-7639-d610" && document.Root.Attribute("name")!.Value == army.Name, "Edition or escaped Unicode name lost");
            var units = document.Root.Element(ns + "forces")!.Element(ns + "force")!.Element(ns + "selections")!.Elements(ns + "selection").ToList();
            Check(units.Count == 3, "Attachments were duplicated or merged into bodyguard models");
            var models = units[0].Element(ns + "selections")!.Elements(ns + "selection").ToList();
            Check(models.Count == 2 && models.All(m => (int)m.Attribute("number")! == 1), "Incorrect model counts");
            Check(models[0].Descendants(ns + "profile").Count(p => (string?)p.Attribute("typeName") == "Ranged Weapons") == 2, "Plasma firing modes lost");
            Check(!models[1].ToString().Contains("Plasma") && !xml.Contains("Unequipped flamer"), "Weapons leaked onto unequipped models");
            Check(xml.Contains("Attached to: Guardians") && xml.Contains("Attached Leader: Medic"), "Attachment notes lost");
            Check(units[0].Element(ns + "profiles")!.Elements(ns + "profile").Count(p => (string?)p.Attribute("name") == "GrimList attachments") == 1,
                "Repeated attachment ability names would overwrite one Leader in Yellowscribe");
            Check(models[0].Descendants(ns + "characteristic").Any(c => (string?)c.Attribute("name") == "INSV" && c.Value == "4+"), "Invulnerable save lost");
            var ten = XDocument.Parse(YellowscribeExport.Roster(army, data with { Source = source with { Edition = "10" } }));
            Check(ten.Root!.Attribute("gameSystemId")!.Value == "sys-352e-adc2-7639-d6a9", "10e exported as 11e");
        });
        test("Yellowscribe rejects missing profiles and unassigned manual multi-model weapons without inventing data", () =>
        {
            void Reject(ArmyList a, Dataset d) { try { YellowscribeExport.Roster(a, d); } catch (InvalidDataException) { return; } throw new Exception("Incomplete export accepted"); }
            Reject(army, data with { Units = [body with { Profile = null }, character, medic] });
            Reject(army, data with { Source = source with { Edition = "" } });
            var manual = body with { Profile = body.Profile! with { Loadout = null } };
            var manualLoadout = new UnitLoadout([], [new("manual", "manual:Rifle", 1)], true);
            Reject(army with { Entries = [army.Entries[1] with { Loadout = manualLoadout }] }, data with { Units = [manual] });
        });
        test("Yellowscribe client sends roster bytes and handles success, server errors and malformed codes", () =>
        {
            using var client = new HttpClient(new FixtureHandler(request =>
            {
                Check(request.Method == HttpMethod.Post && request.RequestUri!.Host == "yellowscribe.link", "Wrong destination");
                Check(request.Content!.ReadAsStringAsync().Result == "<roster />", "Wrong request payload");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":\"1234abcd\"}") };
            }));
            Check(YellowscribeClient.Upload(client, "<roster />").GetAwaiter().GetResult() == "1234abcd", "Code lost");
            foreach (var response in new[] { "{\"err\":\"failure\"}", "not JSON", "{\"code\":\"<script>\"}" })
            {
                using var bad = new HttpClient(new FixtureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(response) }));
                try { YellowscribeClient.Upload(bad, "test").GetAwaiter().GetResult(); throw new Exception("Invalid response accepted"); }
                catch (InvalidDataException) { }
            }
            using var offline = new HttpClient(new FixtureHandler(_ => new(HttpStatusCode.ServiceUnavailable)));
            try { YellowscribeClient.Upload(offline, "test").GetAwaiter().GetResult(); throw new Exception("HTTP error accepted"); }
            catch (HttpRequestException) { }
        });
    }
}
