using System.Net;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Post.BuildingBlocks.Serialization;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Integrations;
using Post.Edge.Infrastructure.Integrations.Core;
using Post.Edge.Infrastructure.Integrations.Destination;
using Post.Edge.Infrastructure.Integrations.Postal;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Tests;

public sealed class SoapAndHttpClientTests
{
    private const string SyntheticDestinationSecret = "secret";
    private const string SensitiveTestSecret = "sensitive-password";

    [Fact]
    public void SoapParserReadsDirectAndNestedResultPayloads()
    {
        const string direct = """
            <Envelope><Body><GetDistributeInfoDataResponse><GetDistributeInfoDataResult>
            <Weight>12.5</Weight><BoxSize>L</BoxSize><PostalComm>42</PostalComm><IsJoof>1</IsJoof>
            </GetDistributeInfoDataResult></GetDistributeInfoDataResponse></Body></Envelope>
            """;
        const string nested = """
            <Envelope><Body><GetDistributeInfoDataResult>&lt;Parcel&gt;&lt;Weight&gt;10&lt;/Weight&gt;&lt;IsJoof&gt;false&lt;/IsJoof&gt;&lt;/Parcel&gt;</GetDistributeInfoDataResult></Body></Envelope>
            """;

        var directResult = PostalSoapParser.Parse(direct);
        var nestedResult = PostalSoapParser.Parse(nested);

        Assert.Equal(12.5m, directResult!.Weight);
        Assert.Equal("L", directResult.BoxSize);
        Assert.True(directResult.IsJoof);
        Assert.Equal(10m, nestedResult!.Weight);
        Assert.False(nestedResult.IsJoof);
    }

    [Fact]
    public void SoapParserThrowsForSoapFaultAndReturnsNullWithoutResult()
    {
        var fault = Assert.Throws<InvalidOperationException>(() => PostalSoapParser.Parse(
            $"<Envelope><Fault><faultstring>{SensitiveTestSecret}</faultstring></Fault></Envelope>"));
        Assert.DoesNotContain(SensitiveTestSecret, fault.ToString(), StringComparison.Ordinal);
        Assert.Null(PostalSoapParser.Parse("<Envelope><Body /></Envelope>"));
    }

    [Fact]
    public void SoapParserProhibitsOuterAndNestedDtdAndEnforcesResponseCap()
    {
        const string outer = "<!DOCTYPE Envelope [<!ENTITY xxe SYSTEM 'file:///sensitive'>]><Envelope><Body>&xxe;</Body></Envelope>";
        const string nested = "<Envelope><Body><GetDistributeInfoDataResult>&lt;!DOCTYPE Parcel [ &lt;!ENTITY xxe SYSTEM 'file:///sensitive'&gt; ]&gt;&lt;Parcel&gt;&amp;xxe;&lt;/Parcel&gt;</GetDistributeInfoDataResult></Body></Envelope>";

        var outerError = Assert.Throws<InvalidOperationException>(() => PostalSoapParser.Parse(outer));
        var nestedError = Assert.Throws<InvalidOperationException>(() => PostalSoapParser.Parse(nested));
        var oversized = Assert.Throws<InvalidOperationException>(() =>
            PostalSoapParser.Parse(new string('x', 65), 64));

        Assert.DoesNotContain("file:///sensitive", outerError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("file:///sensitive", nestedError.ToString(), StringComparison.Ordinal);
        Assert.Contains("size limit", oversized.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentOutOfRangeException>(() => PostalSoapParser.Parse("<Envelope />", 0));
    }

    [Fact]
    public async Task DestinationClientCachesTokenFindsNestedCityAndPadsIt()
    {
        var handler = new QueueHttpHandler([
            Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\",\"expires_in\":3600}"),
            Json(HttpStatusCode.OK, "{\"data\":{\"ToCityID\":123}}"),
            Json(HttpStatusCode.OK, "{\"ToCityID\":\"45\"}")
        ]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var settings = Microsoft.Extensions.Options.Options.Create(new DestinationApiOptions
        {
            Username = "user",
            Password = SyntheticDestinationSecret,
            RefreshSkew = TimeSpan.Zero
        });
        using var cache = new DestinationTokenCache(http, settings, new FixedClock());
        var client = new HttpDestinationLookupClient(http, cache, settings);

        Assert.Equal("00123", await client.ResolveAsync("12345678901234", TestContext.Current.CancellationToken));
        Assert.Equal("00045", await client.ResolveAsync("12345678901235", TestContext.Current.CancellationToken));
        Assert.Equal(3, handler.RequestCount);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ResolveAsync("invalid", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DestinationTokenAndLookupResponsesAreBoundedWithoutLeakingBodies()
    {
        const string sensitiveBody =
            "sensitive-upstream-body-sensitive-upstream-body-sensitive-upstream-body";
        var tokenHandler = new QueueHttpHandler([
            Json(HttpStatusCode.OK, $$"""{"access_token":"{{sensitiveBody}}"}""")
        ]);
        using var tokenHttp = new HttpClient(tokenHandler) { BaseAddress = new Uri("https://destination.test") };
        var limited = Microsoft.Extensions.Options.Options.Create(new DestinationApiOptions
        {
            Username = "user",
            Password = SyntheticDestinationSecret,
            MaximumResponseBytes = 8
        });
        using var limitedCache = new DestinationTokenCache(tokenHttp, limited, new FixedClock());

        var tokenError = await Assert.ThrowsAsync<InvalidOperationException>(() => limitedCache.GetAsync(TestContext.Current.CancellationToken));

        var lookupHandler = new QueueHttpHandler([
            Json(HttpStatusCode.OK, "{\"access_token\":\"token\",\"expires_in\":3600}"),
            Json(HttpStatusCode.OK, $$"""{"value":"{{sensitiveBody}}"}""")
        ]);
        using var lookupHttp = new HttpClient(lookupHandler) { BaseAddress = new Uri("https://destination.test") };
        var lookupOptions = Microsoft.Extensions.Options.Options.Create(new DestinationApiOptions
        {
            Username = "user",
            Password = SyntheticDestinationSecret,
            MaximumResponseBytes = 48,
            RefreshSkew = TimeSpan.Zero
        });
        using var lookupCache = new DestinationTokenCache(lookupHttp, lookupOptions, new FixedClock());
        var lookupClient = new HttpDestinationLookupClient(lookupHttp, lookupCache, lookupOptions);
        var lookupError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lookupClient.ResolveAsync("12345678901234", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(sensitiveBody, tokenError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveBody, lookupError.ToString(), StringComparison.Ordinal);
        Assert.Contains("size limit", tokenError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("size limit", lookupError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutboundAdaptersHonorBoundedHttpTimeoutCancellation()
    {
        using var destinationHttp = new HttpClient(new DelayedHttpHandler())
        {
            BaseAddress = new Uri("https://destination.test"),
            Timeout = TimeSpan.FromMilliseconds(25)
        };
        var destinationOptions = Microsoft.Extensions.Options.Options.Create(new DestinationApiOptions
        {
            Username = "user",
            Password = SyntheticDestinationSecret
        });
        using var destinationTokens = new DestinationTokenCache(
            destinationHttp,
            destinationOptions,
            new FixedClock());

        using var coreHttp = new HttpClient(new DelayedHttpHandler())
        {
            BaseAddress = new Uri("https://core.test"),
            Timeout = TimeSpan.FromMilliseconds(25)
        };
        var coreOptions = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            LogicalDeviceCode = "edge-01",
            DeviceSecret = "test-credential"
        });
        using var coreTokens = new CoreAccessTokenProvider(coreHttp, coreOptions, new FixedClock());

        using var soapHttp = new HttpClient(new DelayedHttpHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(25)
        };
        var soap = new PostalSoapClient(soapHttp, Microsoft.Extensions.Options.Options.Create(new PostalSoapOptions
        {
            Endpoint = "https://postal.test/soap"
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destinationTokens.GetAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coreTokens.GetAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            soap.GetAsync("59544", "123", 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CoreClientMapsDispositionAndSendsIdempotencyHeader()
    {
        var handler = new QueueHttpHandler([
            Json(HttpStatusCode.OK, "{\"deviceStatus\":\"Returned\",\"destinationCenterCode\":\"00123\",\"readingRecordId\":\"22222222-2222-2222-2222-222222222222\",\"discrepancies\":[{\"attribute\":\"Weight\"}]}"),
            Json(HttpStatusCode.Accepted, "{}"),
            Json(HttpStatusCode.Accepted, "{}"),
            Json(HttpStatusCode.Accepted, "{}"),
            Json(HttpStatusCode.Accepted, "{}"),
            Json(HttpStatusCode.Created, "{}"),
            Json(HttpStatusCode.NoContent, "{}"),
            Json(HttpStatusCode.NoContent, "{}")
        ]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        var options = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            UseFixedBearerToken = true,
            BearerToken = "edge-token",
            EdgeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ExchangeCenterCode = "12345"
        });
        using var tokens = new CoreAccessTokenProvider(http, options, new FixedClock());
        var client = new CoreApiClient(http, tokens, options);

        var lookup = await client.LookupInboundAsync(new CoreInboundLookupRequest(
            Guid.NewGuid(),
            "11111111-1111-1111-1111-111111111111",
            ["123450000000005954400001"],
            "Parcel",
            "Express",
            "12345",
            "59544",
            100,
            10,
            20,
            30,
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("D")), TestContext.Current.CancellationToken);
        var parcelOperation = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await client.PublishAsync(ContractMessageTypes.EdgeParcelReadingSubmitted, Payload(new ParcelReadingSubmittedV1(
                parcelOperation,
                "123450000000005954400001",
                ["123450000000005954400001"],
                "11111111-1111-1111-1111-111111111111",
                DateTimeOffset.UnixEpoch,
                new CorePhysicalAttributesV1(100, 10, 20, 30),
                CoreOperationStatusV1.Succeeded,
                null,
                "12345",
                "59544")), "idem-1", TestContext.Current.CancellationToken);
        await client.PublishAsync(ContractMessageTypes.EdgeBagClosed, Payload(new BagClosedV1(
                Guid.NewGuid(),
                "BAG-1",
                "11111111-1111-1111-1111-111111111111",
                "12345",
                "59544",
                ["123450000000005954400001"],
                "SEAL-1",
                "Road",
                DateTimeOffset.UnixEpoch,
                new CoreBagClosureCountsV1(1, 0, 0, 0))), "idem-2", TestContext.Current.CancellationToken);
        await client.PublishAsync(ContractMessageTypes.EdgeDispatchClosed, Payload(new DispatchClosedV1(
                Guid.NewGuid(),
                "DSP-1",
                "59544",
                ["BAG-1"],
                DateTimeOffset.UnixEpoch,
                false,
                [new CoreDispatchDeviceStatisticsV1(
                    "11111111-1111-1111-1111-111111111111", 1, 1, 0)])), "idem-3", TestContext.Current.CancellationToken);
        await client.PublishAsync("edge.device-ip-binding.changed", "{\"deviceId\":\"11111111-1111-1111-1111-111111111111\",\"bindingCount\":1,\"actor\":\"admin\",\"correlationId\":\"audit-1\",\"occurredAt\":\"2026-08-16T00:00:00Z\"}", "idem-4", TestContext.Current.CancellationToken);
        await client.PublishAsync("OperationalResultCaptured", Payload(new CoreOperationalResultMessage(
                "operation-1", null, "PostalDispatch.Completed", true, null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, "Succeeded")), "idem-5", TestContext.Current.CancellationToken);
        await client.RegisterAsync(new CoreEdgeRegistration(
            options.Value.EdgeId, options.Value.ExchangeCenterCode, "1.0.0", 7, DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);
        await client.SendHeartbeatAsync(new CoreEdgeHeartbeat(
            options.Value.EdgeId, DateTimeOffset.UnixEpoch, "1.0.0", 7, 1_000_000, 1, 2, 3,
            DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);

        Assert.Equal(CoreParcelDisposition.Returned, lookup.Disposition);
        Assert.Equal("Weight", lookup.DiscrepancyCode);
        Assert.Equal("idem-1", handler.Requests[1].Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("/api/edge/parcel-events", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("/api/edge/bags", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Equal("/api/edge/dispatches", handler.Requests[3].RequestUri!.AbsolutePath);
        Assert.Equal("/api/edge/events", handler.Requests[4].RequestUri!.AbsolutePath);
        Assert.Equal("/api/edge/operational-results", handler.Requests[5].RequestUri!.AbsolutePath);
        Assert.Equal("/api/edge/registration", handler.Requests[6].RequestUri!.AbsolutePath);
        Assert.Equal("/api/edge/heartbeat", handler.Requests[7].RequestUri!.AbsolutePath);
        var parcelJson = await handler.Requests[1].Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"edgeId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"", parcelJson, StringComparison.Ordinal);
        Assert.Contains("\"exchangeCenterCode\":\"12345\"", parcelJson, StringComparison.Ordinal);
        Assert.Contains("\"parcelBarcode\":\"123450000000005954400001\"", parcelJson, StringComparison.Ordinal);
        Assert.Contains("\"finalStatus\":\"Success\"", parcelJson, StringComparison.Ordinal);
        var operationalJson = await handler.Requests[5].Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"edgeId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"", operationalJson,
            StringComparison.Ordinal);
        Assert.Contains("\"exchangeCenterCode\":\"12345\"", operationalJson, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PublishAsync("Unknown", "{}", "idem-6", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SoapClientBuildsRequestAndParsesResponse()
    {
        var handler = new QueueHttpHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<Envelope><Body><GetDistributeInfoDataResult><Weight>25</Weight><BoxSize>S</BoxSize></GetDistributeInfoDataResult></Body></Envelope>",
                    Encoding.UTF8,
                    "text/xml")
            }
        ]);
        using var http = new HttpClient(handler);
        var client = new PostalSoapClient(http, Microsoft.Extensions.Options.Options.Create(new PostalSoapOptions
        {
            Endpoint = "https://postal.test/soap"
        }));

        var result = await client.GetAsync("59544", "123", 1459, TestContext.Current.CancellationToken);

        Assert.Equal(25m, result!.Weight);
        Assert.Contains("GetDistributeInfoData", await handler.Requests[0].Content!.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("http://tempuri.org/GetDistributeInfoData", handler.Requests[0].Headers.GetValues("SOAPAction").Single());
    }

    [Fact]
    public async Task SoapClientRejectsOversizedResponseBeforeParsing()
    {
        var handler = new QueueHttpHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 256), Encoding.UTF8, "text/xml")
            }
        ]);
        using var http = new HttpClient(handler);
        var client = new PostalSoapClient(http, Microsoft.Extensions.Options.Options.Create(new PostalSoapOptions
        {
            Endpoint = "https://postal.test/soap",
            MaximumResponseCharacters = 64
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync("59544", "123", 1459, TestContext.Current.CancellationToken));

        Assert.Contains("size limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CoreConfigurationSourceConsumesVerifierOnlyBootstrapContract()
    {
        const string salt = "AAAAAAAAAAAAAAAAAAAAAA==";
        const string hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        var handler = new QueueHttpHandler([Json(HttpStatusCode.OK, $$"""
            {
              "exchangeCenterCode":"59544","version":7,"generatedAt":"2026-08-16T00:00:00Z",
              "shortReadingThreshold":"00:00:03","returnThreshold":"1.00:00:00",
              "weightToleranceKg":0,"dimensionToleranceCm":0,
              "devices":[{"deviceId":"11111111-1111-1111-1111-111111111111","logicalCode":"L1",
                "name":"Sorter","deviceType":"Sorting","activationStatus":"Active",
                "secretAlgorithm":"PBKDF2-SHA256","secretIterations":100000,
                "secretSalt":"{{salt}}","secretHash":"{{hash}}"}],
              "postalCodes":["59544"]
            }
            """)]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        var options = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            UseFixedBearerToken = true,
            BearerToken = "edge-token"
        });
        using var tokens = new CoreAccessTokenProvider(http, options, new FixedClock());
        var source = new CoreConfigurationSource(http, tokens, options);

        var result = await source.FetchAsync(6, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var device = Assert.Single(result.Devices);
        Assert.Equal("PBKDF2-SHA256", device.SecretAlgorithm);
        Assert.Equal(100000, device.SecretIterations);
        Assert.Equal("/api/edge/config/bootstrap", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("currentVersion=6", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCoreJsonResponsePathUsesTheSharedBoundedReader()
    {
        const string sensitiveBody = "sensitive-core-response";
        var options = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            UseFixedBearerToken = true,
            BearerToken = "edge-token",
            EdgeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ExchangeCenterCode = "59544",
            MaximumResponseBytes = 16
        });

        var lookupHandler = new QueueHttpHandler([Json(HttpStatusCode.OK, $$"""{"value":"{{sensitiveBody}}"}""")]);
        using var lookupHttp = new HttpClient(lookupHandler) { BaseAddress = new Uri("https://core.test") };
        using var lookupTokens = new CoreAccessTokenProvider(lookupHttp, options, new FixedClock());
        var lookupClient = new CoreApiClient(lookupHttp, lookupTokens, options);
        var lookupError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lookupClient.LookupInboundAsync(new CoreInboundLookupRequest(
                Guid.NewGuid(),
                "11111111-1111-1111-1111-111111111111",
                ["123450000000005954400001"],
                "Parcel",
                "Express",
                "59544",
                "12345",
                1,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                Guid.NewGuid().ToString("D")), TestContext.Current.CancellationToken));

        var configurationHandler = new QueueHttpHandler([
            Json(HttpStatusCode.OK, $$"""{"value":"{{sensitiveBody}}"}""")
        ]);
        using var configurationHttp = new HttpClient(configurationHandler)
        {
            BaseAddress = new Uri("https://core.test")
        };
        using var configurationTokens = new CoreAccessTokenProvider(configurationHttp, options, new FixedClock());
        var configurationSource = new CoreConfigurationSource(configurationHttp, configurationTokens, options);
        var configurationError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            configurationSource.FetchAsync(null, TestContext.Current.CancellationToken));

        var imageHandler = new QueueHttpHandler([Json(HttpStatusCode.OK, $$"""{"value":"{{sensitiveBody}}"}""")]);
        using var imageHttp = new HttpClient(imageHandler) { BaseAddress = new Uri("https://core.test") };
        using var imageTokens = new CoreAccessTokenProvider(imageHttp, options, new FixedClock());
        var imageClient = new CoreImageUploadClient(imageHttp, imageTokens, options);
        var imageError = await Assert.ThrowsAsync<InvalidOperationException>(() => imageClient.UploadAsync(new CoreImageUpload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "123450000000005954400001",
                "image-1",
                [1],
                "image/jpeg",
                null,
                "image:bounded",
                DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken));

        Assert.All([lookupError, configurationError, imageError], error =>
        {
            Assert.Contains("size limit", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sensitiveBody, error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task BoundedResponseReaderAcceptsExactBoundaryAndRejectsInvalidLimits()
    {
        using var boundary = new ByteArrayContent(new byte[64]);
        using var oversized = new ByteArrayContent(new byte[65]);

        Assert.Equal(64, (await BoundedHttpResponse.ReadBytesAsync(boundary, 64, TestContext.Current.CancellationToken)).Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedHttpResponse.ReadBytesAsync(oversized, 64, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedHttpResponse.ReadBytesAsync(new ByteArrayContent([]), 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedHttpResponse.ReadBytesAsync(new ByteArrayContent([]), BoundedHttpResponse.HardMaximumResponseBytes + 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CoreImageClientUsesPresignedUploadAndPublishesMetadataWithoutImageBytes()
    {
        var handler = new QueueHttpHandler([
            Json(HttpStatusCode.OK, "{\"objectKey\":\"edge-uploads/safe/object\",\"uploadUrl\":\"https://storage.test/object\",\"expiresAt\":\"2026-08-16T00:10:00Z\",\"requiredHeaders\":{\"x-amz-server-side-encryption\":\"AES256\"}}"),
            Json(HttpStatusCode.OK, "{}"),
            Json(HttpStatusCode.OK, "{}")
        ]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        var options = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            UseFixedBearerToken = true,
            BearerToken = "edge-token",
            EdgeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ExchangeCenterCode = "59544"
        });
        using var tokens = new CoreAccessTokenProvider(http, options, new FixedClock());
        var client = new CoreImageUploadClient(http, tokens, options);

        await client.UploadAsync(new CoreImageUpload(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "123450000000005954400001",
            "img-1",
            [1, 2, 3],
            "image/jpeg",
            "front",
            "image:1",
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        var presign = await handler.Requests[0].Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"contentLength\":3", presign, StringComparison.Ordinal);
        Assert.DoesNotContain("objectKey", presign, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.Requests[1].Content!.Headers.ContentLength);
        Assert.Equal(
            "AES256",
            handler.Requests[1].Headers.GetValues("x-amz-server-side-encryption").Single());
        Assert.Equal("/api/edge/images/metadata", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Equal("image:1", handler.Requests[2].Headers.GetValues("Idempotency-Key").Single());
        var metadata = await handler.Requests[2].Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("edge-uploads/safe/object", metadata, StringComparison.Ordinal);
        Assert.Contains("\"exchangeCenterCode\":\"59544\"", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("AQID", metadata, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"requiredHeaders\":{\"x-unknown\":\"AES256\"}")]
    [InlineData(",\"requiredHeaders\":{\"x-amz-server-side-encryption\":\"none\"}")]
    public async Task CoreImageClientRejectsMissingOrUnexpectedSignedUploadHeaders(string requiredHeadersJson)
    {
        var payload = "{\"objectKey\":\"edge-uploads/safe/object\",\"uploadUrl\":" +
                      "\"https://storage.test/object\",\"expiresAt\":\"2026-08-16T00:10:00Z\"" +
                      requiredHeadersJson + "}";
        var handler = new QueueHttpHandler([Json(HttpStatusCode.OK, payload)]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        var options = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            UseFixedBearerToken = true,
            BearerToken = "edge-token",
            EdgeId = Guid.NewGuid(),
            ExchangeCenterCode = "59544"
        });
        using var tokens = new CoreAccessTokenProvider(http, options, new FixedClock());
        var client = new CoreImageUploadClient(http, tokens, options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.UploadAsync(new CoreImageUpload(
                Guid.NewGuid(), Guid.NewGuid(), "123450000000005954400001", "image", [1],
                "image/jpeg", null, "image:header-policy", DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken));

        Assert.Contains("required-header policy", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DestinationOAuthHealthCheckVerifiesEndpointWithoutExposingCredentials()
    {
        var successHandler = new QueueHttpHandler([
            Json(HttpStatusCode.OK, "{\"access_token\":\"sensitive-token\",\"expires_in\":3600}")
        ]);
        using var successHttp = new HttpClient(successHandler) { BaseAddress = new Uri("https://example.test") };
        var settings = Microsoft.Extensions.Options.Options.Create(new DestinationApiOptions
        {
            Username = "sensitive-user",
            Password = SensitiveTestSecret
        });
        using var successCache = new DestinationTokenCache(successHttp, settings, new FixedClock());
        var healthy = await new DestinationOAuthHealthCheck(successCache)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        var failureHandler = new QueueHttpHandler([Json(HttpStatusCode.Unauthorized, "{}")]);
        using var failureHttp = new HttpClient(failureHandler) { BaseAddress = new Uri("https://example.test") };
        using var failureCache = new DestinationTokenCache(failureHttp, settings, new FixedClock());
        var unhealthy = await new DestinationOAuthHealthCheck(failureCache)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.DoesNotContain("sensitive", healthy.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive", unhealthy.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string Payload<T>(T payload) =>
        System.Text.Json.JsonSerializer.Serialize(payload, PostJsonDefaults.Create());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class QueueHttpHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        internal List<HttpRequestMessage> Requests { get; } = [];
        internal int RequestCount => Requests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
                clone.Content = new StringContent(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(clone);
            return _responses.Dequeue();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var request in Requests) request.Dispose();
                foreach (var response in _responses) response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class DelayedHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timeout test handler unexpectedly resumed.");
        }
    }
}
