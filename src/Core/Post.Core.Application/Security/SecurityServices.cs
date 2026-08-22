using System.Text.Json;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Security;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Security;

public sealed class EdgeAuthService : IEdgeAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IIdentityStore _store;
    private readonly ISecretHasher _secretHasher;
    private readonly ITokenIssuer _tokenIssuer;
    private readonly IRequestHasher _requestHasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;

    public EdgeAuthService(
        IIdentityStore store,
        ISecretHasher secretHasher,
        ITokenIssuer tokenIssuer,
        IRequestHasher requestHasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider)
    {
        _store = store;
        _secretHasher = secretHasher;
        _tokenIssuer = tokenIssuer;
        _requestHasher = requestHasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
    }

    public async Task<ProvisionedEdgeCredentialResult> ProvisionAsync(
        ProvisionEdgeCredentialCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        IdempotencyRecord? idempotency = null;
        string? requestHash = null;
        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var key = new IdempotencyKey(command.IdempotencyKey);
            requestHash = _requestHasher.Hash(new
            {
                Operation = "edge.provision",
                command.DeviceId,
                command.LogicalDeviceCode,
                command.ExchangeCenterCode
            });
            idempotency = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
            var decision = _idempotencyService.Evaluate(idempotency, requestHash);
            if (decision == IdempotencyDecision.Replay)
            {
                return Replay(idempotency!);
            }

            if (decision == IdempotencyDecision.InProgress)
            {
                throw new CoreConflictException("Edge provisioning with this idempotency key is still in progress.");
            }

            idempotency = IdempotencyRecord.Start(key, requestHash, _timeProvider.GetUtcNow());
        }

        var logicalCode = new LogicalDeviceCode(command.LogicalDeviceCode);
        if (await _store.FindEdgeCredentialAsync(logicalCode, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new CoreConflictException("An Edge credential already exists for the logical device code.");
        }

        var credential = EdgeCredential.Provision(
            new DeviceId(command.DeviceId),
            logicalCode,
            new ExchangeCenterCode(command.ExchangeCenterCode),
            _secretHasher.Hash(command.DeviceSecret));
        var now = _timeProvider.GetUtcNow();
        var correlationId = new CorrelationId(command.CorrelationId);
        _store.AddEdgeCredential(credential);
        _store.AddAudit(AuditRecord.Create(
            command.Actor,
            "edge.provision",
            nameof(EdgeCredential),
            command.DeviceId.ToString("D"),
            JsonSerializer.Serialize(new
            {
                command.LogicalDeviceCode,
                command.ExchangeCenterCode
            }),
            correlationId,
            now));
        _store.AddOutbox(OutboxMessage.Create(
            "identity.edge-provisioned",
            JsonSerializer.Serialize(new
            {
                command.DeviceId,
                command.LogicalDeviceCode,
                command.ExchangeCenterCode
            }),
            correlationId,
            now));
        if (idempotency is not null)
        {
            idempotency.Complete(
                JsonSerializer.Serialize(new SecretOperationReceipt(command.DeviceId), JsonOptions),
                now);
            _store.AddIdempotency(idempotency);
        }

        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Edge provisioning conflicts with an already committed operation.", exception);
        }

        return new ProvisionedEdgeCredentialResult(command.DeviceId, command.DeviceSecret);
    }

    public async Task<ProvisionedEdgeCredentialResult> RotateSecretAsync(
        RotateEdgeCredentialCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = new IdempotencyKey(command.IdempotencyKey);
        var requestHash = _requestHasher.Hash(new { Operation = "edge.rotate-secret", command.DeviceId });
        var existing = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existing, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            return Replay(existing!);
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("Edge secret rotation with this idempotency key is still in progress.");
        }

        var credential = await _store.FindEdgeCredentialAsync(new DeviceId(command.DeviceId), cancellationToken)
            .ConfigureAwait(false) ?? throw new CoreNotFoundException("Edge credential does not exist.");
        credential.Rotate(_secretHasher.Hash(command.DeviceSecret));
        var now = _timeProvider.GetUtcNow();
        var correlationId = new CorrelationId(command.CorrelationId);
        var idempotency = IdempotencyRecord.Start(key, requestHash, now);
        idempotency.Complete(
            JsonSerializer.Serialize(new SecretOperationReceipt(command.DeviceId), JsonOptions),
            now);
        _store.AddIdempotency(idempotency);
        _store.AddAudit(AuditRecord.Create(
            command.Actor,
            "edge.secret-rotate",
            nameof(EdgeCredential),
            command.DeviceId.ToString("D"),
            null,
            correlationId,
            now));
        _store.AddOutbox(OutboxMessage.Create(
            "identity.edge-secret-rotated",
            JsonSerializer.Serialize(new { command.DeviceId, command.Actor }),
            correlationId,
            now));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Edge secret rotation conflicts with an already committed operation.", exception);
        }

        return new ProvisionedEdgeCredentialResult(command.DeviceId, command.DeviceSecret);
    }

    public async Task<AccessTokenResult> AuthenticateAsync(
        AuthenticateEdgeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateLifetime(command.TokenLifetime);
        if (string.IsNullOrWhiteSpace(command.LogicalDeviceCode) ||
            command.LogicalDeviceCode.Length > AuthenticationInputLimits.LogicalDeviceCodeMaximumLength ||
            string.IsNullOrWhiteSpace(command.DeviceSecret) ||
            command.DeviceSecret.Length > AuthenticationInputLimits.DeviceSecretMaximumLength)
        {
            throw new CoreValidationException("Logical device code or device secret is invalid.");
        }

        var credential = await _store.FindEdgeCredentialAsync(
            new LogicalDeviceCode(command.LogicalDeviceCode),
            cancellationToken).ConfigureAwait(false);
        var secretMatches = _secretHasher.Verify(
            command.DeviceSecret,
            credential?.SecretVerifier ?? _secretHasher.DummyVerifier);
        if (credential is null || !credential.IsActive || !secretMatches)
        {
            throw new CoreAuthorizationException("Edge credentials are invalid or disabled.");
        }

        var now = _timeProvider.GetUtcNow();
        return _tokenIssuer.Issue(new TokenDescriptor(
            credential.DeviceId.Value.ToString("D"),
            ["Edge"],
            [credential.ExchangeCenterCode.Value],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["device_id"] = credential.DeviceId.Value.ToString("D"),
                ["logical_code"] = credential.LogicalDeviceCode.Value
            },
            now,
            now + command.TokenLifetime));
    }

    public async Task DisableAsync(
        Guid deviceId,
        string idempotencyKey,
        string actor,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var key = new IdempotencyKey(idempotencyKey);
        var requestHash = _requestHasher.Hash(new { Operation = "edge.disable", DeviceId = deviceId });
        var existing = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existing, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            return;
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("Edge disablement with this idempotency key is still in progress.");
        }

        var credential = await _store.FindEdgeCredentialAsync(new DeviceId(deviceId), cancellationToken)
            .ConfigureAwait(false) ?? throw new CoreNotFoundException("Edge credential does not exist.");
        credential.Disable();
        var now = _timeProvider.GetUtcNow();
        var idempotency = IdempotencyRecord.Start(key, requestHash, now);
        idempotency.Complete("{}", now);
        _store.AddIdempotency(idempotency);
        _store.AddAudit(AuditRecord.Create(
            actor,
            "edge.disable",
            nameof(EdgeCredential),
            deviceId.ToString("D"),
            null,
            new CorrelationId(correlationId),
            now));
        _store.AddOutbox(OutboxMessage.Create(
            "edge.disabled",
            JsonSerializer.Serialize(new { DeviceId = deviceId, Actor = actor }),
            new CorrelationId(correlationId),
            now));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Edge disablement conflicts with an already committed operation.", exception);
        }
    }

    private static void ValidateLifetime(TimeSpan tokenLifetime)
    {
        if (tokenLifetime <= TimeSpan.Zero)
        {
            throw new CoreValidationException("Token lifetime must be positive.");
        }
    }

    private static ProvisionedEdgeCredentialResult Replay(IdempotencyRecord record)
    {
        var receipt = JsonSerializer.Deserialize<SecretOperationReceipt>(record.ResponseSnapshot, JsonOptions)
            ?? throw new CoreConflictException("Stored Edge credential receipt is invalid.");
        return new ProvisionedEdgeCredentialResult(receipt.DeviceId, null, true, true);
    }

    private sealed record SecretOperationReceipt(Guid DeviceId);
}

public sealed class AdminAuthService : IAdminAuthService
{
    private readonly IAdminIdentityAuthority _identityAuthority;
    private readonly ITokenIssuer _tokenIssuer;
    private readonly TimeProvider _timeProvider;

    public AdminAuthService(
        IAdminIdentityAuthority identityAuthority,
        ITokenIssuer tokenIssuer,
        TimeProvider timeProvider)
    {
        _identityAuthority = identityAuthority;
        _tokenIssuer = tokenIssuer;
        _timeProvider = timeProvider;
    }

    public async Task<AccessTokenResult> AuthenticateAsync(
        AuthenticateAdminCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.TokenLifetime <= TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(command.UserName) ||
            command.UserName.Length > AuthenticationInputLimits.UserNameMaximumLength ||
            string.IsNullOrWhiteSpace(command.Password) ||
            command.Password.Length > AuthenticationInputLimits.PasswordMaximumLength)
        {
            throw new CoreValidationException("User name, password, or token lifetime is invalid.");
        }

        var user = await _identityAuthority.AuthenticateAsync(
            command.UserName.Trim(),
            command.Password,
            cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            throw new CoreAuthorizationException("Administrative credentials are invalid or disabled.");
        }

        var now = _timeProvider.GetUtcNow();
        return _tokenIssuer.Issue(new TokenDescriptor(
            user.UserId.ToString("D"),
            user.Roles.Select(x => x.ToString()).ToArray(),
            user.AllowedCenterCodes,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = user.UserName
            },
            now,
            now + command.TokenLifetime));
    }
}

public sealed class AuditService : IAuditService
{
    private readonly IIdentityStore _store;
    private readonly TimeProvider _timeProvider;

    public AuditService(IIdentityStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task RecordAsync(AuditCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _store.AddAudit(AuditRecord.Create(
            command.Actor,
            command.Action,
            command.ResourceType,
            command.ResourceId,
            command.Details,
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class AdminProvisioningService : IAdminProvisioningService
{
    private readonly IAdminIdentityAuthority _identityAuthority;
    private readonly TimeProvider _timeProvider;

    public AdminProvisioningService(
        IAdminIdentityAuthority identityAuthority,
        TimeProvider timeProvider)
    {
        _identityAuthority = identityAuthority;
        _timeProvider = timeProvider;
    }

    public async Task<ProvisionedAdminResult> ProvisionInitialAsync(
        ProvisionInitialAdminCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.UserName) ||
            command.UserName.Length > AuthenticationInputLimits.UserNameMaximumLength ||
            string.IsNullOrWhiteSpace(command.Password) ||
            command.Password.Length < 12 ||
            command.Password.Length > AuthenticationInputLimits.PasswordMaximumLength)
        {
            throw new CoreValidationException(
                $"Initial administrator requires a user name and a password between 12 and {AuthenticationInputLimits.PasswordMaximumLength} characters.");
        }

        if (command.Roles is null || command.AllowedCenterCodes is null)
        {
            throw new CoreValidationException("Initial administrator roles and allowed centers are required.");
        }

        var roles = command.Roles.Distinct().ToArray();
        if (!roles.Contains(AdminRole.GlobalAdministrator) && !roles.Contains(AdminRole.SystemAdministrator))
        {
            throw new CoreValidationException("Initial administrator must have a global or system administrator role.");
        }

        var centers = command.AllowedCenterCodes
            .Select(x => new ExchangeCenterCode(x).Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var userId = Guid.NewGuid();
        var userName = command.UserName.Trim();
        var now = _timeProvider.GetUtcNow();
        var correlationId = new CorrelationId(command.CorrelationId);
        var audit = AuditRecord.Create(
            "system-bootstrap",
            "admin.initial-provision",
            "AppUser",
            userId.ToString("D"),
            JsonSerializer.Serialize(new { UserName = userName, Roles = roles }),
            correlationId,
            now);
        var outbox = OutboxMessage.Create(
            "identity.admin-provisioned",
            JsonSerializer.Serialize(new { UserId = userId, UserName = userName, Roles = roles }),
            correlationId,
            now);
        await _identityAuthority.ProvisionInitialAsync(
            new InitialAdminIdentity(userId, userName, command.Password, roles, centers),
            audit,
            outbox,
            cancellationToken).ConfigureAwait(false);

        return new ProvisionedAdminResult(userId, userName);
    }
}
