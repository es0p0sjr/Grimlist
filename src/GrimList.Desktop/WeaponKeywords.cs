using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GrimList.Desktop;

public sealed class WeaponKeywords : TextBlock
{
    public WeaponKeywords() => Visibility = Visibility.Collapsed;

    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.Register(
        nameof(Keywords), typeof(string), typeof(WeaponKeywords),
        new PropertyMetadata("", (owner, _) => ((WeaponKeywords)owner).RenderKeywords()));

    public string Keywords
    {
        get => (string)GetValue(KeywordsProperty);
        set => SetValue(KeywordsProperty, value);
    }

    private static readonly Brush Blue = FrozenBrush("#A8D8FF");
    private static readonly Brush Mint = FrozenBrush("#A8E6CA");
    private static readonly Brush Gold = FrozenBrush("#F4D58D");
    private static readonly Brush Lilac = FrozenBrush("#D9BEFF");
    private static readonly Brush Coral = FrozenBrush("#FFB5AC");
    private static readonly Brush Separator = FrozenBrush("#87999D");

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Brush KeywordBrush(string keyword) => keyword.ToUpperInvariant() switch
    {
        var value when value.StartsWith("LETHAL HITS") || value.StartsWith("SUSTAINED HITS") || value.StartsWith("DEVASTATING WOUNDS") => Gold,
        var value when value.StartsWith("ANTI-") || value.StartsWith("PRECISION") || value.StartsWith("TWIN-LINKED") => Mint,
        var value when value.StartsWith("HAZARDOUS") || value.StartsWith("ONE SHOT") || value.StartsWith("ONE-SHOT") => Coral,
        var value when value.StartsWith("PSYCHIC") || value.StartsWith("EXTRA ATTACKS") => Lilac,
        _ => Blue
    };

    private void RenderKeywords()
    {
        Inlines.Clear();
        foreach (var keyword in (Keywords ?? "").Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var label = keyword.Trim('[', ']').Trim();
            if (label is "" or "-" or "—") continue;
            if (Inlines.Count > 0)
                Inlines.Add(new Run("  ·  ") { Foreground = Separator, FontWeight = FontWeights.Normal });
            // Keep a keyword and its value together when the list wraps.
            Inlines.Add(new Run(label.Replace(' ', '\u00a0')) { Foreground = KeywordBrush(label) });
        }
        Visibility = Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
