namespace Lumen.Core;

/// <summary>Well-known on-disk locations for application state.</summary>
public static class AppPaths
{
    /// <summary>
    /// Root data directory (%LocalAppData%\Lumen). The LUMEN_DATA_ROOT environment variable
    /// overrides it so diagnostic gates can run hermetically on a machine whose default root
    /// holds a real library.
    /// </summary>
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("LUMEN_DATA_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen");

    /// <summary>Path of the SQLite database file.</summary>
    public static string DatabasePath => Path.Combine(DataRoot, "lumen.db");

    /// <summary>Directory for rolling log files.</summary>
    public static string LogsDir => Path.Combine(DataRoot, "logs");

    /// <summary>Root cache directory.</summary>
    public static string CacheDir => Path.Combine(DataRoot, "cache");

    /// <summary>Disk cache for channel logos and posters.</summary>
    public static string ImageCacheDir => Path.Combine(CacheDir, "images");
}
