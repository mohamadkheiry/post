namespace Post.BuildingBlocks.Correlation;

/// <summary>Provides the current correlation id across asynchronous calls.</summary>
public interface ICorrelationContext
{
    /// <summary>Current correlation id, if a scope is active.</summary>
    string? Current { get; }

    /// <summary>Starts a nested scope and restores the previous value on disposal.</summary>
    IDisposable Push(string correlationId);

    /// <summary>Gets the current identifier or creates and stores one.</summary>
    string GetOrCreate();
}

/// <summary>AsyncLocal correlation context with correctly nested scopes.</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private readonly AsyncLocal<ContextHolder?> _holder = new();

    /// <inheritdoc />
    public string? Current => _holder.Value?.Value;

    /// <inheritdoc />
    public IDisposable Push(string correlationId)
    {
        if (!CorrelationId.IsValid(correlationId))
        {
            throw new ArgumentException("Correlation id contains unsafe characters or has an invalid length.", nameof(correlationId));
        }

        var previous = _holder.Value;
        _holder.Value = new ContextHolder(correlationId.Trim());
        return new Scope(this, previous);
    }

    /// <inheritdoc />
    public string GetOrCreate()
    {
        if (Current is not null)
        {
            return Current;
        }

        var created = CorrelationId.NormalizeOrCreate(null);
        _holder.Value = new ContextHolder(created);
        return created;
    }

    private sealed record ContextHolder(string Value);

    private sealed class Scope(CorrelationContext owner, ContextHolder? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner._holder.Value = previous;
            _disposed = true;
        }
    }
}
