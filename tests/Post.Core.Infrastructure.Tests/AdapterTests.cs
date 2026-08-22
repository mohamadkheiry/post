using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Confluent.Kafka;
using Microsoft.IdentityModel.Tokens;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Devices;
using Post.Core.Infrastructure.Messaging;
using Post.Core.Infrastructure.ObjectStorage;
using Post.Core.Infrastructure.Security;

namespace Post.Core.Infrastructure.Tests;

public sealed class AdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Pbkdf2HasherGeneratesEntropyAndUsesVerifierOnly()
    {
        var hasher = new Pbkdf2SecretHasher();
        var secret = hasher.GenerateSecret();
        var anotherSecret = hasher.GenerateSecret();
        var verifier = hasher.Hash(secret, 100_000);

        Assert.NotEqual(secret, anotherSecret);
        Assert.True(hasher.Verify(secret, verifier));
        Assert.False(hasher.Verify("wrong", verifier));
        Assert.False(hasher.Verify("candidate", hasher.DummyVerifier));
        Assert.Equal(Pbkdf2SecretHasher.AlgorithmName, verifier.Algorithm);
        Assert.Equal(SecretVerifier.SaltLength, Convert.FromBase64String(verifier.Salt).Length);
        Assert.Equal(SecretVerifier.HashLength, Convert.FromBase64String(verifier.Hash).Length);
        Assert.DoesNotContain(secret, verifier.Hash, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => hasher.GenerateSecret(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => hasher.Hash(secret, 99_999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            hasher.Hash(secret, SecretVerifier.MaximumIterations + 1));
        Assert.Throws<ArgumentException>(() =>
            hasher.Hash(new string('s', AuthenticationInputLimits.DeviceSecretMaximumLength + 1)));
        Assert.False(hasher.Verify(
            new string('s', AuthenticationInputLimits.DeviceSecretMaximumLength + 1),
            verifier));
    }

    [Fact]
    public void Pbkdf2HasherFailsClosedForCorruptedPersistedVerifiers()
    {
        var hasher = new Pbkdf2SecretHasher();
        var canonicalHash = Convert.ToBase64String(new byte[SecretVerifier.HashLength]);
        var canonicalSalt = Convert.ToBase64String(new byte[SecretVerifier.SaltLength]);
        var corruptions = new (string Property, object? Value)[]
        {
            (nameof(SecretVerifier.Algorithm), "PBKDF2-SHA1"),
            (nameof(SecretVerifier.Iterations), SecretVerifier.MinimumIterations - 1),
            (nameof(SecretVerifier.Iterations), SecretVerifier.MaximumIterations + 1),
            (nameof(SecretVerifier.Salt), null),
            (nameof(SecretVerifier.Salt), new string('!', 24)),
            (nameof(SecretVerifier.Salt), Convert.ToBase64String(new byte[SecretVerifier.SaltLength - 1])),
            (nameof(SecretVerifier.Hash), null),
            (nameof(SecretVerifier.Hash), new string('!', 44)),
            (nameof(SecretVerifier.Hash), Convert.ToBase64String(new byte[SecretVerifier.HashLength - 1])),
            (nameof(SecretVerifier.Hash), canonicalHash[..^2] + "B=")
        };

        foreach (var corruption in corruptions)
        {
            var verifier = new SecretVerifier(
                canonicalHash,
                canonicalSalt,
                SecretVerifier.MinimumIterations);
            CorruptVerifier(verifier, corruption.Property, corruption.Value);
            Assert.False(hasher.Verify("candidate", verifier));
        }
    }

    [Fact]
    public void JwtIssuerEmitsSignedStandardClaimsAndRejectsReservedOverrides()
    {
        var issuer = new HmacJwtTokenIssuer(new HmacJwtOptions
        {
            Issuer = "issuer",
            Audience = "audience",
            SigningKey = "01234567890123456789012345678901"
        });
        var token = issuer.Issue(new TokenDescriptor(
            "subject",
            ["Admin"],
            ["C1"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["device_id"] = "D1" },
            Now,
            Now.AddHours(1)));

        var segments = token.Token.Split('.');
        Assert.Equal(3, segments.Length);
        using var payload = JsonDocument.Parse(DecodeBase64Url(segments[1]));
        Assert.Equal("subject", payload.RootElement.GetProperty("sub").GetString());
        Assert.Equal("issuer", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal("D1", payload.RootElement.GetProperty("device_id").GetString());
        Assert.Equal(Now.AddHours(1), token.ExpiresAt);

        Assert.Throws<CoreValidationException>(() => issuer.Issue(new TokenDescriptor(
            "subject", [], [], new Dictionary<string, string> { ["sub"] = "override" }, Now, Now.AddHours(1))));
        Assert.Throws<CoreValidationException>(() => issuer.Issue(new TokenDescriptor(
            "subject", [], [], new Dictionary<string, string>(), Now, Now)));
        Assert.Throws<ArgumentException>(() => new HmacJwtTokenIssuer(new HmacJwtOptions
        {
            Issuer = "i",
            Audience = "a",
            SigningKey = "short"
        }));
    }

    [Fact]
    public void JwtRoundTripUsesRoleClaimAndRejectsExpiredOrMalformedTokens()
    {
        const string key = "01234567890123456789012345678901";
        var issuer = new HmacJwtTokenIssuer(new HmacJwtOptions
        {
            Issuer = "issuer",
            Audience = "audience",
            SigningKey = key
        });
        var now = DateTimeOffset.UtcNow;
        var token = issuer.Issue(new TokenDescriptor(
            "subject", ["Admin", "Operator"], ["C1"], new Dictionary<string, string>(), now, now.AddMinutes(5)));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validation = ValidationParameters(key);

        var principal = handler.ValidateToken(token.Token, validation, out _);
        Assert.True(principal.IsInRole("Admin"));
        Assert.Equal(2, principal.FindAll("role").Count());
        Assert.Equal("C1", principal.FindFirstValue("allowed_center"));
        Assert.Throws<SecurityTokenMalformedException>(() => handler.ValidateToken("not-a-jwt", validation, out _));

        var expired = issuer.Issue(new TokenDescriptor(
            "subject", ["Admin"], [], new Dictionary<string, string>(), now.AddHours(-2), now.AddHours(-1)));
        Assert.Throws<SecurityTokenExpiredException>(() => handler.ValidateToken(expired.Token, validation, out _));
    }

    [Fact]
    public async Task ConfluentConfigurationRequiresDurableIdempotentProducerAndManualConsumerCommit()
    {
        var options = new KafkaTransportOptions { BootstrapServers = "localhost:9092" };
        var producerConfig = KafkaConfigurationFactory.CreateProducer(options);
        var consumerConfig = KafkaConfigurationFactory.CreateConsumer(options, "group");
        Assert.Equal(Acks.All, producerConfig.Acks);
        Assert.True(producerConfig.EnableIdempotence);
        Assert.False(consumerConfig.EnableAutoCommit);
        Assert.False(consumerConfig.EnableAutoOffsetStore);
        Assert.Equal(IsolationLevel.ReadCommitted, consumerConfig.IsolationLevel);
        Assert.Equal(
            KafkaTransportOptions.MaximumSupportedPayloadBytes + 512,
            producerConfig.MessageMaxBytes);
        Assert.Equal(KafkaTransportOptions.MaximumSupportedPayloadBytes, consumerConfig.MaxPartitionFetchBytes);
        Assert.Equal(KafkaTransportOptions.MaximumSupportedPayloadBytes, consumerConfig.FetchMaxBytes);
        Assert.Equal(
            KafkaTransportOptions.MaximumSupportedPayloadBytes + 512,
            consumerConfig.ReceiveMessageMaxBytes);
        Assert.Equal(30_000, producerConfig.MessageTimeoutMs);
        Assert.Equal(10_000, producerConfig.SocketTimeoutMs);
        Assert.Equal(5, producerConfig.MessageSendMaxRetries);

        Assert.Throws<ArgumentNullException>(() => KafkaConfigurationFactory.CreateProducer(null!));
        Assert.Throws<ArgumentException>(() => KafkaConfigurationFactory.CreateProducer(
            new KafkaTransportOptions { BootstrapServers = " " }));
        Assert.Throws<ArgumentException>(() => KafkaConfigurationFactory.CreateProducer(
            new KafkaTransportOptions { BootstrapServers = "localhost:9092", ClientId = " " }));
        Assert.Throws<ArgumentException>(() => KafkaConfigurationFactory.CreateConsumer(options, " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaConfigurationFactory.CreateProducer(
            options with { MaximumPayloadBytes = KafkaTransportOptions.MinimumPayloadBytes - 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaConfigurationFactory.CreateConsumer(
            options with { MaximumPayloadBytes = KafkaTransportOptions.MaximumSupportedPayloadBytes + 1 },
            "group"));
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaConfigurationFactory.CreateProducer(
            options with { DeliveryTimeoutMilliseconds = 121_000 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaConfigurationFactory.CreateProducer(
            options with { MaximumSendRetries = 21 }));
        Assert.Throws<ArgumentException>(() => KafkaConfigurationFactory.CreateProducer(
            new KafkaTransportOptions
            {
                BootstrapServers = "localhost:9092",
                SecurityProtocol = SecurityProtocol.SaslSsl
            }));
        Assert.Throws<ArgumentException>(() => KafkaConfigurationFactory.CreateProducer(
            new KafkaTransportOptions
            {
                BootstrapServers = "localhost:9092",
                SecurityProtocol = SecurityProtocol.SaslSsl,
                SaslMechanism = SaslMechanism.Plain,
                SaslUsername = "user"
            }));
        var sasl = KafkaConfigurationFactory.CreateConsumer(
            new KafkaTransportOptions
            {
                BootstrapServers = "localhost:9092",
                SecurityProtocol = SecurityProtocol.SaslSsl,
                SaslMechanism = SaslMechanism.Plain,
                SaslUsername = "user",
                SaslPassword = Guid.Empty.ToString("N")
            },
            "secure-group");
        Assert.Equal(SecurityProtocol.SaslSsl, sasl.SecurityProtocol);
        Assert.Equal(SaslMechanism.Plain, sasl.SaslMechanism);

        var producer = new ConfluentKafkaProducer(options);
        await Assert.ThrowsAsync<CorePayloadTooLargeException>(() => producer.ProduceAsync(
            "topic",
            "key",
            new byte[KafkaTransportOptions.MaximumSupportedPayloadBytes + 1],
            new Dictionary<string, string>(), cancellationToken: TestContext.Current.CancellationToken));
        producer.Dispose();
        producer.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => producer.ProduceAsync(
            "topic", "key", ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void S3PresignerProducesDeterministicPathStyleSignature()
    {
        var presigner = new S3SignatureV4Presigner(new S3PresignerOptions
        {
            Endpoint = new Uri("http://minio:9000"),
            Region = "us-east-1",
            Bucket = "post-images",
            AccessKey = "access",
            SecretKey = "secret",
            ForcePathStyle = true,
            RequireServerSideEncryption = true
        });

        var result = presigner.CreateUploadUrl("center 1/image.jpg", "image/jpeg", 1234, Now, TimeSpan.FromMinutes(10));
        Assert.Equal("center 1/image.jpg", result.ObjectKey);
        Assert.Equal("/post-images/center%201/image.jpg", result.UploadUrl.AbsolutePath);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", result.UploadUrl.Query, StringComparison.Ordinal);
        Assert.Contains(
            "X-Amz-SignedHeaders=content-length%3Bcontent-type%3Bhost%3Bx-amz-server-side-encryption",
            result.UploadUrl.Query,
            StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=", result.UploadUrl.Query, StringComparison.Ordinal);
        Assert.Equal("AES256", result.RequiredHeaders["x-amz-server-side-encryption"]);
        Assert.Equal(Now.AddMinutes(10), result.ExpiresAt);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            presigner.CreateUploadUrl("x", "image/jpeg", 1234, Now, TimeSpan.FromDays(8)));
        Assert.Throws<ArgumentException>(() =>
            presigner.CreateUploadUrl("../x", "image/jpeg", 1234, Now, TimeSpan.FromMinutes(1)));
        Assert.Throws<CoreValidationException>(() =>
            presigner.CreateUploadUrl(
                "x",
                "image/jpeg\r\nx-amz-meta-test: injected",
                1234,
                Now,
                TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            presigner.CreateUploadUrl("x", "image/jpeg", 0, Now, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => new S3SignatureV4Presigner(new S3PresignerOptions
        {
            Endpoint = new Uri("http://minio:9000"),
            Region = "us-east-1",
            Bucket = "post-images",
            AccessKey = "access",
            SecretKey = "secret",
            RequireHttps = true
        }));
        Assert.Throws<ArgumentException>(() => new S3SignatureV4Presigner(new S3PresignerOptions
        {
            Endpoint = new Uri(string.Concat(
                "https://",
                "synthetic-user",
                ":",
                "synthetic-value",
                "@storage.example")),
            Region = "us-east-1",
            Bucket = "post-images",
            AccessKey = "access",
            SecretKey = "secret",
            RequireHttps = true
        }));
        Assert.Throws<ArgumentException>(() => new S3SignatureV4Presigner(new S3PresignerOptions
        {
            Endpoint = new Uri("https://127.0.0.1:9000"),
            Region = "us-east-1",
            Bucket = "post-images",
            AccessKey = "access",
            SecretKey = "secret",
            RequireHttps = true
        }));
    }

    [Fact]
    public async Task InMemoryPublisherIsIdempotentAndDetectsConflictingReplay()
    {
        var time = new MutableTimeProvider(Now);
        var publisher = new InMemoryDurableEventPublisher(time);
        var id = Guid.NewGuid();
        var envelope = Envelope(id, "payload");

        var first = await publisher.PublishAsync(envelope, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        var replay = await publisher.PublishAsync(envelope, cancellationToken: TestContext.Current.CancellationToken);
        var correlationRetry = await publisher.PublishAsync(
            envelope with { CorrelationId = "new-attempt-correlation" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first, replay);
        Assert.Equal(first, correlationRetry);
        Assert.Single(publisher.Events);
        await Assert.ThrowsAsync<CoreConflictException>(() => publisher.PublishAsync(Envelope(id, "changed"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task KafkaAdapterAcknowledgesOnlyPersistedDelivery()
    {
        var time = new MutableTimeProvider(Now);
        var producer = new RecordingKafkaProducer(persisted: true);
        var publisher = new KafkaDurableEventPublisher(producer, time);
        var envelope = Envelope(Guid.NewGuid(), "payload");

        var receipt = await publisher.PublishAsync(envelope, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(envelope.EventId, receipt.EventId);
        Assert.Equal("corr", producer.Headers!["correlation-id"]);
        Assert.Equal("payload", Encoding.UTF8.GetString(producer.Payload.Span));

        var rejected = new KafkaDurableEventPublisher(new RecordingKafkaProducer(persisted: false), time);
        await Assert.ThrowsAsync<CoreDependencyUnavailableException>(() => rejected.PublishAsync(envelope, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static EventEnvelope Envelope(Guid id, string payload) => new(
        id,
        "topic",
        "key",
        payload,
        "corr",
        Now);

    private static TokenValidationParameters ValidationParameters(string signingKey) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = "issuer",
        ValidateAudience = true,
        ValidAudience = "audience",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = "role"
    };

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Convert.FromBase64String(normalized);
    }

    private static void CorruptVerifier(SecretVerifier verifier, string propertyName, object? value)
    {
        var field = typeof(SecretVerifier).GetField(
            $"<{propertyName}>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"Backing field for {propertyName} was not found.");
        field.SetValue(verifier, value);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class RecordingKafkaProducer(bool persisted) : IKafkaCompatibleProducer
    {
        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public ReadOnlyMemory<byte> Payload { get; private set; }

        public Task<KafkaPublishReceipt> ProduceAsync(
            string topic,
            string key,
            ReadOnlyMemory<byte> payload,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Headers = headers;
            Payload = payload;
            return Task.FromResult(new KafkaPublishReceipt(topic, 0, 1, persisted));
        }
    }

}
