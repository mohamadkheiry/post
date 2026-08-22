using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Security;

namespace Post.Core.Application.Tests;

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}

internal sealed class RecordingDurablePublisher : IDurableEventPublisher
{
    private readonly Exception? _exception;

    public RecordingDurablePublisher(Exception? exception = null)
    {
        _exception = exception;
    }

    public List<EventEnvelope> Events { get; } = [];

    public Task<DurablePublishReceipt> PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (_exception is not null)
        {
            return Task.FromException<DurablePublishReceipt>(_exception);
        }

        Events.Add(envelope);
        return Task.FromResult(new DurablePublishReceipt(envelope.EventId, envelope.OccurredAt));
    }
}

internal sealed class RecordingOutboxStore(params OutboxMessage[] messages) : IOutboxStore
{
    public IReadOnlyCollection<OutboxMessage> Messages { get; } = messages;

    public int Saves { get; private set; }

    public Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<OutboxMessage>>(Messages.Take(maximumCount).ToArray());

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingTokenIssuer : ITokenIssuer
{
    public TokenDescriptor? Descriptor { get; private set; }

    public AccessTokenResult Issue(TokenDescriptor descriptor)
    {
        Descriptor = descriptor;
        return new AccessTokenResult("token", descriptor.ExpiresAt);
    }
}

internal sealed class RecordingSecretHasher(ISecretHasher inner) : ISecretHasher
{
    public int HashCalls { get; private set; }

    public int VerifyCalls { get; private set; }

    public SecretVerifier? LastVerifier { get; private set; }

    public SecretVerifier DummyVerifier => inner.DummyVerifier;

    public string GenerateSecret(int byteLength = 32) => inner.GenerateSecret(byteLength);

    public SecretVerifier Hash(string secret, int iterations = 210_000)
    {
        HashCalls++;
        return inner.Hash(secret, iterations);
    }

    public bool Verify(string secret, SecretVerifier verifier)
    {
        VerifyCalls++;
        LastVerifier = verifier;
        return inner.Verify(secret, verifier);
    }
}

internal sealed class RecordingAdminIdentityAuthority(
    AdminIdentityProfile? authenticatedProfile = null,
    string acceptedPassword = "test-correct-horse-battery-staple") : IAdminIdentityAuthority
{
    public int EnsureRolesCalls { get; private set; }

    public int AuthenticationCalls { get; private set; }

    public int ProvisionCalls { get; private set; }

    public string? LastUserName { get; private set; }

    public string? LastPassword { get; private set; }

    public InitialAdminIdentity? ProvisionedIdentity { get; private set; }

    public AuditRecord? ProvisionedAudit { get; private set; }

    public OutboxMessage? ProvisionedOutbox { get; private set; }

    public bool RejectSubsequentProvisioning { get; init; }

    public Task EnsureRolesAsync(CancellationToken cancellationToken = default)
    {
        EnsureRolesCalls++;
        return Task.CompletedTask;
    }

    public Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProvisionCalls > 0 || authenticatedProfile is not null);

    public Task<AdminIdentityProfile?> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        AuthenticationCalls++;
        LastUserName = userName;
        LastPassword = password;
        return Task.FromResult(string.Equals(password, acceptedPassword, StringComparison.Ordinal)
            ? authenticatedProfile
            : null);
    }

    public Task ProvisionInitialAsync(
        InitialAdminIdentity identity,
        AuditRecord auditRecord,
        OutboxMessage outboxMessage,
        CancellationToken cancellationToken = default)
    {
        if (RejectSubsequentProvisioning && ProvisionCalls > 0)
        {
            throw new CoreConflictException("Synthetic concurrent bootstrap conflict.");
        }

        ProvisionCalls++;
        ProvisionedIdentity = identity;
        ProvisionedAudit = auditRecord;
        ProvisionedOutbox = outboxMessage;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingPresigner : IS3Presigner
{
    public string? ObjectKey { get; private set; }

    public string? ContentType { get; private set; }

    public long ContentLength { get; private set; }

    public PresignedUrlResult CreateUploadUrl(
        string objectKey,
        string contentType,
        long contentLength,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        ObjectKey = objectKey;
        ContentType = contentType;
        ContentLength = contentLength;
        return new PresignedUrlResult(
            objectKey,
            new Uri("https://storage.example/upload"),
            now + lifetime,
            new Dictionary<string, string>());
    }
}
