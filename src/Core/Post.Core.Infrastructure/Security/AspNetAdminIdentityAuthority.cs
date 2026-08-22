using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Security;
using Post.Core.Infrastructure.Persistence;

namespace Post.Core.Infrastructure.Security;

public sealed class AspNetAdminIdentityAuthority : IAdminIdentityAuthority
{
    private const string DummyPassword = "dummy-fixed-for-timing-equalization";

    private readonly UserManager<AppUser> _userManager;
    private readonly RoleManager<AppRole> _roleManager;
    private readonly IPasswordHasher<AppUser> _passwordHasher;
    private readonly IdentityDbContext _dbContext;
    private readonly AppUser _dummyUser;
    private readonly string _dummyPasswordHash;

    public AspNetAdminIdentityAuthority(
        UserManager<AppUser> userManager,
        RoleManager<AppRole> roleManager,
        IPasswordHasher<AppUser> passwordHasher,
        IdentityDbContext dbContext)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _passwordHasher = passwordHasher;
        _dbContext = dbContext;
        _dummyUser = new AppUser
        {
            Id = Guid.Empty,
            UserName = "missing-user-timing-subject",
            IsActive = false
        };
        _dummyPasswordHash = _passwordHasher.HashPassword(_dummyUser, DummyPassword);
    }

    public async Task EnsureRolesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var role in Enum.GetValues<AdminRole>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roleName = role.ToString();
            if (await _roleManager.RoleExistsAsync(roleName).ConfigureAwait(false))
            {
                continue;
            }

            var result = await _roleManager.CreateAsync(new AppRole
            {
                Id = Guid.NewGuid(),
                Name = roleName
            }).ConfigureAwait(false);
            if (!result.Succeeded && !await _roleManager.RoleExistsAsync(roleName).ConfigureAwait(false))
            {
                throw IdentityFailure("Administrative role seeding failed", result);
            }
        }
    }

    public Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default) =>
        _dbContext.Users.AsNoTracking().AnyAsync(cancellationToken);

    public async Task<AdminIdentityProfile?> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        cancellationToken.ThrowIfCancellationRequested();

        var user = await _userManager.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            _ = _passwordHasher.VerifyHashedPassword(_dummyUser, _dummyPasswordHash, password);
            return null;
        }

        if (!await _userManager.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var roleNames = await _userManager.GetRolesAsync(user).ConfigureAwait(false);
        var roles = roleNames
            .Select(ParsePersistedRole)
            .OrderBy(x => x)
            .ToArray();
        var centers = await _dbContext.UserAllowedCenters
            .AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .OrderBy(x => x.ExchangeCenterCode)
            .Select(x => x.ExchangeCenterCode)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new AdminIdentityProfile(
            user.Id,
            user.UserName ?? throw new InvalidOperationException("Persisted Identity user name is missing."),
            user.IsActive,
            roles,
            centers);
    }

    public async Task ProvisionInitialAsync(
        InitialAdminIdentity identity,
        AuditRecord auditRecord,
        OutboxMessage outboxMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(auditRecord);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        await EnsureRolesAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            _dbContext.IdentityBootstrapLocks.Add(new IdentityBootstrapLock
            {
                Id = IdentityBootstrapLock.SingletonId,
                UserId = identity.UserId,
                CompletedAt = auditRecord.OccurredAt
            });
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var user = new AppUser
            {
                Id = identity.UserId,
                UserName = identity.UserName,
                IsActive = true
            };
            var createResult = await _userManager.CreateAsync(user, identity.Password).ConfigureAwait(false);
            ThrowIfIdentityFailed("Initial administrator creation failed", createResult);

            var roleResult = await _userManager.AddToRolesAsync(
                user,
                identity.Roles.Select(x => x.ToString())).ConfigureAwait(false);
            ThrowIfIdentityFailed("Initial administrator role assignment failed", roleResult);

            _dbContext.UserAllowedCenters.AddRange(identity.AllowedCenterCodes.Select(center =>
                new UserAllowedCenter
                {
                    UserId = user.Id,
                    ExchangeCenterCode = center
                }));
            _dbContext.AuditRecords.Add(auditRecord);
            _dbContext.OutboxMessages.Add(outboxMessage);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (RelationalConstraintClassifier.IsUniqueViolation(exception))
        {
            throw new CoreConflictException(
                "Initial administrator provisioning is permanently closed after the first committed bootstrap.",
                exception);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException(
                "Initial administrator provisioning is permanently closed after the first committed bootstrap.",
                exception);
        }
    }

    private static AdminRole ParsePersistedRole(string value) =>
        Enum.TryParse<AdminRole>(value, ignoreCase: false, out var role) &&
        string.Equals(role.ToString(), value, StringComparison.Ordinal)
            ? role
            : throw new InvalidOperationException($"Persisted administrative role '{value}' is not recognized.");

    private static void ThrowIfIdentityFailed(string operation, IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var duplicate = result.Errors.Any(error =>
            error.Code is "DuplicateUserName" or "DuplicateRoleName");
        if (duplicate)
        {
            throw new CoreConflictException(string.Concat(operation, ": an Identity record already exists."));
        }

        throw IdentityFailure(operation, result);
    }

    private static CoreValidationException IdentityFailure(string operation, IdentityResult result)
    {
        var codes = string.Join(", ", result.Errors.Select(x => x.Code).Order(StringComparer.Ordinal));
        return new CoreValidationException(string.Concat(operation, ". Identity errors: ", codes, "."));
    }
}
