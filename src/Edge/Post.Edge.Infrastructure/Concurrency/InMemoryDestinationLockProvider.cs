using System.Collections.Concurrent;
using Post.Edge.Application.Ports;

namespace Post.Edge.Infrastructure.Concurrency;

public sealed class InMemoryDestinationLockProvider : IDestinationLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async ValueTask<IDestinationLock> AcquireAsync(
        string deviceId,
        string destinationCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device id is required.", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(destinationCode)) throw new ArgumentException("Destination is required.", nameof(destinationCode));
        var key = $"{deviceId.Trim()}:{destinationCode.Trim()}";
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDestinationLock
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
