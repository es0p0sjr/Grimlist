using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using GrimList.Core;

namespace GrimList.Desktop;

// A small text/vector PDF writer: embedded TrueType fonts, Unicode extraction, A4 pagination.
// No browser, installed PDF printer, network service or rasterized datasheets are required.
internal sealed class RosterPdf
{
    private const double Width = 595.28, Height = 841.89, Margin = 36, Bottom = 788;
    private sealed class Font(string filename, string name)
    {
        public readonly GlyphTypeface Face = new(new Uri(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), filename)));
        public readonly string Name = name;
        public readonly Dictionary<ushort, string> Used = [];
        public ushort Glyph(Rune rune) => Face.CharacterToGlyphMap.TryGetValue(rune.Value, out var glyph) ? glyph : Face.CharacterToGlyphMap['?'];
        public double Measure(string text, double size) => text.EnumerateRunes().Sum(r => Face.AdvanceWidths[Glyph(r)] * size);
        public string Encode(string text)
        {
            var result = new StringBuilder();
            foreach (var rune in text.EnumerateRunes()) { var glyph = Glyph(rune); Used.TryAdd(glyph, rune.ToString()); result.Append(glyph.ToString("X4")); }
            return result.ToString();
        }
    }
    private readonly Font regular = new("arial.ttf", "Arial"), bold = new("arialbd.ttf", "Arial-Bold");
    private readonly List<StringBuilder> pages = [];
    private readonly string title;
    private string section = "Roster";
    private double y;
    private StringBuilder Page => pages[^1];
    private RosterPdf(string title) { this.title = title; NewPage("Roster"); }
    private static string Num(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    private void Draw(string text, double x, double top, double size = 10, bool strong = false, string color = "0.12 0.14 0.12")
    {
        var font = strong ? bold : regular;
        Page.AppendLine($"BT /{(strong ? "F2" : "F1")} {Num(size)} Tf {color} rg 1 0 0 1 {Num(x)} {Num(Height - top - size)} Tm <{font.Encode(text)}> Tj ET");
    }
    private void NewPage(string? name = null)
    {
        if (name != null) section = name;
        pages.Add(new()); y = 68;
        Draw(Wrap("GRIMLIST  /  " + section, 9, Width - Margin * 2, bold)[0], Margin, 20, 9, true);
        Draw("Page " + pages.Count, Width - 78, Height - 27, 8);
        foreach (var line in Wrap(title, 8, Width - Margin * 2 - 65, regular).Take(1)) Draw(line, Margin, Height - 27, 8);
        Page.AppendLine($"0.48 0.52 0.16 RG 0.8 w {Num(Margin)} {Num(Height - 44)} m {Num(Width - Margin)} {Num(Height - 44)} l S");
    }
    private bool Ensure(double height)
    {
        if (y + height <= Bottom) return false;
        NewPage(); return true;
    }
    private static List<string> Wrap(string text, double size, double width, Font font)
    {
        List<string> result = [];
        foreach (var paragraph in text.Replace("\r", "").Replace("\t", "    ").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > 0 && font.Measure(line + " " + word, size) <= width) { line.Append(' ').Append(word); continue; }
                if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); }
                foreach (var rune in word.EnumerateRunes())
                {
                    if (line.Length > 0 && font.Measure(line.ToString() + rune, size) > width) { result.Add(line.ToString()); line.Clear(); }
                    line.Append(rune.ToString());
                }
            }
            result.Add(line.ToString());
        }
        return result;
    }
    private void Text(string text, double size = 10, bool strong = false, double after = 6)
    {
        foreach (var line in Wrap(text, size, Width - Margin * 2, strong ? bold : regular))
        { Ensure(size * 1.4); Draw(line, Margin, y, size, strong); y += size * 1.4; }
        y += after;
    }
    private void Heading(string text) { Ensure(48); Text(text, 12, true, 8); }
    private void Table(string[] headers, IEnumerable<string[]> rows, double[] widths)
    {
        void Header()
        {
            Page.AppendLine($"0.91 0.93 0.85 rg {Num(Margin)} {Num(Height - y - 22)} {Num(widths.Sum())} 22 re f");
            double x = Margin;
            for (var i = 0; i < headers.Length; i++) { Draw(headers[i], x + 4, y + 5, 8, true); x += widths[i]; }
            y += 25;
        }
        Ensure(55); Header();
        foreach (var row in rows)
        {
            var cells = row.Select((value, i) => Wrap(value, 9, widths[i] - 8, regular)).ToArray();
            var lines = cells.Max(c => c.Count);
            if (y + Math.Min(lines * 13 + 8, 640) > Bottom) { NewPage(); Header(); }
            for (var line = 0; line < lines; line++)
            {
                if (Ensure(14)) Header();
                double x = Margin;
                for (var i = 0; i < cells.Length; i++) { if (line < cells[i].Count) Draw(cells[i][line], x + 4, y, 9); x += widths[i]; }
                y += 13;
            }
            y += 7;
        }
        y += 6;
    }

    public static byte[] Create(ArmyList army, Dataset? data, bool includeRules = true, bool allWeapons = false)
    {
        var pdf = new RosterPdf(army.Name);
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

    private byte[] Bytes()
    {
        List<byte[]> objects = [[], []];
        int Add(string value) { objects.Add(Encoding.ASCII.GetBytes(value)); return objects.Count; }
        int Stream(byte[] bytes, string extra = "")
        {
            using var zipped = new MemoryStream();
            using (var zip = new ZLibStream(zipped, CompressionLevel.Optimal, true)) zip.Write(bytes);
            var data = zipped.ToArray();
            using var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes($"<< /Length {data.Length} /Filter /FlateDecode {extra} >>\nstream\n"));
            output.Write(data); output.Write(Encoding.ASCII.GetBytes("\nendstream")); objects.Add(output.ToArray()); return objects.Count;
        }
        int Embed(Font font)
        {
            using var input = font.Face.GetFontStream(); using var buffer = new MemoryStream(); input.CopyTo(buffer);
            var file = Stream(buffer.ToArray(), "/Length1 " + buffer.Length);
            var descriptor = Add($"<< /Type /FontDescriptor /FontName /{font.Name} /Flags 32 /FontBBox [-1000 -1000 3000 3000] /ItalicAngle 0 /Ascent 1000 /Descent -300 /CapHeight 750 /StemV 80 /FontFile2 {file} 0 R >>");
            var widths = string.Join(" ", font.Used.OrderBy(p => p.Key).Select(p => $"{p.Key} [{Num(font.Face.AdvanceWidths[p.Key] * 1000)}]"));
            var cid = Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{font.Name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /W [{widths}] >>");
            var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /GrimListUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
            foreach (var chunk in font.Used.Chunk(100))
            { cmap.AppendLine(chunk.Length + " beginbfchar"); foreach (var pair in chunk) cmap.AppendLine($"<{pair.Key:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(pair.Value))}>"); cmap.AppendLine("endbfchar"); }
            cmap.AppendLine("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend");
            var unicode = Stream(Encoding.ASCII.GetBytes(cmap.ToString()));
            return Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{font.Name} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {unicode} 0 R >>");
        }
        var normalId = Embed(regular); var boldId = Embed(bold); List<int> pageIds = [];
        foreach (var page in pages)
        {
            var stream = Stream(Encoding.ASCII.GetBytes(page.ToString()));
            pageIds.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(Width)} {Num(Height)}] /Resources << /Font << /F1 {normalId} 0 R /F2 {boldId} 0 R >> >> /Contents {stream} 0 R >>"));
        }
        objects[0] = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ", pageIds.Select(id => id + " 0 R"))}] >>");
        using var pdf = new MemoryStream(); List<long> positions = [0];
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n%"); pdf.Write([0xe2, 0xe3, 0xcf, 0xd3]); Write("\n");
        for (var i = 0; i < objects.Count; i++) { positions.Add(pdf.Position); Write($"{i + 1} 0 obj\n"); pdf.Write(objects[i]); Write("\nendobj\n"); }
        var xref = pdf.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var position in positions.Skip(1)) Write(position.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}
