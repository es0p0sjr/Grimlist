using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace GrimList.Core;

// The public Yellowscribe roszParser consumes BattleScribe XML, including its 11e gameSystemId.
// https://github.com/ThePants999/Yellowscribe/blob/main/bin/roszParser.js
public static class YellowscribeExport
{
    private static readonly XNamespace Ns = "http://www.battlescribe.net/schema/rosterSchema";
    private static XElement Node(string name, params object?[] values) => new(Ns + name, values);
    private static XAttribute Attr(string name, object value) => new(name, value);
    private static XElement Profile(string name, string type, IEnumerable<KeyValuePair<string, string>> characteristics) => Node("profile",
        Attr("name", name), Attr("typeName", type), Node("characteristics", characteristics.Select(c => Node("characteristic", Attr("name", c.Key), c.Value))));
    private static XElement Ability(string name, string text) => Profile(name, "Abilities", new Dictionary<string, string> { ["Description"] = text });

    public static string Roster(ArmyList army, Dataset? data)
    {
        if (data == null) throw new InvalidDataException("Import datasheets before exporting to Yellowscribe.");
        if (army.Entries.Count == 0) throw new InvalidDataException("Add units before exporting to Yellowscribe.");
        if (data.Source.Edition is not ("10" or "11")) throw new InvalidDataException("Yellowscribe export requires an identified 10th- or 11th-edition library.");
        var selection = new List<XElement>();
        foreach (var entry in RosterExport.OrderedEntries(army, data))
        {
            var unit = MusterEffects.EffectiveUnit(army, entry, data) ?? throw new InvalidDataException(entry.Name + ": datasheet unavailable.");
            var profile = unit.Profile ?? throw new InvalidDataException(entry.Name + ": datasheet unavailable.");
            var loadout = entry.Loadout ?? throw new InvalidDataException(entry.Name + ": configure the equipment before exporting to Yellowscribe.");
            var issues = Loadouts.Issues(unit, entry);
            if (issues.Count > 0) throw new InvalidDataException(entry.Name + ": " + string.Join(" ", issues));
            var definition = Loadouts.Definition(unit);
            var slots = loadout.Manual ? Enumerable.Range(0, entry.Models).Select(_ => new ModelLoadout("manual", [])).ToList() : loadout.Models;
            if (slots.Count != entry.Models) throw new InvalidDataException(entry.Name + ": model composition is incomplete.");
            // Flat, manually counted weapons have no per-model ownership. Never put each weapon on every model.
            var unitWeapons = Loadouts.Weapons(unit, new([], loadout.Equipment, loadout.Manual));
            if (entry.Models > 1 && unitWeapons.Count > 0)
                throw new InvalidDataException(entry.Name + ": some weapons are only assigned to the unit. Import/configure per-model equipment before exporting to Yellowscribe.");
            var profiles = Node("profiles", Loadouts.Abilities(unit, loadout).Select(a => Ability(a.Name, a.Text)));
            if (EffectiveDatasheets.Project(army, entry, data) is { AppliedModifierSources.Count: > 0 } effective)
                profiles.Add(Ability("Effective datasheet changes", effective.Summary));
            var attachmentNotes = RosterExport.AttachmentNotes(army, data, entry);
            if (attachmentNotes.Count > 0) profiles.Add(Ability("GrimList attachments", string.Join("\n", attachmentNotes)));
            if (entry.Id == army.WarlordEntryId) profiles.Add(Ability("Warlord", "This model is your Warlord."));
            if (entry.Enhancement is { } enhancement)
            {
                var card = Enhancements.Find(army, data, enhancement);
                profiles.Add(Ability(enhancement.Name, card?.DisplayText ?? "Rule text unavailable."));
            }
            var models = Node("selections");
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i]; var role = definition.Roles.FirstOrDefault(r => r.Id == slot.RoleId);
                var modelName = role?.Name ?? unit.Name;
                var candidates = profile.Models ?? [];
                var stats = candidates.FirstOrDefault(m => UnitIdentity.Normalize(m.Name) == UnitIdentity.Normalize(modelName))?.Characteristics
                    ?? profile.Characteristics ?? (candidates.Count == 1 ? candidates[0].Characteristics : null);
                if (stats == null || new[] { "M", "T", "SV", "W", "LD", "OC" }.Any(k => !stats.Keys.Any(s => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException(entry.Name + ": complete characteristics could not be matched to " + modelName + ".");
                var standardized = stats.Where(c => !DatasheetStats.IsInvulnerable(c.Key)).ToDictionary(c => c.Key.ToUpperInvariant(), c => c.Value);
                if (DatasheetStats.Invulnerable(stats) is { Length: > 0 } save) standardized["INSV"] = save;
                var model = Node("selection", Attr("id", entry.Id + "-model-" + i), Attr("name", modelName), Attr("type", "model"), Attr("number", 1),
                    Node("profiles", Profile(modelName, "Unit", standardized)));
                var chosen = new UnitLoadout(loadout.Manual ? [] : [slot], entry.Models == 1 ? loadout.Equipment : [], loadout.Manual);
                var weapons = Loadouts.Weapons(unit, chosen);
                model.Add(Node("selections", weapons.Select((w, n) => Node("selection", Attr("id", entry.Id + "-weapon-" + i + "-" + n),
                    Attr("name", w.Profile.Name), Attr("type", "upgrade"), Attr("number", w.Count), Node("profiles", Weapon(w.Profile))))));
                models.Add(model);
            }
            var categories = MusterRules.Keywords(army, unit, data).Append("Faction: " + AlliedRules.OriginFaction(unit)).Distinct()
                .Select(k => Node("category", Attr("name", k), Attr("primary", "false")));
            selection.Add(Node("selection", Attr("id", entry.Id), Attr("name", LeaderRules.EntryLabel(army, entry.Id)), Attr("type", "unit"), Attr("number", 1),
                Node("categories", categories), profiles, models));
        }
        var root = Node("roster", Attr("id", army.Id), Attr("name", army.Name), Attr("battleScribeVersion", "2.03"),
            Attr("gameSystemName", army.Game + " " + data.Source.Edition + "th Edition"),
            Attr("gameSystemId", data.Source.Edition == "11" ? "sys-352e-adc2-7639-d610" : "sys-352e-adc2-7639-d6a9"),
            Node("forces", Node("force", Attr("id", "grimlist-force"), Attr("name", army.Faction), Node("selections", selection))));
        var validation = ArmyCalculator.Calculate(army, data);
        root.Add(Node("customNotes", string.Join("\n", new[] { validation.ValidationStatus }.Concat(validation.Issues))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }
    private static XElement Weapon(WeaponProfile weapon) => Profile(weapon.Name, weapon.Type == "Melee" ? "Melee Weapons" : "Ranged Weapons",
        new Dictionary<string, string> { ["Range"] = weapon.Type == "Melee" ? "Melee" : weapon.Range, ["A"] = weapon.Attacks,
            [weapon.Type == "Melee" ? "WS" : "BS"] = weapon.Skill, ["S"] = weapon.Strength, ["AP"] = weapon.Ap,
            ["D"] = weapon.Damage, ["Keywords"] = string.IsNullOrWhiteSpace(weapon.Keywords) ? "-" : weapon.Keywords });
}

public static class YellowscribeClient
{
    public const string Site = "https://yellowscribe.link/";
    public static async Task<string> Upload(HttpClient client, string roster, CancellationToken cancellation = default)
    {
        using var content = new StringContent(roster, Encoding.UTF8, "application/octet-stream");
        using var response = await client.PostAsync(Site + "makeArmyAndReturnCode?filename=grimlist.ros&allocationMode=allModels&uiHeight=700&uiWidth=1200&modules=MatchedPlay", content, cancellation);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Yellowscribe returned HTTP {(int)response.StatusCode}. Save the .ros file and try its website later.");
        var body = await response.Content.ReadAsStringAsync(cancellation);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() is { Length: 8 } value && value.All(Uri.IsHexDigit)) return value;
        }
        catch (JsonException) { }
        throw new InvalidDataException("Yellowscribe did not return a valid army code. Save the .ros file and try its website.");
    }
}
