namespace Post.BuildingBlocks.Time;

/// <summary>Abstraction over current UTC time.</summary>
public interface IClock
{
    /// <summary>Gets current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by <see cref="TimeProvider.System"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared stateless clock instance.</summary>
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}
