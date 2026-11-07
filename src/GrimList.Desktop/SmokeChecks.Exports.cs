using System.IO;
using System.Windows.Controls;
using GrimList.Core;

namespace GrimList.Desktop;

internal static partial class SmokeChecks
{
    private static void CheckExports(MainWindow owner, string directory, string imagePath)
    {
        var root = Path.GetDirectoryName(directory)!;
        var data = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "export-fixture-data.json")));
        var army = DataJson.Read<ArmyList>(File.ReadAllText(Path.Combine(root, "export-fixture-army.json")));
        var before = DataJson.Write(army);
        var export = new ExportWindow(army, data) { Owner = owner }; export.Show();
        try
        {
            Check(Find<Button>(export, "SavePdfButton").IsEnabled && Find<Button>(export, "SaveYellowscribeButton").IsEnabled, "Ready roster export actions disabled");
            Check(Find<TextBox>(export, "TextPreview").Text.Contains("↳ Commander"), "UI preview lost attachment grouping");
            export.SaveToFile(Path.Combine(root, "roster-selected.pdf"), "pdf");
            export.SaveToFile(Path.Combine(root, "roster.ros"), "ros");
            export.SaveToFile(Path.Combine(root, "roster.txt"), "txt");
            Find<CheckBox>(export, "AllWeapons").IsChecked = true;
            Find<CheckBox>(export, "IncludeRules").IsChecked = false;
            export.SaveToFile(Path.Combine(root, "roster-all-weapons.pdf"), "pdf");
            Capture(export, imagePath + ".exports.png");
        }
        finally { export.Close(); }
        var missing = new ExportWindow(army, data with { Units = [] }) { Owner = owner }; missing.Show();
        try
        {
            Check(!Find<Button>(missing, "SaveYellowscribeButton").IsEnabled && !Find<Button>(missing, "SendYellowscribeButton").IsEnabled, "Missing datasheets silently exported to Yellowscribe");
            missing.SaveToFile(Path.Combine(root, "roster-missing.pdf"), "pdf");
        }
        finally { missing.Close(); }
        var validationData = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "validation-fixture-data.json")));
        var validationArmy = DataJson.Read<ArmyList>(File.ReadAllText(Path.Combine(root, "validation-fixture-army.json")));
        foreach (var valid in new[] { true, false })
        {
            var candidate = valid ? validationArmy : validationArmy with { WarlordEntryId = "", Detachments = [] };
            var validationWindow = new ExportWindow(candidate, validationData) { Owner = owner }; validationWindow.Show();
            try
            {
                var expected = valid ? "Army validated." : "Army not validated.";
                Check(Find<TextBlock>(validationWindow, "ValidationStatus").Text == expected, "Export window validation status is incorrect");
                Check(Find<TextBox>(validationWindow, "TextPreview").Text.Contains(expected), "Text preview disagrees with validation status");
                validationWindow.SaveToFile(Path.Combine(root, valid ? "validation-valid.pdf" : "validation-invalid.pdf"), "pdf");
                Capture(validationWindow, imagePath + (valid ? ".validation-valid.png" : ".validation-invalid.png"));
            }
            finally { validationWindow.Close(); }
        }
        if (File.Exists(Path.Combine(root, "real-association-fixture.json")))
        {
            var real = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(root, "real-association-fixture.json")));
            var names = new[] { "Plague Marines", "Malignant Plaguecaster", "Biologus Putrifier" };
            var roster = ArmyList.New("Death Guard") with { Name = "10 Plague Marines with two Leaders", Entries = names.Select((n, i) =>
            { var u = real.Units.Single(u => u.Name == n); return new ArmyEntry(i.ToString(), u.Key, u.Name, i == 0 ? 10 : 1, Loadout: Loadouts.Create(u, i == 0 ? 10 : 1), AttachedToId: i == 0 ? "" : "0"); }).ToList() };
            File.WriteAllBytes(Path.Combine(root, "death-guard.pdf"), RosterPdf.Create(roster, real));
            File.WriteAllText(Path.Combine(root, "death-guard.ros"), YellowscribeExport.Roster(roster, real));
        }
        Check(DataJson.Write(army) == before, "Export changed the roster");
        File.WriteAllText(imagePath + ".exports.txt", "PASS: export window, text/PDF/ROS save path, selected/all-weapons and rule toggles, missing datasheet handling, actual-source two-Leader export, no roster mutation.");
    }
}
