using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Infrastructure.Hosting;
using Post.Core.Infrastructure.Messaging;

namespace Post.Core.Infrastructure.Tests;

public sealed class CoreBackgroundServiceTests
{
    [Fact]
    public async Task OutboxWorkerUsesConfiguredBoundsAndStopsCleanlyDuringIdleDelay()
    {
        var invocation = new TaskCompletionSource<(int BatchSize, int MaximumAttempts)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(new DelegateOutboxPublisher((batchSize, maximumAttempts, _) =>
        {
            invocation.TrySetResult((batchSize, maximumAttempts));
            return Task.FromResult(0);
        }));
        var service = new CoreOutboxBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CoreInfrastructureOptions
            {
                OutboxBatchSize = 7,
                OutboxMaximumAttempts = 3,
                OutboxPollingInterval = TimeSpan.FromMinutes(1)
            },
            NullLogger<CoreOutboxBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var observed = await invocation.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal((7, 3), observed);
    }

    [Fact]
    public async Task OutboxWorkerLogsRecoverableFailureAndRemainsStoppable()
    {
        const string sensitiveMarker = "private-upstream-host?credential=sentinel";
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = CreateProvider(new DelegateOutboxPublisher((_, _, _) =>
        {
            attempted.TrySetResult();
            return Task.FromException<int>(new CoreDependencyUnavailableException(sensitiveMarker));
        }));
        var logger = new RecordingLogger<CoreOutboxBackgroundService>();
        var service = new CoreOutboxBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CoreInfrastructureOptions { OutboxPollingInterval = TimeSpan.FromMinutes(1) },
            logger);

        await service.StartAsync(CancellationToken.None);
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1 && entry.Level == LogLevel.Error);
        Assert.Contains(logger.Entries, entry =>
            entry.Message.Contains(nameof(CoreDependencyUnavailableException), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(sensitiveMarker, StringComparison.Ordinal) ||
            entry.Exception?.ToString().Contains(sensitiveMarker, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task KafkaConsumerRetryDelaysExerciseFirstAttemptCapAndCancellation()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var transport = new KafkaTransportOptions { BootstrapServers = "localhost:9092" };
        var attachment = new CoreAttachmentMetadataConsumerBackgroundService(
            scopeFactory,
            transport,
            new AttachmentMetadataConsumerOptions { FailureRetryDelay = TimeSpan.Zero },
            NullLogger<CoreAttachmentMetadataConsumerBackgroundService>.Instance);
        var parcel = new CoreParcelEventConsumerBackgroundService(
            scopeFactory,
            transport,
            new ParcelEventConsumerOptions { FailureRetryDelay = TimeSpan.Zero },
            NullLogger<CoreParcelEventConsumerBackgroundService>.Instance);

        await InvokeDelayAsync(attachment, 1, CancellationToken.None);
        await InvokeDelayAsync(attachment, 31, CancellationToken.None);
        await InvokeDelayAsync(parcel, 1, CancellationToken.None);
        await InvokeDelayAsync(parcel, 31, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellableAttachment = new CoreAttachmentMetadataConsumerBackgroundService(
            scopeFactory,
            transport,
            new AttachmentMetadataConsumerOptions { FailureRetryDelay = TimeSpan.FromSeconds(1) },
            NullLogger<CoreAttachmentMetadataConsumerBackgroundService>.Instance);
        var cancellableParcel = new CoreParcelEventConsumerBackgroundService(
            scopeFactory,
            transport,
            new ParcelEventConsumerOptions { FailureRetryDelay = TimeSpan.FromSeconds(1) },
            NullLogger<CoreParcelEventConsumerBackgroundService>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeDelayAsync(cancellableAttachment, 1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeDelayAsync(cancellableParcel, 1, cancellation.Token));
    }

    [Fact]
    public async Task KafkaBackgroundLoopsExitCleanlyWhenHostIsAlreadyStopping()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var transport = new KafkaTransportOptions { BootstrapServers = "localhost:9092" };
        var attachment = new CoreAttachmentMetadataConsumerBackgroundService(
            scopeFactory,
            transport,
            new AttachmentMetadataConsumerOptions(),
            NullLogger<CoreAttachmentMetadataConsumerBackgroundService>.Instance);
        var parcel = new CoreParcelEventConsumerBackgroundService(
            scopeFactory,
            transport,
            new ParcelEventConsumerOptions(),
            NullLogger<CoreParcelEventConsumerBackgroundService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await InvokeExecuteAsync(attachment, cancellation.Token);
        await InvokeExecuteAsync(parcel, cancellation.Token);
    }

    private static ServiceProvider CreateProvider(IOutboxPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => publisher);
        return services.BuildServiceProvider();
    }

    private static Task InvokeDelayAsync(object service, int attempt, CancellationToken cancellationToken)
    {
        var method = service.GetType().GetMethod("DelayAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(service, [attempt, cancellationToken]));
    }

    private static Task InvokeExecuteAsync(object service, CancellationToken cancellationToken)
    {
        var method = service.GetType().GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(service, [cancellationToken]));
    }

    private sealed class DelegateOutboxPublisher(
        Func<int, int, CancellationToken, Task<int>> callback) : IOutboxPublisher
    {
        public Task<int> PublishBatchAsync(
            int maximumCount,
            int maximumAttempts,
            CancellationToken cancellationToken = default) =>
            callback(maximumCount, maximumAttempts, cancellationToken);
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
