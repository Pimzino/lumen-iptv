namespace Lumen.Core.Abstractions;

/// <summary>Testable source of the current time.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by the system time.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
