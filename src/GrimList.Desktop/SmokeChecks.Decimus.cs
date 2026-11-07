using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GrimList.Application;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    public static void CheckDecimus(MainWindow owner, string directory, string imagePath)
    {
        var archive = Path.Combine("tests", "fixtures", "bsdata", "11e", "cc1830fbfead059f8059ed6c7f8e73cd4c390908", "repository.zip");
        var documents = BsDataDocument.ReadArchive(File.ReadAllBytes(archive));
        var included = new HashSet<string>();
        void Include(BsDataDocument document)
        {
            if (!included.Add(document.Id)) return;
            var dependencies = BsDataDocument.Children(document.Root, "catalogueLinks").Select(l => BsDataDocument.Text(l, "targetId"))
                .Append(BsDataDocument.Text(document.Root, "gameSystemId"));
            foreach (var dependency in documents.Where(d => dependencies.Contains(d.Id))) Include(dependency);
        }
        Include(BsDataImporter.ArmyCatalogues(documents).Single(d => BsDataImporter.FactionName(d) == "Deathwatch"));
        var imported = BsDataImporter.ImportAll(documents.Where(d => included.Contains(d.Id)).ToList(),
            new("BSData", "Decimus regression", "", "", Edition: "11")).Dataset;
        var names = new[] { "Watch Master", "Decimus Kill Team", "Fortis Kill Team" };
        var data = imported with { Units = imported.Units.Where(u => u.Faction == "Deathwatch" && names.Contains(u.Name)).ToList(),
            ReferenceCards = imported.ReferenceCards?.Where(c => c.Faction == "Deathwatch").ToList(),
            Detachments = imported.Detachments?.Where(d => d.Faction == "Deathwatch").ToList() };
        Check(data.Units.Single(u => u.Name == "Decimus Kill Team").Profile?.Loadout?.Composition != null,
            "Regression must use the real constrained Decimus composition");
        using var store = WindowsServices.OpenStore(Path.Combine(Path.GetDirectoryName(directory)!, "decimus-ui-" + Guid.NewGuid().ToString("N")));
        store.Import(data);
        var army = ArmyList.New("Deathwatch") with { Name = "Watch Master then Decimus" }; store.Save(army);
        var window = new MainWindow(store.DirectoryPath) { Owner = owner, ShowInTaskbar = false }; window.Show();
        var session = (ArmySession)typeof(MainWindow).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            typeof(MainWindow).GetMethod("OpenRoster", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [army]);
            var library = Find<ListBox>(window, "UnitPicker");
            foreach (var name in new[] { "Watch Master", "Decimus Kill Team" })
            {
                var unit = library.Items.Cast<UnitDefinition>().Single(u => u.Name == name);
                library.ScrollIntoView(unit); window.UpdateLayout();
                // Exercise QuickAddUnit's selection events, not just session.AddUnit.
                FindTaggedButton(library, unit.Key).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(session.Army.Entries.Count(e => e.Name == name) == 1, "Quick add failed: " + name);
            }
            var leader = session.Army.Entries.Single(e => e.Name == "Watch Master");
            var guard = session.Army.Entries.Single(e => e.Name == "Decimus Kill Team");
            Check(Loadouts.Issues(session.Dataset!.FindUnit(guard.UnitKey)!, guard).Count == 0, "Decimus added with an invalid composition");
            Check(session.AttachmentCandidates(guard.Id).Any(e => e.Id == leader.Id), "Watch Master absent from Decimus leader choices");
            session.Attach(leader.Id, guard.Id);
            Check(LeaderRules.Issues(session.Army, session.Dataset).Count == 0, "Indirect attachment invalid");
            session.Undo(); session.Redo();
            Check(session.Army.Entries.Single(e => e.Id == leader.Id).AttachedToId == guard.Id, "Attachment redo lost");
            // Switching and manually changing valid preview sizes must still refresh.
            foreach (var name in new[] { "Watch Master", "Decimus Kill Team", "Fortis Kill Team", "Decimus Kill Team" })
            {
                library.SelectedItem = library.Items.Cast<UnitDefinition>().Single(u => u.Name == name);
                var sizes = Find<ComboBox>(window, "SizePicker");
                foreach (var size in sizes.Items.Cast<PointOption>().ToList()) sizes.SelectedItem = size;
                window.UpdateLayout();
            }
            File.WriteAllText(imagePath + ".decimus.txt", "PASS: real BSData Watch Master then Decimus quick-add, valid composition, indirect leader picker/attachment, undo/redo and preview size changes.");
            Capture(window, imagePath + ".decimus.png");
        }
        finally { window.Close(); }
    }
}
