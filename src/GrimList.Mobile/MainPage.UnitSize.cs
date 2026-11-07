using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private sealed record UnitSizeState(int Models, int? Smaller, int? Larger);
    private readonly PresentationValue<UnitSizeState> unitSize = new(new(0, null, null));

    private static UnitSizeState SizeState(int models, List<PointOption> options) => new(models,
        options.Where(o => o.Models < models).Select(o => (int?)o.Models).Max(),
        options.Where(o => o.Models > models).Select(o => (int?)o.Models).Min());

    private View UnitSizeStepper(string id, string name, PresentationValue<UnitSizeState> state)
    {
        var row = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 4, AutomationId = "unit-size-" + id
        };
        var less = Action("−", () => StepUnitSize(id, false), "unit-size-less-" + id, Ui.Input, true);
        var more = Action("+", () => StepUnitSize(id, true), "unit-size-more-" + id, Ui.Input, true);
        foreach (var button in new[] { less, more })
        { button.MinimumWidthRequest = 48; button.MinimumHeightRequest = 48; }
        SemanticProperties.SetDescription(less, "Decrease unit size for " + name);
        SemanticProperties.SetDescription(more, "Increase unit size for " + name);
        state.Bind(less, IsEnabledProperty, value => value.Smaller != null);
        state.Bind(more, IsEnabledProperty, value => value.Larger != null);
        var count = Ui.Text("", 14, true);
        count.HorizontalTextAlignment = TextAlignment.Center; count.VerticalTextAlignment = TextAlignment.Center;
        count.AutomationId = "unit-size-count-" + id;
        state.Bind(count, Label.TextProperty, value => value.Models.ToString());
        state.Bind(count, SemanticProperties.DescriptionProperty, value => value.Models + " models");
        state.Bind(row, IsVisibleProperty, value => value.Smaller != null || value.Larger != null);
        row.Add(less); row.Add(count, 1); row.Add(more, 2);
        return row;
    }

    private Task StepUnitSize(string id, bool increase)
    {
        if (playMode) return Task.CompletedTask;
        return Session.RunAsync(s =>
        {
            // Read the current copy's legal sizes at click time, including after undo
            // or a roster edit, instead of retaining a rendered size or price.
            var priced = s.Result.Entries.FirstOrDefault(p => p.Entry.Id == id);
            if (priced == null) return;
            var state = SizeState(priced.Entry.Models, s.OptionsFor(priced.Entry.UnitKey, priced.Copy));
            if ((increase ? state.Larger : state.Smaller) is { } models) s.ResizeUnit(id, models);
        });
    }
}
