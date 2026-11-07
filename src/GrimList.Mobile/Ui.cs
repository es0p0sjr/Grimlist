using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace GrimList.Mobile;

internal static class Ui
{
    internal static readonly Color Background = Color.FromArgb("#1D2321"), Surface = Color.FromArgb("#333333"),
        Input = Color.FromArgb("#000000"), Muted = Color.FromArgb("#BAC5C7"), Accent = Color.FromArgb("#403D75"),
        Line = Color.FromArgb("#4B5350"), Danger = Color.FromArgb("#913E43");
    internal static Color Dark(Color colour) => Color.FromRgb(colour.Red * .45, colour.Green * .45, colour.Blue * .45);
    internal static Label Text(string text, double size = 16, bool bold = false, Color? colour = null)
        => new() { Text = text, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            TextColor = colour ?? Colors.White, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.WordWrap };
    internal static Button Button(string text, Action clicked, string id = "", Color? colour = null, bool compact = false)
    {
        var button = new Button { Text = text, FontSize = compact ? 23 : 16, TextColor = Colors.White,
            BackgroundColor = colour ?? Accent, CornerRadius = 7, MinimumHeightRequest = 46,
            Padding = compact ? new Thickness(8, 2) : new Thickness(14, 9), BorderWidth = 0,
            FontAutoScalingEnabled = true, AutomationId = id.Length > 0 ? id : null };
        if (compact) button.MinimumWidthRequest = 44;
        button.Clicked += (_, _) => clicked(); return button;
    }
    internal static Entry Entry(string placeholder, string value = "", string id = "")
        => new() { Placeholder = placeholder, Text = value, TextColor = Colors.White, PlaceholderColor = Muted,
            BackgroundColor = Input, FontSize = 16, MinimumHeightRequest = 48, AutomationId = id.Length > 0 ? id : null };
    internal static Picker Picker(string title, string id = "") => new() { Title = title, TitleColor = Muted,
        TextColor = Colors.White, BackgroundColor = Input, FontSize = 16, MinimumHeightRequest = 48, AutomationId = id.Length > 0 ? id : null };
    internal static Border Card(View view, Color? colour = null, double padding = 10)
        => new() { Content = view, BackgroundColor = colour ?? Input, Padding = padding,
            Stroke = new SolidColorBrush(Line), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 8 } };
    internal static View Section(string text, Color colour)
        => new Border { Content = Text(text, 15, true), BackgroundColor = colour, StrokeThickness = 0, Padding = new Thickness(12, 10) };
    internal static ScrollView Scroll(View content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Default };
    internal static Border Badge(string text, Color colour) => Card(Text(text, 13), colour, 6);
    internal static void Tap(View view, Action action)
    { var tap = new TapGestureRecognizer(); tap.Tapped += (_, _) => action(); view.GestureRecognizers.Add(tap); }
    internal static View Fold(string title, Func<View> content, Color colour, string subtitle = "", bool expanded = false, Action<bool>? toggled = null,
        Action<Label, Label>? bind = null)
    {
        var body = new VerticalStackLayout { IsVisible = expanded, Padding = 12, Spacing = 8 };
        if (expanded) body.Add(content());
        var heading = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, Padding = 12, BackgroundColor = colour };
        var labels = new VerticalStackLayout { Spacing = 3 }; var titleLabel = Text(title, 16, true); labels.Add(titleLabel);
        var subtitleLabel = Text(subtitle, 12); if (subtitle.Length > 0 || bind != null) labels.Add(subtitleLabel);
        bind?.Invoke(titleLabel, subtitleLabel);
        heading.Add(labels); var arrow = Text(expanded ? "⌃" : "⌄", 23); heading.Add(arrow, 1);
        Tap(heading, () => { if (body.Children.Count == 0) body.Add(content()); body.IsVisible = !body.IsVisible; arrow.Text = body.IsVisible ? "⌃" : "⌄"; toggled?.Invoke(body.IsVisible); });
        return Card(new VerticalStackLayout { Spacing = 0, Children = { heading, body } }, Input, 0);
    }
}
