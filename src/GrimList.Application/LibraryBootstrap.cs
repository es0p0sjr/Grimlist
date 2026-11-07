using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform;

namespace GrimList.Application;

public static class LibraryBootstrap
{
    /// <summary>Existing files always bypass bootstrap, including empty legacy libraries.</summary>
    public static ArmyStore Open(IPlatformPaths paths, ISqliteConnectionFactory connections,
        Func<BootstrapRelease>? releaseProvider = null, Action<string>? progress = null)
    {
        var directory = Path.GetFullPath(paths.DataDirectory);
        var target = Path.Combine(directory, "grimlist.db");
        if (File.Exists(target)) return new ArmyStore(directory, connections);
        try
        {
            Directory.CreateDirectory(directory);
            using var gate = AcquireLock(Path.Combine(directory, ".bootstrap.lock"));
            if (!File.Exists(target))
            {
                // Same volume as target: a completed, closed database is atomically renamed.
                // A crash before the rename leaves only disposable staging data.
                var staging = Path.Combine(directory, ".bootstrap-initializing");
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                try
                {
                    progress?.Invoke("Preparing your bundled offline library…");
                    var release = (releaseProvider ?? BootstrapRelease.ReadBundled)();
                    release.Validate();
                    using (var repository = new ArmyStore(staging, connections))
                    {
                        progress?.Invoke("Installing factions, units and reference cards…");
                        var workflow = new LibraryWorkflow(repository);
                        workflow.Apply(workflow.Prepare(release.Dataset, ImportLayer.Combined));
                        progress?.Invoke("Finishing your offline library…");
                        repository.VerifyBootstrap(release.Manifest.Units);
                        repository.RecordBootstrap(release.Manifest.ReleaseId, release.Manifest.Sha256);
                    }
                    var completed = Path.Combine(staging, "grimlist.db");
                    using (var durable = new FileStream(completed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        durable.Flush(flushToDisk: true);
                    File.Move(completed, target, overwrite: false);
                }
                finally
                {
                    // Cleanup failure after a successful rename must not invalidate that library.
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("GrimList could not initialize its bundled offline library. "
                + "Check free space and write access to " + directory + ". Restart to retry, or reinstall if the bundled data is damaged. "
                + "No incomplete library has been installed. Details: " + ex.Message, ex);
        }
        progress?.Invoke("Opening your library…");
        return new ArmyStore(directory, connections);
    }

    private static FileStream AcquireLock(string path)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
        }
    }
}
