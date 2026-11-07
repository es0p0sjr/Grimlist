using System.Windows;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;
internal static partial class SmokeChecks
{
    private static void CheckStratagemColours(ItemsControl cards)
    {
        cards.UpdateLayout();
        var colours = Descendants<Border>(cards).Select(b => b.Background?.ToString()).ToHashSet();
        Check(new[] { "#FF276442", "#FF245C91", "#FF913E43" }.All(colours.Contains), "Stratagem headers must show all three activation colours.");
        var labels = Descendants<TextBlock>(cards).Select(t => t.Text).ToHashSet();
        Check(new[] { "Either turn", "Your turn", "Opponent's turn" }.All(labels.Contains), "Stratagem turn labels must be visible.");
    }
    private static Button DetachmentAction(FrameworkElement owner, string control, string name)
    {
        owner.UpdateLayout();
        return Descendants<Button>(Find<ItemsControl>(owner, control)).Single(b => b.Tag as string == name);
    }
    private static void CheckDetachmentSelector(MainWindow window, ArmyStore store, string armyId, string imagePath)
    {
        ArmyList Saved() => store.LoadArmies().Single(a => a.Id == armyId);
        void Add(string name) => DetachmentAction(window, "AvailableDetachments", name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Remove(string name) => DetachmentAction(window, "CurrentDetachments", name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Size(int points)
        {
            var input = Find<TextBox>(window, "LimitBox"); input.Text = points.ToString(); input.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        }
        Click(window, "RulesButton");
        Check(window.FindName("RulesDetachmentPicker") == null, "The browse-all detachment selector must be removed.");
        foreach (var name in DetachmentRules.Selected(Saved())) Remove(name);
        Check(Find<ItemsControl>(window, "StratagemCards").Items.Count == 0 && Find<ItemsControl>(window, "EnhancementCards").Items.Count == 0,
            "An army without detachments must not show unrelated rules.");
        Click(window, "SelectDetachmentsButton");
        Check(Find<StackPanel>(window, "DetachmentOptionsPanel").Visibility == Visibility.Visible, "Adding detachments must open inline.");
        Size(1000);
        Check(DetachmentAction(window, "AvailableDetachments", "Virulent Vectorium").IsEnabled, "A lone 3 DP option must fit a 1,000-point game.");
        Add("Virulent Vectorium");
        Check(!DetachmentAction(window, "AvailableDetachments", "Contagion Engines").IsEnabled, "A 3 DP option must be selected alone.");
        Check(!Find<TextBlock>(window, "DetachmentBudgetText").Text.Contains("over"), "A lone 3 DP option must not show an over-budget warning.");
        Remove("Virulent Vectorium");
        Add("Contagion Engines");
        Check(Saved().Disposition == "Reconnaissance", "Single disposition must be automatic.");
        Check(!DetachmentAction(window, "AvailableDetachments", "Champions of Contagion").IsEnabled, "Adding must enforce the remaining DP budget.");
        Size(2000); Add("Champions of Contagion");
        Check(DetachmentRules.Selected(Saved()).SequenceEqual(["Contagion Engines", "Champions of Contagion"]), "One-click additions must save immediately.");
        var disposition = Find<ComboBox>(window, "DispositionPicker");
        Check(disposition.Items.Cast<string>().SequenceEqual(["Reconnaissance", "Take and Hold"]), "Disposition choices must come only from selected detachments.");
        disposition.SelectedItem = "Take and Hold";
        Check(Saved().Disposition == "Take and Hold", "Disposition choice must save immediately.");
        Click(window, "RulesUndoButton"); Check(Saved().Disposition == "Reconnaissance", "Undo lost disposition choice.");
        Click(window, "RulesRedoButton"); Check(Saved().Disposition == "Take and Hold", "Redo lost disposition choice.");
        Check(Find<ItemsControl>(window, "StratagemCards").Items.Count == 9 && Find<ItemsControl>(window, "EnhancementCards").Items.Count == 6,
            "Rules must include both selected detachments, with no unselected cards.");
        foreach (var control in new[] { "DetachmentCards", "StratagemCards", "EnhancementCards" })
            Check(Find<ItemsControl>(window, control).Items.Cast<ReferenceCard>().All(c => DetachmentRules.Contains(Saved(), c.Detachment)), "Unselected detachment rules leaked into the army.");
        Capture(window, imagePath + ".quick-detachments.png");
        Remove("Champions of Contagion");
        Check(Saved().Disposition == "Reconnaissance" && disposition.Items.Count == 1, "Removing a detachment must remove its disposition.");
        CheckStratagemColours(Find<ItemsControl>(window, "StratagemCards"));
        Click(window, "RulesUndoButton"); Check(Saved().Disposition == "Take and Hold", "Undo removal must restore the prior disposition.");
        Size(3000); Add("Flyblown Host");
        Check(DetachmentRules.Spent(Saved(), store.LoadDataset()) == 4 && disposition.Items.Count == 2, "4 DP should fit at 3,000 points; shared dispositions should not duplicate.");
        Size(1000);
        Check(DetachmentRules.Selected(Saved()).Count == 3 && Find<TextBlock>(window, "DetachmentBudgetText").Text.Contains("over"), "Budget reductions must retain choices and show the excess.");
        Click(window, "RulesUndoButton");
        Click(window, "SelectDetachmentsButton");
        Capture(window, imagePath + ".current-detachments.png");
        var final = Saved();
        Click(window, "CloseRulesButton"); Click(window, "NewArmyButton");
        var home = Find<HomeView>(window, "Home");
        var tile = Find<WrapPanel>(home, "TilePanel").Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()).Single(b => b.Tag is ArmyList a && a.Id == armyId);
        tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Click(window, "RulesButton");
        Check(Saved().Disposition == final.Disposition && DetachmentRules.Selected(Saved()).SequenceEqual(DetachmentRules.Selected(final)), "Reopen lost detachments or disposition.");
        Click(window, "CloseRulesButton");

        string? failure = null;
        var setup = new ArmySetupView();
        var setupWindow = new Window { Content = setup, Width = 1280, Height = 860, Owner = window, ShowInTaskbar = false };
        ArmyList? created = null; setup.Completed += army => created = army;
        setup.Begin(store.LoadDataset(), [], true);
        setupWindow.ContentRendered += (_, _) =>
        {
            try
            {
                Find<ListBox>(setup, "FactionList").SelectedValue = "Death Guard"; Click(setup, "NextButton");
                var selector = Find<DetachmentSelector>(setup, "PointsDetachments");
                Check(selector.Visibility == Visibility.Visible && !Find<Button>(setup, "NextButton").IsEnabled, "New army must select detachments.");
                Find<ComboBox>(selector, "GameSizePicker").SelectedValue = 2000;
                DetachmentAction(selector, "OptionsList", "Contagion Engines").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DetachmentAction(selector, "OptionsList", "Champions of Contagion").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Find<ComboBox>(selector, "DispositionPicker").SelectedItem = "Take and Hold";
                Capture(setupWindow, imagePath + ".detachment-setup.png");
                Click(setup, "NextButton"); Find<TextBox>(setup, "NameInput").Text = "Disposition setup test";
                Click(setup, "BackButton");
                Check(selector.Draft.Disposition == "Take and Hold" && DetachmentRules.Selected(selector.Draft).Count == 2, "Back lost setup choices.");
                Click(setup, "NextButton"); Click(setup, "NextButton");
                Check(created?.Limit == 2000 && created.Disposition == "Take and Hold" && DetachmentRules.Selected(created).Count == 2, "Army creation lost its disposition or detachments.");
            }
            catch (Exception ex) { failure = ex.Message; }
            finally { setupWindow.Close(); }
        };
        setupWindow.ShowDialog(); Check(failure == null, failure ?? "");
    }
}
