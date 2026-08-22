using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;

namespace Post.Core.Infrastructure.ObjectStorage;

public sealed record S3PresignerOptions
{
    public required Uri Endpoint { get; init; }

    public required string Region { get; init; }

    public required string Bucket { get; init; }

    public required string AccessKey { get; init; }

    public required string SecretKey { get; init; }

    public bool ForcePathStyle { get; init; } = true;

    public bool RequireHttps { get; init; }

    public bool RequireServerSideEncryption { get; init; }
}

public sealed class S3SignatureV4Presigner : IS3Presigner
{
    private const string Service = "s3";
    private const string ServerSideEncryptionHeader = "x-amz-server-side-encryption";
    private const string ServerSideEncryptionAlgorithm = "AES256";
    private static readonly IReadOnlyDictionary<string, string> NoRequiredHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> ServerSideEncryptionHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServerSideEncryptionHeader] = ServerSideEncryptionAlgorithm
        };
    private readonly S3PresignerOptions _options;

    public S3SignatureV4Presigner(S3PresignerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public PresignedUrlResult CreateUploadUrl(
        string objectKey,
        string contentType,
        long contentLength,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            objectKey.StartsWith('/') ||
            objectKey.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("A relative object key is required.", nameof(objectKey));
        }

        var normalizedContentType = AttachmentMetadataRules.NormalizeContentType(contentType);

        if (contentLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength), "Content length must be positive.");
        }

        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "S3 presigned URLs must expire within seven days.");
        }

        var utcNow = now.ToUniversalTime();
        var dateStamp = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var credentialScope = string.Join('/', dateStamp, _options.Region, Service, "aws4_request");
        var builder = CreateTarget(objectKey);
        var host = builder.Uri.IsDefaultPort
            ? builder.Host.ToLowerInvariant()
            : string.Concat(builder.Host.ToLowerInvariant(), ":", builder.Port.ToString(CultureInfo.InvariantCulture));
        var signedHeaders = _options.RequireServerSideEncryption
            ? "content-length;content-type;host;x-amz-server-side-encryption"
            : "content-length;content-type;host";
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = string.Concat(_options.AccessKey, "/", credentialScope),
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = ((long)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = signedHeaders
        };
        var canonicalQuery = string.Join('&', query.Select(x => string.Concat(AwsEncode(x.Key), "=", AwsEncode(x.Value))));
        var canonicalHeaders = string.Concat(
            "content-length:", contentLength.ToString(CultureInfo.InvariantCulture), "\n",
            "content-type:", normalizedContentType, "\n",
            "host:", host, "\n",
            _options.RequireServerSideEncryption
                ? string.Concat(ServerSideEncryptionHeader, ":", ServerSideEncryptionAlgorithm, "\n")
                : string.Empty);
        var canonicalRequest = string.Join(
            '\n',
            "PUT",
            builder.Path,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            "UNSIGNED-PAYLOAD");
        var stringToSign = string.Join(
            '\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            HexSha256(canonicalRequest));
        var signature = Convert.ToHexStringLower(Sign(stringToSign, dateStamp));
        builder.Query = string.Concat(canonicalQuery, "&X-Amz-Signature=", signature);
        return new PresignedUrlResult(
            objectKey,
            builder.Uri,
            utcNow + lifetime,
            _options.RequireServerSideEncryption ? ServerSideEncryptionHeaders : NoRequiredHeaders);
    }

    private UriBuilder CreateTarget(string objectKey)
    {
        var endpoint = new UriBuilder(_options.Endpoint);
        var encodedObjectKey = string.Join('/', objectKey.Split('/').Select(AwsEncode));
        var endpointPath = endpoint.Path.TrimEnd('/');
        if (_options.ForcePathStyle)
        {
            endpoint.Path = string.Concat(endpointPath, "/", AwsEncode(_options.Bucket), "/", encodedObjectKey);
        }
        else
        {
            endpoint.Host = string.Concat(_options.Bucket, ".", endpoint.Host);
            endpoint.Path = string.Concat(endpointPath, "/", encodedObjectKey);
        }

        return endpoint;
    }

    private byte[] Sign(string stringToSign, string dateStamp)
    {
        var dateKey = Hmac(Encoding.UTF8.GetBytes(string.Concat("AWS4", _options.SecretKey)), dateStamp);
        var regionKey = Hmac(dateKey, _options.Region);
        var serviceKey = Hmac(regionKey, Service);
        var signingKey = Hmac(serviceKey, "aws4_request");
        return Hmac(signingKey, stringToSign);
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string HexSha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string AwsEncode(string value) =>
        Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);

    private static void ValidateOptions(S3PresignerOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri ||
            !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("S3 endpoint must be an absolute HTTP or HTTPS URI.", nameof(options));
        }

        if (!string.IsNullOrEmpty(options.Endpoint.Query) ||
            !string.IsNullOrEmpty(options.Endpoint.Fragment) ||
            !string.IsNullOrEmpty(options.Endpoint.UserInfo))
        {
            throw new ArgumentException("S3 endpoint cannot contain user information, a query, or a fragment.", nameof(options));
        }

        if (options.RequireHttps &&
            !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("S3 endpoint must use HTTPS in this environment.", nameof(options));
        }

        if (options.RequireHttps && options.Endpoint.IsLoopback)
        {
            throw new ArgumentException("Production S3 endpoint cannot be loopback.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Region) || string.IsNullOrWhiteSpace(options.Bucket) ||
            string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
        {
            throw new ArgumentException("S3 region, bucket, access key, and secret key are required.", nameof(options));
        }
    }
}
