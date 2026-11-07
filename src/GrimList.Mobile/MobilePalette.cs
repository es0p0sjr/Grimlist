using Microsoft.Maui.Graphics;

namespace GrimList.Mobile;
internal static class MobilePalette
{
    // Presentation colours mirror the desktop palette; no WPF dependency.
    private static readonly Dictionary<string, string> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chaos Daemons"] = "#626772", ["Chaos Knights"] = "#527D78", ["Chaos Space Marines"] = "#2C5965", ["Death Guard"] = "#7A850B",
        ["Emperor’s Children"] = "#9845A2", ["Thousand Sons"] = "#007A85", ["World Eaters"] = "#801C20", ["Chaos Titan Legions"] = "#66434C",
        ["Adepta Sororitas"] = "#980900", ["Adeptus Custodes"] = "#907354", ["Adeptus Mechanicus"] = "#A53229", ["Titan Legions"] = "#075FA6",
        ["Astra Militarum"] = "#487B5E", ["Grey Knights"] = "#557D89", ["Imperial Agents"] = "#116C8C", ["Imperial Knights"] = "#527D78",
        ["Black Templars"] = "#004B61", ["Blood Angels"] = "#9B111E", ["Dark Angels"] = "#006221", ["Deathwatch"] = "#75848D",
        ["Space Marines"] = "#607F7D", ["Space Wolves"] = "#487E89", ["Aeldari"] = "#21828E", ["Drukhari"] = "#00626C",
        ["Imperial Fists"] = "#8B751F", ["Iron Hands"] = "#596369", ["Raven Guard"] = "#4C5C64",
        ["Salamanders"] = "#347447", ["Ultramarines"] = "#285A97", ["White Scars"] = "#727C80",
        ["Genestealer Cults"] = "#72164E", ["Leagues of Votann"] = "#517668", ["Necrons"] = "#007A43", ["Orks"] = "#64842D",
        ["T’au Empire"] = "#1B7786", ["Tyranids"] = "#772980"
    };
    public static Color ForFaction(string faction) => Color.FromArgb(Colors.GetValueOrDefault(faction, "#45416F"));
    public static string Group(string name) => name switch
    {
        "Black Templars" or "Blood Angels" or "Dark Angels" or "Deathwatch" or "Space Marines" or "Space Wolves"
            or "Imperial Fists" or "Iron Hands" or "Raven Guard" or "Salamanders" or "Ultramarines" or "White Scars" => "SPACE MARINES",
        "Chaos Daemons" or "Chaos Knights" or "Chaos Space Marines" or "Chaos Titan Legions" or "Death Guard" or "Emperor’s Children" or "Thousand Sons" or "World Eaters" => "CHAOS",
        "Adepta Sororitas" or "Adeptus Custodes" or "Adeptus Mechanicus" or "Astra Militarum" or "Grey Knights" or "Imperial Agents" or "Imperial Knights" or "Titan Legions" => "IMPERIUM",
        "Aeldari" or "Drukhari" or "Genestealer Cults" or "Leagues of Votann" or "Necrons" or "Orks" or "T’au Empire" or "Tyranids" => "XENOS",
        _ => "OTHER"
    };
}
