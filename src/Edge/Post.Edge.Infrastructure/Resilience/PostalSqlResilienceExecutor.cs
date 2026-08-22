using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Resilience;

public interface IPostalSqlResilienceExecutor
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        bool retrySafe,
        CancellationToken cancellationToken);
}

public sealed class PostalSqlResilienceExecutor(
    IOptions<EdgeResilienceOptions> options,
    IClock clock) : IPostalSqlResilienceExecutor
{
    private readonly object _sync = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _halfOpenProbeActive;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        bool retrySafe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var halfOpenProbe = AcquireCircuitLease();
        var settings = options.Value;
        var retries = retrySafe ? settings.SqlMaxRetryAttempts : 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await operation(cancellationToken);
                RecordSuccess();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReleaseCancelledProbe(halfOpenProbe);
                throw;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                if (attempt < retries)
                {
                    await Task.Delay(RetryDelay(settings.SqlRetryBaseDelay, attempt), cancellationToken);
                    continue;
                }

                RecordTransientFailure(halfOpenProbe, settings);
                throw;
            }
            catch
            {
                RecordSuccess();
                throw;
            }
        }
    }

    internal static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is TimeoutException ||
               exception is SqlException sql && sql.Errors.Cast<SqlError>().Any(error =>
                   error.Number is -2 or 20 or 64 or 233 or 1_006 or 1_007 or 1_089 or 1_092 or 4_013 or
                       4_053 or 4_060 or 10_053 or 10_054 or 10_060 or 10_061 or 10_064 or 11_001 or 12_020);
    }

    private bool AcquireCircuitLease()
    {
        lock (_sync)
        {
            if (_openUntil is null) return false;
            if (clock.UtcNow < _openUntil)
                throw new InvalidOperationException("Postal SQL resilience circuit is open.");
            if (_halfOpenProbeActive)
                throw new InvalidOperationException("Postal SQL resilience circuit is testing recovery.");
            _halfOpenProbeActive = true;
            return true;
        }
    }

    private void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
            _halfOpenProbeActive = false;
        }
    }

    private void RecordTransientFailure(bool halfOpenProbe, EdgeResilienceOptions settings)
    {
        lock (_sync)
        {
            _halfOpenProbeActive = false;
            _consecutiveFailures++;
            if (!halfOpenProbe && _consecutiveFailures < settings.SqlCircuitFailureThreshold) return;
            _consecutiveFailures = 0;
            _openUntil = clock.UtcNow + settings.SqlCircuitBreakDuration;
        }
    }

    private void ReleaseCancelledProbe(bool halfOpenProbe)
    {
        if (!halfOpenProbe) return;
        lock (_sync)
        {
            _halfOpenProbeActive = false;
        }
    }

    private static TimeSpan RetryDelay(TimeSpan baseDelay, int attempt)
    {
        var exponentialMilliseconds = Math.Min(
            baseDelay.TotalMilliseconds * Math.Pow(2, attempt),
            30_000);
        var jitterMilliseconds = Random.Shared.NextDouble() * baseDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds);
    }
}
