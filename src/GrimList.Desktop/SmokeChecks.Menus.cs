using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckMenus(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        using var store = WindowsServices.OpenStore(Path.Combine(root, "menus-ui"));
        store.Import(DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "export-fixture-data.json"))));
        var army = DataJson.Read<ArmyList>(File.ReadAllText(Path.Combine(root, "export-fixture-army.json")));
        store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner }; window.Show();
        ArmyList Saved() => store.LoadArmies().Single();
        var roster = Find<ListBox>(window, "Roster");
        void Snapshot(ContextMenu menu, string suffix)
        {
            menu.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)menu.ActualWidth, (int)menu.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(menu); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(imagePath + suffix); encoder.Save(file);
        }
        MenuItem Context(string id, bool keyboard = false)
        {
            var row = roster.Items.Cast<object>().Single(r => (string)r.GetType().GetProperty("Id")!.GetValue(r)! == id);
            roster.ScrollIntoView(row); window.UpdateLayout();
            var container = (ListBoxItem)roster.ItemContainerGenerator.ContainerFromItem(row);
            if (keyboard) roster.SelectedValue = id;
            var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [keyboard ? (object)roster : container, true, keyboard ? -1d : 1d, keyboard ? -1d : 1d], null)!;
            (keyboard ? (UIElement)roster : container).RaiseEvent(args);
            Check(roster.ContextMenu.IsOpen && (string)roster.SelectedValue == id, "Context menu did not target the clicked unit");
            return roster.ContextMenu.Items.OfType<MenuItem>().Single();
        }
        void Duplicate(MenuItem item) { roster.ContextMenu.IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            Click(window, "RosterMenuButton");
            var menu = Find<Button>(window, "RosterMenuButton").ContextMenu;
            Check(menu.IsOpen && menu.Items.OfType<MenuItem>().Select(AutomationProperties.GetName).SequenceEqual(
                ["New army", "Export army", "Manage data", "Report a Bug", "Export dataset", "Delete army"]), "Roster menu retained duplicate actions or lost a unique action");
            Snapshot(menu, ".roster-menu.png"); menu.IsOpen = false;
            roster.SelectedIndex = -1;
            roster.RaiseEvent((ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [roster, true, -1d, -1d], null)!);
            Check(!roster.ContextMenu.IsOpen, "Keyboard context menu opened without a unit");
            roster.SelectedValue = "body";
            var action = Context("leader"); Check(action.IsEnabled, "Legal duplicate disabled");
            Snapshot(roster.ContextMenu, ".unit-menu.png"); Duplicate(action);
            var copy = Saved().Entries.Last(); var original = Saved().Entries.Single(e => e.Id == "leader");
            Check(copy.UnitKey == original.UnitKey && copy.Id != original.Id && copy.AttachedToId == ""
                && DataJson.Write(copy.Loadout) == DataJson.Write(original.Loadout) && Saved().WarlordEntryId == "leader", "Context duplication copied the selected bodyguard or lost equipment/attachment semantics");
            Click(window, "UndoButton"); Check(Saved().Entries.Count == 3, "Context duplicate did not undo");
            Click(window, "RedoButton"); Check(Saved().Entries.Count == 4, "Context duplicate did not redo");
            Duplicate(Context("leader", true)); Check(Saved().Entries.Count == 5, "Keyboard context menu failed");
            var capped = Context("leader"); Check(!capped.IsEnabled, "Context duplicate ignored copy limit");
            Duplicate(capped); Check(Saved().Entries.Count == 5, "Routed click bypassed copy limit");
            Click(window, "PlayModeButton");
            var playing = Context("body"); Check(!playing.IsEnabled, "Context duplicate enabled in Play mode");
            Duplicate(playing); Check(Saved().Entries.Count == 5, "Context duplicate mutated Play mode roster");
            Click(window, "PlayModeButton");
            File.WriteAllText(imagePath + ".menus.txt", "PASS: consolidated menu; right-click targets the clicked row; keyboard context action; equipment retained, copy unattached, warlord preserved, undo/redo, copy cap and Play mode guards.");
        }
        finally { window.Close(); }
    }
}
