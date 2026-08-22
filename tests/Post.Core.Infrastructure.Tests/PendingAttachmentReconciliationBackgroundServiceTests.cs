using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Post.Core.Application.Common;
using Post.Core.Infrastructure.Hosting;

namespace Post.Core.Infrastructure.Tests;

public sealed class PendingAttachmentReconciliationBackgroundServiceTests
{
    [Fact]
    public async Task BatchIsLoggedAndDependencyFailureDoesNotStopLaterRecovery()
    {
        const string sensitiveMarker = "private-reconciler?credential=sentinel";
        var calls = 0;
        var processor = new DelegateProcessor(_ =>
        {
            calls++;
            return calls == 1
                ? Task.FromException<PendingAttachmentReconciliationResult>(
                    new CoreDependencyUnavailableException(sensitiveMarker))
                : Task.FromResult(new PendingAttachmentReconciliationResult(2, 1, 0, 1, 0));
        });
        var logger = new RecordingLogger<CorePendingAttachmentReconciliationBackgroundService>();
        using var provider = CreateProvider(processor);
        var service = CreateService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger);

        Assert.Null(await service.ReconcileOnceAsync(TestContext.Current.CancellationToken));
        var recovered = await service.ReconcileOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(recovered);
        Assert.Equal(2, recovered.Examined);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 7 && entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 6 && entry.Level == LogLevel.Information);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains(nameof(CoreDependencyUnavailableException), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(sensitiveMarker, StringComparison.Ordinal) || entry.Exception is not null);
    }

    [Fact]
    public async Task RequestedCancellationPropagatesWithoutFailureLog()
    {
        var processor = new DelegateProcessor(cancellationToken =>
            Task.FromCanceled<PendingAttachmentReconciliationResult>(cancellationToken));
        var logger = new RecordingLogger<CorePendingAttachmentReconciliationBackgroundService>();
        using var provider = CreateProvider(processor);
        var service = CreateService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReconcileOnceAsync(cancellation.Token));
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 7);
    }

    [Fact]
    public async Task HostedLoopRunsAnImmediateBatchAndStopsDuringItsTimerWait()
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new DelegateProcessor(_ =>
        {
            processed.TrySetResult();
            return Task.FromResult(new PendingAttachmentReconciliationResult(0, 0, 0, 0, 0));
        });
        using var provider = CreateProvider(processor);
        var service = CreateService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new RecordingLogger<CorePendingAttachmentReconciliationBackgroundService>());

        await service.StartAsync(CancellationToken.None);
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static ServiceProvider CreateProvider(IPendingAttachmentReconciliationProcessor processor)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => processor);
        return services.BuildServiceProvider();
    }

    private static CorePendingAttachmentReconciliationBackgroundService CreateService(
        IServiceScopeFactory scopeFactory,
        ILogger<CorePendingAttachmentReconciliationBackgroundService> logger) =>
        new(
            scopeFactory,
            TimeProvider.System,
            new PendingAttachmentReconciliationOptions(),
            logger);

    private sealed class DelegateProcessor(
        Func<CancellationToken, Task<PendingAttachmentReconciliationResult>> callback)
        : IPendingAttachmentReconciliationProcessor
    {
        public Task<PendingAttachmentReconciliationResult> ProcessBatchAsync(
            CancellationToken cancellationToken = default) =>
            callback(cancellationToken);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception), exception));
    }
}
