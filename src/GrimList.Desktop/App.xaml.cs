using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GrimList.Desktop;
public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Window? loading = null;
        ArmyStore? repository = null;
        try
        {
            var index = Array.IndexOf(e.Args, "--data-dir");
            var directory = new WindowsPaths(index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null).DataDirectory;
            var label = new System.Windows.Controls.TextBlock { Text = "Opening your library…", Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap };
            loading = new Window { Title = "GrimList", Width = 430, Height = 150, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = label };
            loading.Show();
            var progress = new Progress<string>(message => label.Text = message);
            repository = await Task.Run(() =>
            {
                var opened = LibraryBootstrap.Open(new WindowsPaths(directory), new WindowsSqliteConnectionFactory(),
                    progress: message => ((IProgress<string>)progress).Report(message));
                try
                {
                    ReferenceLibraryRecovery.Restore(opened, message => ((IProgress<string>)progress).Report(message));
                    return opened;
                }
                catch { opened.Dispose(); throw; }
            });
            if (Dispatcher.HasShutdownStarted) { repository.Dispose(); return; }
            var window = new MainWindow(repository);
            MainWindow = window;
            window.Show();
            loading.Close();
#if SMOKE_CHECKS
            var previewIndex = Array.IndexOf(e.Args, "--smoke-test");
            if (previewIndex >= 0 && previewIndex + 1 < e.Args.Length)
            {
                window.ShowInTaskbar = false;
                _ = window.Dispatcher.InvokeAsync(() =>
                {
                    var imagePath = Path.GetFullPath(e.Args[previewIndex + 1]);
                    try
                    {
                        if (e.Args.Contains("--pantheon-only")) SmokeChecks.CheckPantheon(window, directory, imagePath);
                        else if (e.Args.Contains("--enhancement-weapons-only")) SmokeChecks.CheckEnhancementWeapons(window, directory, imagePath);
                        else if (e.Args.Contains("--army-rule-coverage-only")) SmokeChecks.CheckArmyRuleCoverage(window, imagePath);
                        else if (e.Args.Contains("--enhancement-evidence-only")) SmokeChecks.CheckEnhancementEvidence(window, imagePath);
                        else if (e.Args.Contains("--battle-tracker-only")) SmokeChecks.CheckBattleTracker(window, directory, imagePath);
                        else if (e.Args.Contains("--primary-missions-only")) SmokeChecks.CheckPrimaryMissions(window, directory, imagePath);
                        else if (e.Args.Contains("--async-import-only")) SmokeChecks.CheckImportResponsiveness(window, directory, imagePath);
                        else if (e.Args.Contains("--reference-recovery-only")) SmokeChecks.CheckReferenceRecovery(window, directory, imagePath);
                        else if (e.Args.Contains("--full-import-only")) SmokeChecks.CheckFullLibraryImport(window, directory, imagePath);
                        else if (e.Args.Contains("--marine-stratagems-only")) SmokeChecks.CheckSpaceMarineStratagems(window, directory, imagePath);
                        else if (e.Args.Contains("--army-rules-only")) SmokeChecks.CheckSpaceMarineArmyRule(window, directory, imagePath);
                        else if (e.Args.Contains("--stats-retirement-only")) SmokeChecks.CheckStatRetirement(window, directory, imagePath);
                        else if (e.Args.Contains("--detachment-budget-only")) SmokeChecks.CheckDetachmentBudget(window, directory, imagePath);
                        else if (e.Args.Contains("--detachments-only")) SmokeChecks.CheckDetachmentCatalogue(window, directory, imagePath);
                        else if (e.Args.Contains("--faction-loading-only")) SmokeChecks.CheckFactionLoading(window, directory, imagePath);
                        else if (e.Args.Contains("--windows-performance-only")) SmokeChecks.CheckWindowsPerformance(window, directory, imagePath);
                        else if (e.Args.Contains("--rules-packs-only")) SmokeChecks.CheckRulesPacks(window, directory, imagePath);
                        else if (e.Args.Contains("--mfm-only")) SmokeChecks.CheckMfm(window, directory, imagePath);
                        else if (e.Args.Contains("--decimus-only")) SmokeChecks.CheckDecimus(window, directory, imagePath);
                        else if (e.Args.Contains("--attachment-statlines-only")) SmokeChecks.CheckStatlines(window, directory, imagePath);
                        else if (e.Args.Contains("--recipients-only")) SmokeChecks.CheckRecipients(window, directory, imagePath);
                        else if (e.Args.Contains("--weapon-rules-only")) SmokeChecks.CheckWeaponRules(window, directory, imagePath);
                        else SmokeChecks.Run(window, directory, imagePath);
                        window.Close();
                    }
                    catch (Exception ex) { File.WriteAllText(imagePath + ".result.txt", "FAIL: " + ex); Shutdown(1); }
                }, DispatcherPriority.ApplicationIdle);
            }
#endif
        }
        catch (Exception ex)
        {
            repository?.Dispose();
            var smoke = Array.IndexOf(e.Args, "--smoke-test");
            if (smoke >= 0 && smoke + 1 < e.Args.Length) File.WriteAllText(e.Args[smoke + 1] + ".result.txt", "FAIL startup: " + ex);
            else MessageBox.Show(ex.Message, "GrimList could not start");
            Shutdown(1);
        }
    }
}
