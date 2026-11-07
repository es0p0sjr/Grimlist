using GrimList.Application;
using GrimList.Persistence;
using GrimList.Platform;

namespace GrimList.Mobile;

// Only composition is mobile-specific. The workspace and all workflows are shared.
public sealed class MobileWorkspace(IPlatformPaths paths, ISqliteConnectionFactory connections, IPdfTextExtractor pdf)
{
    private readonly object sync = new();
    private Task<ArmyWorkspace>? opening;
    public Task<ArmyWorkspace> GetAsync(IProgress<string>? progress = null)
    {
        lock (sync)
        {
            if (opening == null || opening.IsFaulted || opening.IsCanceled)
                opening = Task.Run(() =>
                {
#if PERFORMANCE_CHECKS
                    using var capture = LocalPerformance.Capture(MainPage.StartupStages.Enqueue);
#endif
                    var repository = LocalPerformance.Measure("startup.database-open", () => LibraryBootstrap.Open(paths, connections, progress: message => progress?.Report(message)));
                    try
                    {
                        var workspace = LocalPerformance.Measure("startup.session", () => new ArmyWorkspace(repository, pdf, useFactionLoading: true,
                            libraryLoader: r => ReferenceLibraryRecovery.OpenLibrary(r, message => progress?.Report(message)).Library,
                            libraryVariant: ReferenceLibraryRecovery.RecoveryVersion));
                        LocalPerformance.Measure("startup.restore", () => workspace.Session.Restore()); return workspace;
                    }
                    catch { repository.Dispose(); throw; }
                });
            return opening;
        }
    }
}
