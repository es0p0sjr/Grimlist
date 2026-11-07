using GrimList.Core;
using GrimList.Platform;
using Android.Graphics;
using Paint = Android.Graphics.Paint;
using PdfDocument = Android.Graphics.Pdf.PdfDocument;
namespace GrimList.Mobile;

// Android draws the same shared roster, loadout and rule content without WPF or Windows fonts.
public sealed class AndroidRosterPdf : IRosterPdfExporter
{
    private const double Width = 595, Margin = 36;
    public byte[] Create(ArmyList army, Dataset? data, bool includeRules = true, bool allWeapons = false)
    {
        using var pdf = new Writer(army.Name);
        pdf.Text(army.Name, 22, true, 12);
        pdf.Text(string.Join('\n', RosterExport.Text(army, data, includeSourceLinks: true).Split('\n').Skip(1)), 10);
        var totals = ArmyCalculator.Calculate(army, data).Entries.ToDictionary(e => e.Entry.Id);
        foreach (var entry in RosterExport.OrderedEntries(army, data))
        {
            var unit = MusterEffects.EffectiveUnit(army, entry, data); var profile = unit?.Profile;
            pdf.NewPage(LeaderRules.EntryLabel(army, entry.Id));
            pdf.Text(LeaderRules.EntryLabel(army, entry.Id), 20, true, 10);
            pdf.Text($"{entry.Models} models · {totals[entry.Id].Points?.ToString() ?? "UNKNOWN"} pts" + (army.WarlordEntryId == entry.Id ? " · WARLORD" : ""));
            foreach (var note in RosterExport.AttachmentNotes(army, data, entry)) pdf.Text(note, 10, true);
            if (profile == null) { pdf.Text("Datasheet unavailable. Import this unit's datasheet to include its rules and profiles."); continue; }
            pdf.Text(string.Join(" · ", MusterRules.Keywords(army, unit!, data)), 9);
            var models = profile.Models?.Count > 0 ? profile.Models : profile.Characteristics != null ? [new ModelProfile(unit!.Name, profile.Characteristics)] : new List<ModelProfile>();
            foreach (var model in models)
            {
                pdf.Heading(model.Name);
                var pairs = model.Characteristics.ToList();
                foreach (var group in pairs.Chunk(8))
                    pdf.Table(group.Select(p => p.Key).ToArray(), [group.Select(p => p.Value).ToArray()], Enumerable.Repeat((Width - Margin * 2) / group.Length, group.Length).ToArray());
            }
            pdf.Heading(allWeapons ? "Available weapon profiles" : "Selected weapon profiles");
            var weapons = allWeapons ? (profile.Weapons ?? []).Select(w => new EquippedWeapon(w, 0)).ToList() : Loadouts.Weapons(unit!, entry.Loadout);
            if (weapons.Count == 0) pdf.Text(entry.Loadout == null ? "Equipment not configured; select a loadout or export all available weapon profiles." : "No weapon profiles available for this selection.");
            foreach (var type in weapons.GroupBy(w => w.Profile.Type))
            {
                pdf.Heading(type.Key);
                pdf.Table(["Weapon", "Range", "A", "BS/WS", "S", "AP", "D"], type.Select(w => new[]
                {
                    (allWeapons ? "" : w.Count + " × ") + w.Profile.Name + (string.IsNullOrWhiteSpace(w.Profile.Keywords) ? "" : "\n" + w.Profile.Keywords),
                    w.Profile.Range, w.Profile.Attacks, w.Profile.Skill, w.Profile.Strength, w.Profile.Ap, w.Profile.Damage
                }), [245, 55, 45, 45, 40, 40, Width - Margin * 2 - 470]);
            }
            if (entry.Enhancement is { } enhancement)
            {
                pdf.Heading("Enhancement / upgrade: " + enhancement.Name);
                pdf.Text(Enhancements.Find(army, data, enhancement)?.Text ?? "Rule text unavailable.");
            }
            pdf.Heading("Abilities");
            void Ability(AbilityCard ability, int depth = 0)
            {
                pdf.Heading((depth > 0 ? "Option: " : "") + ability.Name); pdf.Text(ability.Text);
                foreach (var option in ability.Options) Ability(option, depth + 1);
            }
            foreach (var ability in AbilityGroups.Create(allWeapons ? profile.Abilities ?? [] : Loadouts.Abilities(unit!, entry.Loadout)))
                Ability(ability);
            var effects = AttachedEffects.ForUnit(army, data, entry);
            if (effects.Count > 0)
            {
                pdf.Heading("Attached unit abilities");
                pdf.Text("Weapon profiles above show imported base values. Apply the following effects when their conditions are met.", 9);
                foreach (var effect in effects) { pdf.Heading(effect.Source + " — " + effect.Ability.Name); pdf.Text(effect.Ability.Text); }
            }
            foreach (var warning in profile.Warnings ?? []) pdf.Text("Source note: " + warning, 8);
            var source = unit!.ProfileSource ?? data!.Source;
            pdf.Text($"Datasheet source: {source.Name} · {source.Version} · {source.Date}\n{source.Url}", 8);
        }
        if (includeRules)
        {
            var rules = RosterExport.Rules(army, data);
            pdf.NewPage("Army and detachment rules"); pdf.Text("Army and detachment rules", 20, true);
            if (rules.Count == 0) pdf.Text("No army or selected detachment rules have been imported.");
            foreach (var card in rules)
            {
                pdf.Heading(card.Name + (card.Cost.Length > 0 ? " · " + card.Cost : ""));
                pdf.Text((ArmyReferences.IsCore(card) ? "Core · " : "") + card.Section + (card.Detachment.Length > 0 ? " · " + card.Detachment : ""), 8);
                pdf.Text(card.Text);
            }
        }
        return pdf.Bytes();
    }


    private sealed class Writer : IDisposable
    {
        private readonly PdfDocument document = new();
        private readonly Paint paint = new(PaintFlags.AntiAlias);
        private PdfDocument.Page? page;
        private float y;
        private int number;
        private string section = "Roster";
        private readonly string title;
        public Writer(string title) { this.title = title; NewPage(); }
        public void NewPage(string? name = null)
        {
            if (page != null) document.FinishPage(page);
            if (name != null) section = name;
            using var info = new PdfDocument.PageInfo.Builder(595, 842, ++number).Create();
            page = document.StartPage(info); y = 42;
            Text("GRIMLIST / " + section, 9, true); y += 8;
            paint.TextSize = 8;
            page!.Canvas!.DrawText("Page " + number, 515, 816, paint);
        }
        public void Text(string text, double size = 10, bool strong = false, double after = 6)
        {
            paint.TextSize = (float)size; paint.Color = Android.Graphics.Color.Rgb(28, 32, 28);
            paint.SetTypeface(strong ? Typeface.DefaultBold : Typeface.Default);
            foreach (var paragraph in text.Replace("\r", "").Replace("\t", "    ").Split('\n'))
            {
                var remaining = paragraph;
                do
                {
                    var count = Math.Max(1, paint.BreakText(remaining, true, 523, null));
                    count = Math.Min(count, remaining.Length);
                    if (count < remaining.Length && remaining.LastIndexOf(' ', Math.Max(0, count - 1), count) is > 0 and var space) count = space;
                    if (y + size * 1.4 > 792) { NewPage(); paint.TextSize = (float)size; paint.SetTypeface(strong ? Typeface.DefaultBold : Typeface.Default); }
                    page!.Canvas!.DrawText(remaining[..count], 36, y, paint); y += (float)(size * 1.4);
                    remaining = remaining[count..].TrimStart();
                } while (remaining.Length > 0);
            }
            y += (float)after;
        }
        public void Heading(string text) => Text(text, 12, true, 8);
        public void Table(string[] headers, IEnumerable<string[]> rows, double[] widths)
        {
            foreach (var row in rows) Text(string.Join("   |   ", row.Select((v, i) => headers[i] + ": " + v)), 9);
        }
        public byte[] Bytes()
        {
            if (page != null) { document.FinishPage(page); page = null; }
            using var output = new MemoryStream(); document.WriteTo(output); return output.ToArray();
        }
        public void Dispose() { if (page != null) document.FinishPage(page); document.Close(); document.Dispose(); paint.Dispose(); }
    }
}
