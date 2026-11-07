using System.Windows;
using GrimList.Core;

namespace GrimList.Desktop;

public partial class RuleReferenceWindow : Window
{
    public RuleReferenceWindow(string title, List<RuleDefinition> definitions)
    {
        InitializeComponent(); Title = title; ReferenceTitle.Text = title;
        DefinitionCards.ItemsSource = definitions.Count > 0 ? definitions : [new RuleDefinition("Weapon keywords", "This weapon profile has no keyword abilities.")];
        DarkWindow.Apply(this);
    }
    private void CloseReference(object sender, RoutedEventArgs e) => Close();
}
