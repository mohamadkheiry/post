using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Post.Edge.Infrastructure.Workers;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Services;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.Common;
using System.Text;

namespace Post.Edge.Application.Tests;

public sealed class IdentityAndDeviceServiceTests
{
    [Fact]
    public void OperationIdentityPrefersStableIdAndMarksCorrelationFallback()
    {
        var stable = Guid.NewGuid();
        var correlation = Guid.NewGuid();

        Assert.Equal(stable, OperationIdentity.Resolve(stable, correlation).Value);
        Assert.False(OperationIdentity.Resolve(stable, correlation).IsBestEffortFallback);
        Assert.True(OperationIdentity.Resolve(null, correlation).IsBestEffortFallback);
        Assert.Throws<ArgumentException>(() => OperationIdentity.Resolve(null, Guid.Empty));
    }

    [Fact]
    public async Task ConfigurationAndDeviceAuthenticationEnforceTokenIpAndSession()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        Assert.True(await host.InvokeAsync<IConfigurationSyncService, bool>(x => x.SyncAsync()));
        Assert.False(await host.InvokeAsync<IConfigurationSyncService, bool>(x => x.SyncAsync()));

        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceConnectionService>();
        var connected = new DeviceConnectedRequest("device-1", "127.0.0.1", "connection-1");
        var sessionId = await service.ConnectedAsync(connected, TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, await service.ConnectedAsync(connected, TestContext.Current.CancellationToken));

        var failed = await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "wrong", "127.0.0.1", "connection-1"), TestContext.Current.CancellationToken);
        var success = await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "test-token", "127.0.0.1", "connection-1"), TestContext.Current.CancellationToken);

        Assert.False(failed.IsSuccess);
        Assert.True(success.IsSuccess);
        Assert.Equal(sessionId, success.SessionId);
        await service.DisconnectedAsync(new DeviceDisconnectedRequest("device-1", "connection-1"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AuthenticationRejectsRemoteIpOutsideBinding()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Edge:Security:AllowUnboundDevices"] = "false"
        });
        await host.InvokeAsync<IConfigurationSyncService>(async x => { await x.SyncAsync(); });
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceConnectionService>();
        await service.ConnectedAsync(new DeviceConnectedRequest("device-1", "127.0.0.1", "connection-unbound"), TestContext.Current.CancellationToken);
        var unbound = await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "test-token", "127.0.0.1", "connection-unbound"), TestContext.Current.CancellationToken);
        Assert.False(unbound.IsSuccess);

        var bindings = scope.ServiceProvider.GetRequiredService<IDeviceNetworkBindingRepository>();
        var bindingResult = await scope.ServiceProvider.GetRequiredService<IDeviceNetworkBindingAdminService>()
            .ReplaceAsync(new ReplaceDeviceNetworkBindingsCommand(
                "device-1", ["127.0.0.1"], "test-actor", Guid.NewGuid().ToString("D")), TestContext.Current.CancellationToken);
        Assert.Single(bindingResult.IpAddresses);
        Assert.False(await scope.ServiceProvider.GetRequiredService<IConfigurationSyncService>().SyncAsync(TestContext.Current.CancellationToken));
        Assert.Single(await bindings.ListAsync("device-1", TestContext.Current.CancellationToken));
        await service.ConnectedAsync(new DeviceConnectedRequest("device-1", "127.0.0.1", "connection-bound"), TestContext.Current.CancellationToken);
        var bound = await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "test-token", "127.0.0.1", "connection-bound"), TestContext.Current.CancellationToken);
        Assert.True(bound.IsSuccess);

        await service.ConnectedAsync(new DeviceConnectedRequest("device-1", "127.0.0.2", "connection-2"), TestContext.Current.CancellationToken);

        var response = await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "test-token", "127.0.0.2", "connection-2"), TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccess);
        Assert.Equal("AUTH_FAILED", response.ErrorCode);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.ServiceProvider.GetRequiredService<IDeviceNetworkBindingAdminService>()
                .ReplaceAsync(new ReplaceDeviceNetworkBindingsCommand(
                    "unknown", ["127.0.0.1"], "test-actor", Guid.NewGuid().ToString("D")), TestContext.Current.CancellationToken));
        Assert.Empty(await bindings.ListAsync("unknown", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfigurationSnapshotDoesNotPersistRawDeviceSecret()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        await host.InvokeAsync<IConfigurationSyncService>(async x => { await x.SyncAsync(); });

        await using var database = new FileStream(
            host.DatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);
        using var buffer = new MemoryStream();
        await database.CopyToAsync(buffer, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("test-token", Encoding.UTF8.GetString(buffer.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationAlwaysExecutesConfiguredOrDummyCredentialVerification()
    {
        var recorder = new RecordingCredentialVerifier();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IDeviceCredentialVerifier>();
            services.AddSingleton<IDeviceCredentialVerifier>(recorder);
        });
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceConnectionService>();

        await service.ConnectedAsync(new DeviceConnectedRequest("unknown", "127.0.0.1", "unknown-before-sync"), TestContext.Current.CancellationToken);
        Assert.False((await service.AuthenticateAsync(new DeviceAuthRequest(
            "unknown", "not-a-token", "127.0.0.1", "unknown-before-sync"), TestContext.Current.CancellationToken)).IsSuccess);

        await scope.ServiceProvider.GetRequiredService<IConfigurationSyncService>().SyncAsync(TestContext.Current.CancellationToken);
        await service.ConnectedAsync(new DeviceConnectedRequest("device-1", "127.0.0.1", "known-wrong"), TestContext.Current.CancellationToken);
        Assert.False((await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "wrong", "127.0.0.1", "known-wrong"), TestContext.Current.CancellationToken)).IsSuccess);
        await service.ConnectedAsync(new DeviceConnectedRequest("unknown", "127.0.0.1", "unknown-after-sync"), TestContext.Current.CancellationToken);
        Assert.False((await service.AuthenticateAsync(new DeviceAuthRequest(
            "unknown", "not-a-token", "127.0.0.1", "unknown-after-sync"), TestContext.Current.CancellationToken)).IsSuccess);

        Assert.Equal([false, false, true, false, false, false], recorder.ConfiguredVerifierCalls);
        Assert.False(new DeviceCredentialVerifier().Verify(null, "not-a-token"));
    }

    [Fact]
    public async Task AuthenticationBoundsOversizedIdentifiersBeforeCredentialWork()
    {
        var recorder = new RecordingCredentialVerifier();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IDeviceCredentialVerifier>();
            services.AddSingleton<IDeviceCredentialVerifier>(recorder);
        });
        await host.InvokeAsync<IConfigurationSyncService>(service => service.SyncAsync());
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceConnectionService>();
        await service.ConnectedAsync(new DeviceConnectedRequest(
            "device-1", "127.0.0.1", "oversized-auth"), TestContext.Current.CancellationToken);

        var response = await service.AuthenticateAsync(new DeviceAuthRequest(
            new string('D', DeviceAuthenticationLimits.MaximumDeviceIdCharacters + 1),
            new string('T', DeviceAuthenticationLimits.MaximumDeviceTokenCharacters + 1),
            "127.0.0.1",
            "oversized-auth"), TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccess);
        Assert.Equal(
            ["invalid-device-secret", "invalid-device-secret"],
            recorder.SuppliedSecrets);
        Assert.Equal([false, false], recorder.ConfiguredVerifierCalls);
    }

    [Fact]
    public void CredentialVerifierRejectsOversizedSecretUsingBoundedSurrogate()
    {
        var verifier = DeviceSecretVerifier.DeriveForDevelopment("valid-token");
        var oversized = new string('x', DeviceAuthenticationLimits.MaximumDeviceTokenCharacters + 1);

        Assert.False(new DeviceCredentialVerifier().Verify(verifier, oversized));
    }

    [Fact]
    public async Task InvalidVerifierSnapshotDoesNotReplaceLastUsableConfiguration()
    {
        var source = new ValidThenUnsafeConfigurationSource();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IConfigurationSource>();
            services.AddSingleton<IConfigurationSource>(source);
        });

        Assert.True(await host.InvokeAsync<IConfigurationSyncService, bool>(service => service.SyncAsync()));
        await Assert.ThrowsAsync<DomainException>(() =>
            host.InvokeAsync<IConfigurationSyncService, bool>(service => service.SyncAsync()));
        var snapshot = await host.InvokeAsync<IDeviceConfigurationRepository, ConfigurationSnapshot?>(repository =>
            repository.GetAsync(default));

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(DeviceSecretVerifier.MinimumIterations, Assert.Single(snapshot.Devices).SecretVerifier.Iterations);
    }

    [Fact]
    public async Task DeviceLifecycleEventsAreDurableRedactedAndReplayAfterCoreOutage()
    {
        var clock = new MutableClock();
        var core = new FailOnceCoreClient();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
            services.RemoveAll<ICoreApiClient>();
            services.AddSingleton<ICoreApiClient>(core);
        });
        await host.InvokeAsync<IConfigurationSyncService>(service => service.SyncAsync());
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceConnectionService>();
        const string connectionId = "redacted-connection-id";
        const string remoteIp = "127.0.0.77";
        await service.ConnectedAsync(new DeviceConnectedRequest("device-1", remoteIp, connectionId), TestContext.Current.CancellationToken);
        Assert.False((await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "test-token-wrong", remoteIp, connectionId), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await service.AuthenticateAsync(new DeviceAuthRequest(
            "device-1", "test-token", remoteIp, connectionId), TestContext.Current.CancellationToken)).IsSuccess);
        await service.DisconnectedAsync(new DeviceDisconnectedRequest("device-1", connectionId), TestContext.Current.CancellationToken);

        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
        var first = await processor.ProcessBatchAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Retried);
        clock.UtcNow = clock.UtcNow.AddSeconds(3);
        var replay = await processor.ProcessBatchAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(1, replay.Completed);

        Assert.Equal(
            [
                "edge.device.authenticated",
                "edge.device.authentication-rejected",
                "edge.device.connected",
                "edge.device.disconnected"
            ],
            core.Published.Select(message => message.MessageType).Order(StringComparer.Ordinal).ToArray());
        var payloads = string.Join('|', core.Published.Select(message => message.PayloadJson));
        Assert.DoesNotContain("test-token", payloads, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteIp, payloads, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionId, payloads, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", core.LastFailureMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync(host.DatabasePath,
            "SELECT COUNT(*) FROM outbox WHERE ProcessedAt IS NULL;"));
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync() ?? -1L);
    }

    private sealed class RecordingCredentialVerifier : IDeviceCredentialVerifier
    {
        public List<bool> ConfiguredVerifierCalls { get; } = [];
        public List<string> SuppliedSecrets { get; } = [];

        public bool Verify(DeviceSecretVerifier? configuredVerifier, string suppliedSecret)
        {
            ConfiguredVerifierCalls.Add(configuredVerifier is not null);
            SuppliedSecrets.Add(suppliedSecret);
            return configuredVerifier?.Verify(suppliedSecret) ?? false;
        }
    }

    private sealed class ValidThenUnsafeConfigurationSource : IConfigurationSource
    {
        private int _calls;

        public Task<EdgeConfigurationDto?> FetchAsync(long? currentVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls++;
            var verifier = DeviceSecretVerifier.DeriveForDevelopment("safe-token");
            return Task.FromResult<EdgeConfigurationDto?>(new EdgeConfigurationDto(
                "59544",
                _calls,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromHours(1),
                [new DeviceConfigurationDto(
                    "device-1",
                    "SORT-1",
                    true,
                    verifier.Algorithm,
                    _calls == 1 ? verifier.Iterations : DeviceSecretVerifier.MaximumIterations + 1,
                    verifier.SaltBase64,
                    verifier.HashBase64)],
                ["59544"]));
        }
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class FailOnceCoreClient : ICoreApiClient
    {
        private bool _failed;
        public List<PublishedCoreMessage> Published { get; } = [];
        public string? LastFailureMessage { get; private set; }

        public Task<CoreInboundLookupResult> LookupInboundAsync(
            CoreInboundLookupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CoreInboundLookupResult(CoreParcelDisposition.Normal, null, null));

        public Task PublishAsync(
            string messageType,
            string payloadJson,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_failed)
            {
                _failed = true;
                LastFailureMessage = "Core is temporarily unavailable.";
                throw new HttpRequestException(LastFailureMessage);
            }
            Published.Add(new PublishedCoreMessage(messageType, payloadJson, idempotencyKey));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedCoreMessage(
        string MessageType,
        string PayloadJson,
        string IdempotencyKey);
}
