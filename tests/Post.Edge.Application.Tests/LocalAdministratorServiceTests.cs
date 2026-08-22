using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;
using Post.Edge.Domain.Devices;

namespace Post.Edge.Application.Tests;

public sealed class LocalAdministratorServiceTests
{
    private static readonly string InitialPassword =
        string.Concat("initial-local-", "administrator-value!");
    private static readonly string RotatedPassword =
        string.Concat("rotated-local-", "administrator-value!");

    [Fact]
    public async Task FirstRunLoginRotationAndExpiryPersistOnlyVerifierAndTokenHash()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));
        await using var host = await CreateHostAsync(clock);
        await InitializeAsync(host, InitialPassword);

        var unknown = await LoginAsync(host, "unknown", InitialPassword, TimeSpan.FromMinutes(5));
        var wrong = await LoginAsync(host, "admin", RotatedPassword, TimeSpan.FromMinutes(5));
        var login = await LoginAsync(host, "admin", InitialPassword, TimeSpan.FromMinutes(5));

        Assert.Null(unknown);
        Assert.Null(wrong);
        Assert.NotNull(login);
        Assert.Equal("admin", await AuthenticateAsync(host, login.AccessToken));

        var rotated = await host.InvokeAsync<ILocalAdministratorService, bool>(service =>
            service.RotatePasswordAsync(new RotateLocalAdministratorPasswordCommand(
                "admin", InitialPassword, RotatedPassword), default));
        Assert.True(rotated);
        Assert.Null(await AuthenticateAsync(host, login.AccessToken));
        Assert.Null(await LoginAsync(host, "admin", InitialPassword, TimeSpan.FromMinutes(5)));

        var rotatedLogin = await LoginAsync(host, "admin", RotatedPassword, TimeSpan.FromMinutes(5));
        Assert.NotNull(rotatedLogin);
        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        Assert.Null(await AuthenticateAsync(host, rotatedLogin.AccessToken));

        var databaseText = await ReadDatabaseTextAsync(host.DatabasePath);
        Assert.DoesNotContain(InitialPassword, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedPassword, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(login.AccessToken, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(rotatedLogin.AccessToken, databaseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameTimestampRotationRejectsSessionCommittedWithOldCredentialVersion()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        await using var host = await CreateHostAsync(clock);
        await InitializeAsync(host, InitialPassword);
        LocalAdministrator previous;
        await using (var before = host.Services.CreateAsyncScope())
            previous = Assert.IsType<LocalAdministrator>(await before.ServiceProvider
                .GetRequiredService<ILocalAdministratorRepository>().GetAsync(TestContext.Current.CancellationToken));

        Assert.True(await host.InvokeAsync<ILocalAdministratorService, bool>(service =>
            service.RotatePasswordAsync(new RotateLocalAdministratorPasswordCommand(
                "admin", InitialPassword, RotatedPassword), default)));

        const string staleRawToken = "stale-racing-session-token";
        await using (var race = host.Services.CreateAsyncScope())
        {
            race.ServiceProvider.GetRequiredService<ILocalAdministratorRepository>().AddSession(
                new LocalAdministratorSession(
                    HashToken(staleRawToken),
                    "admin",
                    previous.CredentialVersion,
                    clock.UtcNow,
                    clock.UtcNow.AddMinutes(5)));
            await race.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Null(await AuthenticateAsync(host, staleRawToken));
    }

    [Fact]
    public async Task CorruptPersistedVerifierFailsClosedWithoutBootstrapResetAndMakesReadinessUnhealthy()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        await using var host = await CreateHostAsync(clock);
        await InitializeAsync(host, InitialPassword);
        await host.InvokeAsync<IConfigurationSyncService>(service => service.SyncAsync());
        await using (var database = new SqliteConnection($"Data Source={host.DatabasePath}"))
        {
            await database.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = database.CreateCommand();
            command.CommandText = "UPDATE local_administrators SET SecretHash = 'corrupt-verifier' WHERE Id = 1";
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InitializeAsync(host, RotatedPassword));
        Assert.Null(await LoginAsync(host, "admin", InitialPassword, TimeSpan.FromMinutes(5)));
        var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, health.Status);

        await using var verification = new SqliteConnection($"Data Source={host.DatabasePath}");
        await verification.OpenAsync(TestContext.Current.CancellationToken);
        await using var query = verification.CreateCommand();
        query.CommandText = "SELECT SecretHash FROM local_administrators WHERE Id = 1";
        Assert.Equal("corrupt-verifier", await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NullRotationFieldsReturnSafeFailure()
    {
        await using var host = await CreateHostAsync(new MutableClock(DateTimeOffset.UnixEpoch));
        await InitializeAsync(host, InitialPassword);

        var result = await host.InvokeAsync<ILocalAdministratorService, bool>(service =>
            service.RotatePasswordAsync(
                new RotateLocalAdministratorPasswordCommand(null!, null!, null!),
                default));

        Assert.False(result);
    }

    private static async Task<EdgeApplicationTestHost> CreateHostAsync(MutableClock clock) =>
        await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });

    private static Task InitializeAsync(EdgeApplicationTestHost host, string password) =>
        host.InvokeAsync<ILocalAdministratorService>(service =>
            service.EnsureInitializedAsync("admin", password, default));

    private static Task<LocalAdministratorLoginResult?> LoginAsync(
        EdgeApplicationTestHost host,
        string userName,
        string password,
        TimeSpan lifetime) => host.InvokeAsync<ILocalAdministratorService, LocalAdministratorLoginResult?>(service =>
            service.LoginAsync(new LocalAdministratorLoginCommand(userName, password), lifetime, default));

    private static Task<string?> AuthenticateAsync(EdgeApplicationTestHost host, string token) =>
        host.InvokeAsync<ILocalAdministratorService, string?>(service =>
            service.AuthenticateSessionAsync(token, default));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<string> ReadDatabaseTextAsync(string databasePath)
    {
        await using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4_096,
            FileOptions.Asynchronous);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
