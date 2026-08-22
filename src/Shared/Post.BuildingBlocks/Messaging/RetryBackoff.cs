namespace Post.BuildingBlocks.Messaging;

/// <summary>Calculates retry delays without performing scheduling or I/O.</summary>
public interface IRetryBackoffPolicy
{
    /// <summary>Returns the delay for a one-based attempt number.</summary>
    TimeSpan GetDelay(int attemptNumber, Guid messageId);
}

/// <summary>Bounded exponential backoff with deterministic per-message jitter.</summary>
public sealed class ExponentialBackoffPolicy : IRetryBackoffPolicy
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly double _multiplier;
    private readonly double _jitterRatio;

    /// <summary>Initializes a bounded exponential policy.</summary>
    /// <param name="initialDelay">Delay for attempt one.</param>
    /// <param name="maximumDelay">Hard upper bound after jitter.</param>
    /// <param name="multiplier">Exponential multiplier, at least one.</param>
    /// <param name="jitterRatio">Deterministic jitter from zero through one.</param>
    public ExponentialBackoffPolicy(
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        double multiplier = 2d,
        double jitterRatio = 0.2d)
    {
        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be positive.");
        }

        if (maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay), "Maximum delay cannot be less than initial delay.");
        }

        if (!double.IsFinite(multiplier) || multiplier < 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Multiplier must be finite and at least one.");
        }

        if (!double.IsFinite(jitterRatio) || jitterRatio is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterRatio), "Jitter ratio must be between zero and one.");
        }

        _initialDelay = initialDelay;
        _maximumDelay = maximumDelay;
        _multiplier = multiplier;
        _jitterRatio = jitterRatio;
    }

    /// <inheritdoc />
    public TimeSpan GetDelay(int attemptNumber, Guid messageId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);
        ArgumentOutOfRangeException.ThrowIfEqual(messageId, Guid.Empty);

        var exponent = Math.Min(attemptNumber - 1, 62);
        var unjittered = Math.Min(
            _maximumDelay.TotalMilliseconds,
            _initialDelay.TotalMilliseconds * Math.Pow(_multiplier, exponent));

        var jitterFactor = 1d;
        if (_jitterRatio > 0d)
        {
            var bytes = messageId.ToByteArray();
            uint hash = 2166136261;
            foreach (var value in bytes)
            {
                hash = (hash ^ value) * 16777619;
            }

            var normalized = hash / (double)uint.MaxValue;
            jitterFactor += ((normalized * 2d) - 1d) * _jitterRatio;
        }

        var milliseconds = Math.Clamp(unjittered * jitterFactor, 1d, _maximumDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
