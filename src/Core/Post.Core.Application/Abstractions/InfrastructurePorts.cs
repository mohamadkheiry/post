using Post.Core.Application.Contracts;
using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Security;

namespace Post.Core.Application.Abstractions;

public interface IDurableEventPublisher
{
    Task<DurablePublishReceipt> PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);
}

public interface IRequestHasher
{
    string Hash<T>(T request);
}

public interface ISecretHasher
{
    SecretVerifier DummyVerifier { get; }

    string GenerateSecret(int byteLength = 32);

    SecretVerifier Hash(string secret, int iterations = 210_000);

    bool Verify(string secret, SecretVerifier verifier);
}

public interface IAdminIdentityAuthority
{
    Task EnsureRolesAsync(CancellationToken cancellationToken = default);

    Task<bool> HasAnyUserAsync(CancellationToken cancellationToken = default);

    Task<AdminIdentityProfile?> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default);

    Task ProvisionInitialAsync(
        InitialAdminIdentity identity,
        AuditRecord auditRecord,
        OutboxMessage outboxMessage,
        CancellationToken cancellationToken = default);
}

public interface ITokenIssuer
{
    AccessTokenResult Issue(TokenDescriptor descriptor);
}

public interface IS3Presigner
{
    PresignedUrlResult CreateUploadUrl(
        string objectKey,
        string contentType,
        long contentLength,
        DateTimeOffset now,
        TimeSpan lifetime);
}
