using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Security;
using Post.Core.Domain.ValueObjects;
using Post.Core.Infrastructure.Persistence;

namespace Post.Core.Infrastructure.Tests;

public sealed class AspNetAdminIdentityAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task AuthorityPersistsIdentityRolesCentersAndPasswordAcrossRestart()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            string.Concat("post-identity-", Guid.NewGuid().ToString("N"), ".db"));
        try
        {
            var userId = Guid.NewGuid();
            await using (var provider = CreateProvider(databasePath))
            {
                await using var scope = provider.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                var authority = scope.ServiceProvider.GetRequiredService<IAdminIdentityAuthority>();
                await authority.EnsureRolesAsync(TestContext.Current.CancellationToken);
                await authority.ProvisionInitialAsync(
                    new InitialAdminIdentity(
                        userId,
                        "admin",
                        "correct horse battery staple",
                        [AdminRole.SystemAdministrator, AdminRole.CenterAdministrator],
                        ["C1", "C2"]),
                    AuditRecord.Create(
                        "system-bootstrap",
                        "admin.initial-provision",
                        "AppUser",
                        userId.ToString("D"),
                        null,
                        new CorrelationId("identity-authority"),
                        Now),
                    OutboxMessage.Create(
                        "identity.admin-provisioned",
                        "{}",
                        new CorrelationId("identity-authority"),
                        Now),
                    TestContext.Current.CancellationToken);

                var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
                foreach (var role in Enum.GetValues<AdminRole>())
                {
                    Assert.True(await roleManager.RoleExistsAsync(role.ToString()));
                }

                var stored = await context.Users.AsNoTracking().SingleAsync(
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(userId, stored.Id);
                Assert.NotNull(stored.PasswordHash);
                Assert.StartsWith("AQAAAA", stored.PasswordHash, StringComparison.Ordinal);
                Assert.DoesNotContain("correct horse", stored.PasswordHash, StringComparison.Ordinal);
                Assert.Single(await context.IdentityBootstrapLocks.AsNoTracking().ToArrayAsync(
                    TestContext.Current.CancellationToken));
                Assert.Single(await context.AuditRecords.AsNoTracking().ToArrayAsync(
                    TestContext.Current.CancellationToken));
                Assert.Single(await context.OutboxMessages.AsNoTracking().ToArrayAsync(
                    TestContext.Current.CancellationToken));
            }

            await using (var restartedProvider = CreateProvider(databasePath))
            {
                await using var scope = restartedProvider.CreateAsyncScope();
                var authority = scope.ServiceProvider.GetRequiredService<IAdminIdentityAuthority>();
                var profile = await authority.AuthenticateAsync(
                    "admin",
                    "correct horse battery staple",
                    TestContext.Current.CancellationToken);

                Assert.NotNull(profile);
                Assert.Equal(userId, profile.UserId);
                Assert.Equal(
                    [AdminRole.SystemAdministrator, AdminRole.CenterAdministrator],
                    profile.Roles);
                Assert.Equal(["C1", "C2"], profile.AllowedCenterCodes);
                Assert.Null(await authority.AuthenticateAsync(
                    "admin",
                    "wrong password",
                    TestContext.Current.CancellationToken));
                Assert.Null(await authority.AuthenticateAsync(
                    "missing",
                    "wrong password",
                    TestContext.Current.CancellationToken));
                Assert.True(await authority.HasAnyUserAsync(TestContext.Current.CancellationToken));

                await Assert.ThrowsAsync<CoreConflictException>(() => authority.ProvisionInitialAsync(
                    new InitialAdminIdentity(
                        Guid.NewGuid(),
                        "second-admin",
                        "another correct horse battery staple",
                        [AdminRole.SystemAdministrator],
                        []),
                    AuditRecord.Create(
                        "system-bootstrap",
                        "admin.initial-provision",
                        "AppUser",
                        Guid.NewGuid().ToString("D"),
                        null,
                        new CorrelationId("identity-authority-duplicate"),
                        Now),
                    OutboxMessage.Create(
                        "identity.admin-provisioned",
                        "{}",
                        new CorrelationId("identity-authority-duplicate"),
                        Now),
                    TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task InvalidIdentityUserNameReturnsStableValidationCodesWithoutSecretMaterial()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            string.Concat("post-identity-invalid-", Guid.NewGuid().ToString("N"), ".db"));
        const string passwordMarker = "identity-password-must-not-leak";
        try
        {
            await using var provider = CreateProvider(databasePath);
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var authority = scope.ServiceProvider.GetRequiredService<IAdminIdentityAuthority>();

            var exception = await Assert.ThrowsAsync<CoreValidationException>(() =>
                authority.ProvisionInitialAsync(
                    new InitialAdminIdentity(
                        Guid.NewGuid(),
                        "invalid user name!",
                        passwordMarker,
                        [AdminRole.SystemAdministrator],
                        []),
                    AuditRecord.Create(
                        "system-bootstrap",
                        "admin.initial-provision",
                        "AppUser",
                        Guid.NewGuid().ToString("D"),
                        null,
                        new CorrelationId("identity-invalid-user"),
                        Now),
                    OutboxMessage.Create(
                        "identity.admin-provisioned",
                        "{}",
                        new CorrelationId("identity-invalid-user"),
                        Now),
                    TestContext.Current.CancellationToken));

            Assert.Contains("InvalidUserName", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(passwordMarker, exception.ToString(), StringComparison.Ordinal);
            Assert.Empty(await context.Users.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static ServiceProvider CreateProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddCoreInfrastructure(
            new ConfigurationManager(),
            new CoreInfrastructureOptions
            {
                Environment = CoreInfrastructureEnvironment.Testing,
                ServiceProfile = CoreServiceProfile.Identity,
                DatabaseProvider = CoreDatabaseProvider.Sqlite,
                IdentityConnectionString = string.Concat("Data Source=", databasePath)
            });
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
