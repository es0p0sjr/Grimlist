using GrimList.Core;
using GrimList.Application;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    // Same palette and categories as Desktop WeaponKeywords.
    private static Color KeywordColour(string keyword) => Color.FromArgb(keyword.ToUpperInvariant() switch
    {
        var k when k.StartsWith("LETHAL HITS") || k.StartsWith("SUSTAINED HITS") || k.StartsWith("DEVASTATING WOUNDS") => "#F4D58D",
        var k when k.StartsWith("ANTI-") || k.StartsWith("PRECISION") || k.StartsWith("TWIN-LINKED") => "#A8E6CA",
        var k when k.StartsWith("HAZARDOUS") || k.StartsWith("ONE SHOT") || k.StartsWith("ONE-SHOT") => "#FFB5AC",
        var k when k.StartsWith("PSYCHIC") || k.StartsWith("EXTRA ATTACKS") => "#D9BEFF",
        _ => "#A8D8FF"
    });
    private async Task ShowReference(string title, IEnumerable<RuleDefinition> definitions)
    {
        var body = new VerticalStackLayout { Padding = 18, Spacing = 14 };
        body.Add(Ui.Text(title, 23, true)); body.Add(Ui.Text("RULE REFERENCE", 12, colour: Ui.Muted));
        var rules = definitions.ToList();
        if (rules.Count == 0) body.Add(Ui.Text("No additional rule text in this library."));
        foreach (var rule in rules)
            body.Add(Ui.Card(new VerticalStackLayout { Spacing = 10, Children =
            { Ui.Text(rule.Name, 18, true, KeywordColour(rule.Name)), Ui.Text(rule.Text, 15) } }, Ui.Surface));
        var close = Ui.Button("Close", async () => await Navigation.PopModalAsync(), "close-reference", Accent);
        var grid = new Grid { Padding = 8, RowDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        grid.Add(Ui.Scroll(body)); grid.Add(close, 0, 1);
        await Navigation.PushModalAsync(new ContentPage { Title = title, BackgroundColor = Ui.Background, Content = grid });
    }
    private View WeaponKeywordLinks(WeaponProfile weapon, UnitDefinition unit)
    {
        var row = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Direction = Microsoft.Maui.Layouts.FlexDirection.Row };
        foreach (var keyword in WeaponRuleReference.Keywords(weapon.Keywords))
        {
            var link = Ui.Button(keyword + " ⓘ", async () => await ShowReference(keyword,
                WeaponRuleReference.ForWeapon(weapon with { Keywords = keyword }, unit, Session.Dataset)), "keyword-" + keyword, Ui.Input);
            link.TextColor = KeywordColour(keyword); link.BorderColor = KeywordColour(keyword); link.BorderWidth = 1;
            link.FontSize = 13; link.Margin = new Thickness(0, 2, 6, 2);
            SemanticProperties.SetDescription(link, "Explain " + keyword); row.Children.Add(link);
        }
        return row;
    }
    private async Task EditArmyLimit()
    {
        if (playMode) return;
        var value = await DisplayPromptAsync("Maximum army points", "Set the points limit for this army.",
            initialValue: Session.Army.Limit.ToString(), keyboard: Keyboard.Numeric, maxLength: 7);
        if (value == null) return;
        if (!int.TryParse(value, out var limit)) throw new InvalidDataException("Enter a numeric points limit.");
        await Session.RunAsync(s => s.SetMetadata(s.Army.Name, limit));
    }
    private void RenderEnhancementChoices(VerticalStackLayout body, UnitView view)
    {
        var entry = view.Entry;
        if (entry == null || view.Enhancements.Count == 0 && entry.Enhancement == null || playMode && entry.Enhancement == null) return;
        body.Add(Ui.Section("ENHANCEMENTS", Accent));
        var usage = view.EnhancementSummary!;
        body.Add(Ui.Text($"{usage.Choices}/{usage.Budget?.ToString() ?? "?"} enhancement choices · {usage.Upgrades} Upgrades (up to 3 copies of each share 1 choice).\nOne enhancement or upgrade across this unit and its attached leaders.", 13, colour: Ui.Muted));
        if (entry.Enhancement is { } chosen)
        {
            var card = usage.SelectedCard;
            body.Add(Ui.Fold("Selected: " + chosen.Name, () => Ui.Text(card?.DisplayText ?? "Rule text unavailable in this library."), Ui.Dark(Accent),
                chosen.Detachment + (card != null ? " · " + card.Cost : "")));
            if (!playMode) body.Add(Action("Remove enhancement / upgrade", () => Session.RunAsync(s => s.SelectEnhancement(entry.Id, null)), "remove-enhancement", Ui.Surface));
        }
        foreach (var problem in usage.Issues)
            body.Add(Ui.Text(problem, 13, colour: Color.FromArgb("#FFB5AC")));
        if (playMode) return;
        if (view.Enhancements.Count > 0) AddEnhancementChooser(body, entry.Id);
    }
    private void RenderEnhancementOptions(VerticalStackLayout body, EnhancementPresentation presentation, ArmyList snapshot, Dataset library, string entryId)
    {
        foreach (var option in presentation.Options)
        {
            var card = option.Card;
            var choice = new EnhancementSelection(card.Detachment, card.Name);
            var selected = option.Selected; var reason = option.Rejection;
            var row = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 5 };
            var button = Action((selected ? "✓ " : "") + card.Name + " · " + card.Cost,
                () => Session.RunAsync(s =>
                {
                    if (ReferenceEquals(s.Army, snapshot) && ReferenceEquals(s.Dataset, library) && s.Context.EntryId == entryId)
                        s.ToggleEnhancement(entryId, choice);
                }), "enhancement-" + card.Name, selected ? Accent : Ui.Input);
            button.IsEnabled = reason == null; button.Opacity = reason == null ? 1 : .4;
            row.Add(button); row.Add(Ui.Button("ⓘ", async () => await ShowReference(card.Name, [new(card.Name, card.DisplayText)]), "enhancement-info-" + card.Name, Ui.Surface, true), 1);
            body.Add(row); body.Add(Ui.Text(reason ?? Enhancements.Kind(card) + " · " + card.Detachment, 12, colour: Ui.Muted));
        }
    }
}
