using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using GrimList.Application;
using GrimList.Core;
using GrimList.Desktop;
using GrimList.Platform.Windows;

// Diagnostic host only: base Application never invokes production App.OnStartup.
// Load the real resource definitions and MainWindow without opening live data.
internal static class WindowsPerformance
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Disposable directory and report directory required.");
        var directory = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
        var marker = Path.Combine(directory, "windows-performance.fixture");
        if (Directory.Exists(directory) && !File.Exists(marker)) throw new InvalidOperationException("Refusing a directory without this harness's fixture marker.");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(output); File.WriteAllText(marker, "Disposable Windows performance fixture");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ApplicationResources.xaml")).Root!;
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(presentation + "ResourceDictionary",
            xaml.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => new XAttribute(a.Name,
                a.Name.LocalName == "local" ? "clr-namespace:GrimList.Desktop;assembly=GrimList" : a.Value)),
            xaml.Element(presentation + "Application.Resources")!.Nodes());
        foreach (var element in dictionary.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:GrimList.Desktop"))
            element.Name = XNamespace.Get("clr-namespace:GrimList.Desktop;assembly=GrimList") + element.Name.LocalName;
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var timer = Stopwatch.StartNew();
        var result = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            try
            {
                var store = await Task.Run(() =>
                {
                    var opened = LibraryBootstrap.Open(new WindowsPaths(directory), new WindowsSqliteConnectionFactory());
                    try { ReferenceLibraryRecovery.Restore(opened); return opened; }
                    catch { opened.Dispose(); throw; }
                });
                window = new MainWindow(store); app.MainWindow = window; window.Show();
                await Frames(window);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var session = (ArmySession)typeof(MainWindow).GetField("session", flags)!.GetValue(window)!;
                File.WriteAllText(Path.Combine(output, "startup.json"), DataJson.Write(new {
                    AppEntryToUsableMs = timer.Elapsed.TotalMilliseconds,
                    ProcessStartToUsableMs = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds,
                    ManagedBytes = GC.GetTotalMemory(false), WorkingSetBytes = Environment.WorkingSet,
                    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    Context = session.Context, SavedArmies = session.LoadArmies().Count,
                    FullReads = session.FactionLoading?.FullReads,
                    Assembly = typeof(MainWindow).Assembly.ManifestModule.ModuleVersionId
                }));
                if (!args.Contains("--startup-only"))
                {
                    void Invoke(string method) => typeof(MainWindow).GetMethod(method, flags, Type.EmptyTypes)!.Invoke(window, null);
                    void Field(string field, bool value) => typeof(MainWindow).GetField(field, flags)!.SetValue(window, value);
                    FrameworkElement Control(string name) => (FrameworkElement)window.FindName(name);
                    async Task Present(string operation)
                    {
                        if (operation == "home") Invoke("ShowHome");
                        else if (operation == "datasheet")
                        {
                            Field("rendering", true);
                            ((ListBox)window.FindName("Roster")).SelectedValue = session.Context.EntryId;
                            Field("rendering", false); Invoke("RefreshDetails");
                        }
                        else
                        {
                            Field("rosterOpen", true); Field("playMode", false); Invoke("ApplyPlayLayout");
                            foreach (var name in new[] { "Home", "GlobalTools", "Setup", "RulesScreen" }) Control(name).Visibility = Visibility.Collapsed;
                            Control("BuilderScreen").Visibility = Visibility.Visible;
                            Invoke("Render");
                        }
                        await Frames(window);
                    }
                    var samples = await WindowsWorkload.Run(session, Present, 10);
                    File.WriteAllText(Path.Combine(output, "performance.json"), DataJson.Write(samples));
                }
            }
            catch (Exception error) { result = 1; File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); }
            finally
            {
                window?.Close();
                app.Shutdown(result);
                // This host enters Dispatcher.Run directly, rather than Application.Run.
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        return result;
    }
    private static async Task Frames(Window window)
    {
        window.UpdateLayout();
        for (var i = 0; i < 2; i++)
        {
            var completion = new TaskCompletionSource();
            EventHandler frame = (_, _) => completion.TrySetResult();
            CompositionTarget.Rendering += frame;
            try { window.InvalidateVisual(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { CompositionTarget.Rendering -= frame; }
        }
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
