using Lumen.Core.Models;

namespace Lumen.Core.Abstractions;

/// <summary>Progress snapshot reported during an EPG import.</summary>
public readonly record struct EpgImportProgress(long Channels, long Programmes);

/// <summary>Outcome of a completed EPG import.</summary>
public sealed record EpgImportResult(long Channels, long Programmes, long Skipped);

/// <summary>
/// Receives a stream of parsed EPG entities. Implementations batch writes (the XMLTV
/// parser pushes millions of programmes) and replace the profile's previous EPG data.
/// </summary>
public interface IEpgImportSink : IAsyncDisposable
{
    /// <summary>Prepares the sink (clears previous EPG data for the profile).</summary>
    Task BeginAsync(CancellationToken cancellationToken);

    ValueTask AddChannelAsync(EpgChannel channel, CancellationToken cancellationToken);

    ValueTask AddProgrammeAsync(Programme programme, CancellationToken cancellationToken);

    /// <summary>Flushes remaining batches and commits.</summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>Creates per-profile EPG import sinks.</summary>
public interface IEpgImportSinkFactory
{
    IEpgImportSink Create(long profileId);
}
