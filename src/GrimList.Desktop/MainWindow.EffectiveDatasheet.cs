using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class MainWindow
{
    internal static readonly Brush ModifiedValueBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(EffectiveDatasheetDisplay.ModifiedColour));
    private static void HighlightEffectiveValue(TextBlock value, bool modified)
    {
        value.Foreground = modified ? ModifiedValueBrush : Brushes.White;
        value.TextDecorations = modified ? TextDecorations.Underline : null;
        value.ToolTip = modified ? "Modified by active rules" : null;
    }
    private static void StyleEffectiveWeapons(DataGrid table, UnitProfile? source)
    {
        foreach (var column in table.Columns.OfType<DataGridTextColumn>().Where(c => c.Header?.ToString() != "Weapon"))
        {
            var style = new Style(typeof(TextBlock));
            var changed = new DataTrigger { Binding = new Binding { Converter = new WeaponChangedConverter(source, column.Header!.ToString()!) }, Value = true };
            changed.Setters.Add(new Setter(TextBlock.ForegroundProperty, ModifiedValueBrush));
            changed.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            changed.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline));
            changed.Setters.Add(new Setter(TextBlock.ToolTipProperty, "Modified by active rules"));
            style.Triggers.Add(changed); column.ElementStyle = style;
        }
    }
    private sealed class WeaponChangedConverter(UnitProfile? source, string field) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is WeaponProfile weapon && EffectiveDatasheetDisplay.WeaponChanged(source, weapon, field);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private void RefreshDatasheetChoices(ArmyEntry? entry)
    {
        DatasheetChoices.Children.Clear();
        if (entry == null) return;
        foreach (var group in EffectiveDatasheets.Choices(army, entry, data))
        {
            DatasheetChoices.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 5) });
            var options = new[] { new DatasheetChoiceOption("", "Choose…", [], []) }.Concat(group.Options).ToList();
            var picker = new ComboBox { ItemsSource = options, DisplayMemberPath = "Name", SelectedValuePath = "Id", IsEnabled = !playMode,
                SelectedValue = entry.Choices?.FirstOrDefault(c => c.GroupId == group.Id)?.OptionId ?? "" };
            AutomationProperties.SetAutomationId(picker, "datasheet-choice-" + group.Id);
            AutomationProperties.SetName(picker, group.Name);
            picker.SelectionChanged += (_, _) =>
            {
                if (rendering || renderingDetails || playMode || picker.SelectedValue is not string option) return;
                var value = option.Length == 0 ? null : option;
                Run(() =>
                {
                    var proposed = session.ProposeChoice(entry.Id, group.Id, value);
                    var invalid = session.InvalidEnhancements(proposed);
                    if (invalid.Count > 0 && MessageBox.Show(this, "This choice makes the following enhancements unavailable:\n\n"
                        + string.Join("\n", invalid.Select(e => e.Name + " — " + e.Enhancement!.Name))
                        + "\n\nRemove them and apply this choice?", "Change " + group.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    { RefreshDetails(); return; }
                    Edit(() => session.SelectChoice(entry.Id, group.Id, value, invalid.Count > 0));
                });
            };
            DatasheetChoices.Children.Add(picker);
        }
    }
}
