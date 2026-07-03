namespace Lumen.Data;

/// <summary>
/// Runs repository work on the thread pool. Microsoft.Data.Sqlite has no true async I/O —
/// its async methods complete synchronously — so without this hop an awaited repository
/// call runs the whole query and row materialization inline on the caller's thread,
/// freezing the UI when called from view models. Callers must therefore never rely on a
/// repository call completing synchronously.
/// </summary>
internal static class DbOffload
{
    public static Task Run(Func<Task> work, CancellationToken cancellationToken) =>
        Task.Run(work, cancellationToken);

    public static Task<T> Run<T>(Func<Task<T>> work, CancellationToken cancellationToken) =>
        Task.Run(work, cancellationToken);
}
