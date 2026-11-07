using GrimList.Platform;

namespace GrimList.Application;

public sealed class LibraryRefreshException(Exception inner) : Exception(
    "The library update was saved, but GrimList could not reopen it. Restart the app before editing armies. You do not need to apply this update again.", inner);

/// <summary>Own this at application lifetime, never at page/window lifetime.</summary>
public sealed class ArmyWorkspace : IDisposable
{
    public IArmyRepository Repository { get; }
    public ArmySession Session { get; }
    public GrimList.Core.DatasetSnapshot? Snapshot => Session.Snapshot;
    public LibraryWorkflow Library { get; }
    public ArmyWorkspace(IArmyRepository repository, IPdfTextExtractor? pdf = null, LoadedLibrary? loadedLibrary = null,
        bool useFactionLoading = false, Func<IArmyRepository, LoadedLibrary>? libraryLoader = null, string libraryVariant = "")
    {
        Repository = repository; Session = new(repository, loadedLibrary: loadedLibrary, useFactionLoading: useFactionLoading,
            libraryLoader: libraryLoader, libraryVariant: libraryVariant); Library = new(repository, pdf, libraryVariant);
    }
    public static Task<ArmyWorkspace> OpenAsync(Func<IArmyRepository> open, CancellationToken cancellation = default) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var repository = open();
        try { var workspace = new ArmyWorkspace(repository); workspace.Session.Restore(); return workspace; }
        catch { repository.Dispose(); throw; }
    }, cancellation);
    public async Task ApplyLibraryAsync(LibraryPreview preview, CancellationToken cancellation = default, IProgress<string>? progress = null)
    {
        progress?.Report("Saving update · Saving points, datasheets and rules…");
        await Library.ApplyAsync(preview, cancellation).ConfigureAwait(false);
        // Once committed, refresh even if the caller cancels. Never publish a stale session.
        try
        {
            progress?.Report("Finishing update · Rebuilding the army library…");
            await Session.RunAsync(s => s.ReloadLibrary()).ConfigureAwait(false);
        }
        catch (Exception error) { throw new LibraryRefreshException(error); }
    }
    public void Dispose() => Repository.Dispose();
}
