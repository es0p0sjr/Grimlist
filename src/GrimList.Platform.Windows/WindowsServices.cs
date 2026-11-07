using GrimList.Persistence;

namespace GrimList.Platform.Windows;

public sealed class WindowsSqliteConnectionFactory : ISqliteConnectionFactory
{
    public ISqliteConnection Open(string path) => new WindowsSqliteConnection(path);
}

public sealed class WindowsPaths(string? directory = null) : IPlatformPaths
{
    public string DataDirectory { get; } = Path.GetFullPath(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrimList"));
    public string CacheDirectory => Path.Combine(DataDirectory, "sources");
}

public static class WindowsServices
{
    public static ArmyStore OpenStore(string directory) => new(directory, new WindowsSqliteConnectionFactory());
}
