using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Infrastructure.Hosting;

namespace Post.Core.Infrastructure.Tests;

public sealed class EdgeHealthMonitorBackgroundServiceTests
{
    [Fact]
    public async Task SweepUsesConfiguredThresholdAndReportsTransitions()
    {
        TimeSpan? observedThreshold = null;
        var monitor = new DelegateMonitor((threshold, _) =>
        {
            observedThreshold = threshold;
            return Task.FromResult(2);
        });
        var logger = new RecordingLogger<CoreEdgeHealthMonitorBackgroundService>();
        using var provider = CreateProvider(monitor);
        var service = CreateService(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        var result = await service.SweepOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.OfflineTransitions);
        Assert.Equal(TimeSpan.FromMinutes(5), observedThreshold);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 4 && entry.Level == LogLevel.Information);
    }

    [Fact]
    public async Task DependencyFailureIsLoggedAndNextSweepCanContinue()
    {
        const string sensitiveMarker = "private-health-store?credential=sentinel";
        var calls = 0;
        var monitor = new DelegateMonitor((_, _) =>
        {
            calls++;
            return calls == 1
                ? Task.FromException<int>(new CoreDependencyUnavailableException(sensitiveMarker))
                : Task.FromResult(1);
        });
        var logger = new RecordingLogger<CoreEdgeHealthMonitorBackgroundService>();
        using var provider = CreateProvider(monitor);
        var service = CreateService(provider.GetRequiredService<IServiceScopeFactory>(), logger);

        var failure = await service.SweepOnceAsync(TestContext.Current.CancellationToken);
        var recovery = await service.SweepOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(failure.Succeeded);
        Assert.Equal(0, failure.OfflineTransitions);
        Assert.True(recovery.Succeeded);
        Assert.Equal(1, recovery.OfflineTransitions);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 5 && entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains(nameof(CoreDependencyUnavailableException), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(sensitiveMarker, StringComparison.Ordinal) || entry.Exception is not null);
    }

    [Fact]
    public async Task RequestedCancellationIsNotSwallowedAsAHealthFailure()
    {
        var monitor = new DelegateMonitor((_, cancellationToken) =>
            Task.FromCanceled<int>(cancellationToken));
        var logger = new RecordingLogger<CoreEdgeHealthMonitorBackgroundService>();
        using var provider = CreateProvider(monitor);
        var service = CreateService(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SweepOnceAsync(cancellation.Token));
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 5);
    }

    [Fact]
    public async Task HostedLoopRunsAnImmediateSweepAndStopsDuringItsTimerWait()
    {
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new DelegateMonitor((_, _) =>
        {
            swept.TrySetResult();
            return Task.FromResult(0);
        });
        using var provider = CreateProvider(monitor);
        var service = CreateService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new RecordingLogger<CoreEdgeHealthMonitorBackgroundService>());

        await service.StartAsync(CancellationToken.None);
        await swept.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static ServiceProvider CreateProvider(IEdgeHealthMonitor monitor)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => monitor);
        return services.BuildServiceProvider();
    }

    private static CoreEdgeHealthMonitorBackgroundService CreateService(
        IServiceScopeFactory scopeFactory,
        ILogger<CoreEdgeHealthMonitorBackgroundService> logger) =>
        new(
            scopeFactory,
            TimeProvider.System,
            new EdgeHealthMonitorOptions
            {
                CheckInterval = TimeSpan.FromSeconds(30),
                StaleThreshold = TimeSpan.FromMinutes(5)
            },
            logger);

    private sealed class DelegateMonitor(
        Func<TimeSpan, CancellationToken, Task<int>> callback) : IEdgeHealthMonitor
    {
        public Task<int> MarkStaleEdgesOfflineAsync(
            TimeSpan threshold,
            CancellationToken cancellationToken = default) =>
            callback(threshold, cancellationToken);
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
