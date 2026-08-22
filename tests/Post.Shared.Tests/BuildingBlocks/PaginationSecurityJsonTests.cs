using System.Text.Json;
using Post.BuildingBlocks.Pagination;
using Post.BuildingBlocks.Security;
using Post.BuildingBlocks.Serialization;
using Post.Contracts.V1.Device;

namespace PostShared.Tests.BuildingBlocks;

public sealed class PaginationSecurityJsonTests
{
    [Fact]
    public void PageRequestCreateUsesDefaultsAndComputesOverflowSafeOffset()
    {
        var defaults = PageRequest.Create();
        var large = PageRequest.Create(int.MaxValue, 200);

        Assert.Equal(1, defaults.PageNumber);
        Assert.Equal(PageRequest.DefaultPageSize, defaults.PageSize);
        Assert.Equal(0, defaults.Offset);
        Assert.Equal((long)(int.MaxValue - 1) * 200, large.Offset);
    }

    [Fact]
    public void PageRequestCreateRejectsInvalidPageSizeAndCeiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRequest.Create(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRequest.Create(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRequest.Create(1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRequest.Create(1, 201, 200));
    }

    [Fact]
    public void PagedResultCreateMaterializesItemsAndComputesNavigation()
    {
        var page = PageRequest.Create(2, 2);
        var source = Enumerable.Range(3, 2).Select(value => value);

        var result = PagedResult.Create(source, 5, page);

        Assert.Equal([3, 4], result.Items);
        Assert.Equal(5, result.TotalCount);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(2, result.PageSize);
        Assert.Equal(3, result.TotalPages);
        Assert.True(result.HasPreviousPage);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public void PagedResultCreateHandlesEmptyAndLastPages()
    {
        var empty = PagedResult.Create(Array.Empty<int>(), 0, PageRequest.Create());
        var last = PagedResult.Create([5], 5, PageRequest.Create(3, 2));

        Assert.Equal(0, empty.TotalPages);
        Assert.False(empty.HasPreviousPage);
        Assert.False(empty.HasNextPage);
        Assert.True(last.HasPreviousPage);
        Assert.False(last.HasNextPage);
    }

    [Fact]
    public void PagedResultCreateValidatesArgumentsAndPageCapacity()
    {
        var page = PageRequest.Create(1, 1);

        Assert.Throws<ArgumentNullException>(() => PagedResult.Create<int>(null!, 0, page));
        Assert.Throws<ArgumentNullException>(() => PagedResult.Create(Array.Empty<int>(), 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => PagedResult.Create(Array.Empty<int>(), -1, page));
        Assert.Throws<ArgumentException>(() => PagedResult.Create([1, 2], 2, page));
    }

    [Fact]
    public void PagedResultMapMapsItemsAndPreservesMetadata()
    {
        var page = PagedResult.Create([1, 2], 10, PageRequest.Create(2, 2));

        var mapped = page.Map(value => $"v{value}");

        Assert.Equal(["v1", "v2"], mapped.Items);
        Assert.Equal(page.TotalCount, mapped.TotalCount);
        Assert.Equal(page.PageNumber, mapped.PageNumber);
        Assert.Throws<ArgumentNullException>(() => page.Map<string>(null!));
    }

    [Fact]
    public void SecretRedactorRecognizesDefaultAndNormalizedNames()
    {
        var redactor = new SecretRedactor();

        Assert.True(redactor.IsSensitive("device_token"));
        Assert.True(redactor.IsSensitive("Connection-String"));
        Assert.True(redactor.IsSensitive("PRESIGNED.URL"));
        Assert.False(redactor.IsSensitive("deviceId"));
        Assert.Throws<ArgumentException>(() => redactor.IsSensitive(" "));
    }

    [Fact]
    public void SecretRedactorSupportsCustomNamesAndMarkerAndValidatesConfiguration()
    {
        var redactor = new SecretRedactor(["postalCredential"], "***");

        Assert.True(redactor.IsSensitive("postal-credential"));
        Assert.Equal("***", redactor.RedactSecret("secret"));
        Assert.Throws<ArgumentException>(() => new SecretRedactor(marker: " "));
        Assert.Throws<ArgumentException>(() => new SecretRedactor([" "]));
    }

    [Fact]
    public void SecretRedactorRedactsNonNullSecretAndPreservesNullOrSafeValues()
    {
        var redactor = new SecretRedactor();

        Assert.Equal(SecretRedactor.DefaultMarker, redactor.RedactSecret("secret"));
        Assert.Equal(string.Empty, redactor.RedactSecret(null));
        Assert.Equal(SecretRedactor.DefaultMarker, redactor.RedactValue("password", "secret"));
        Assert.Null(redactor.RedactValue("password", null));
        Assert.Equal("device-1", redactor.RedactValue("deviceId", "device-1"));
    }

    [Fact]
    public void SecretRedactorReturnsNewReadOnlyRedactedDictionary()
    {
        var redactor = new SecretRedactor();
        var source = new Dictionary<string, object?>
        {
            ["authorization"] = "Bearer secret",
            ["deviceId"] = "device-1"
        };

        var result = redactor.RedactProperties(source);

        Assert.Equal(SecretRedactor.DefaultMarker, result["authorization"]);
        Assert.Equal("device-1", result["deviceId"]);
        source["deviceId"] = "changed";
        Assert.Equal("device-1", result["deviceId"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)result).Add("x", "y"));
        Assert.Throws<ArgumentNullException>(() => redactor.RedactProperties(null!));
    }

    [Fact]
    public void JsonDefaultsCreateReturnsFreshStrictCamelCaseOptions()
    {
        var first = PostJsonDefaults.Create();
        var second = PostJsonDefaults.Create();
        var json = JsonSerializer.Serialize(new JsonSample("value", null), first);

        Assert.NotSame(first, second);
        Assert.Equal("{\"someValue\":\"value\"}", json);
        Assert.False(first.PropertyNameCaseInsensitive);
        Assert.False(first.AllowTrailingCommas);
        Assert.Equal(64, first.MaxDepth);
    }

    [Fact]
    public void JsonDefaultsStrictRejectsUnknownPropertiesCommentsAndTrailingCommas()
    {
        var options = PostJsonDefaults.Create();

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JsonSample>("{\"someValue\":\"x\",\"unknown\":1}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JsonSample>("{/*x*/\"someValue\":\"x\"}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JsonSample>("{\"someValue\":\"x\",}", options));
    }

    [Fact]
    public void JsonDefaultsLenientAcceptsUnknownPropertiesCommentsAndTrailingCommas()
    {
        var options = PostJsonDefaults.Create(strict: false);
        const string json = "{/*x*/\"someValue\":\"x\",\"unknown\":1,}";

        var result = JsonSerializer.Deserialize<JsonSample>(json, options);

        Assert.Equal("x", result!.SomeValue);
        Assert.True(options.AllowTrailingCommas);
    }

    [Fact]
    public void JsonDefaultsApplyConfiguresMutableOptionsAndRejectsNullOrReadOnlyOptions()
    {
        var mutable = new JsonSerializerOptions();
        PostJsonDefaults.Apply(mutable);
        Assert.Equal(JsonNamingPolicy.CamelCase, mutable.PropertyNamingPolicy);

        Assert.Throws<ArgumentNullException>(() => PostJsonDefaults.Apply(null!));

        _ = JsonSerializer.Serialize(new JsonSample("x", null), mutable);
        Assert.True(mutable.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => PostJsonDefaults.Apply(mutable));
    }

    [Fact]
    public void JsonDefaultsPreservesProtocolSpecificNumericAndStringEnumEncoding()
    {
        var options = PostJsonDefaults.Create();

        Assert.Equal("0", JsonSerializer.Serialize(DeviceStatusCodeV1.Success, options));
        Assert.Equal("\"LabelPending\"", JsonSerializer.Serialize(BagCloseResultTypeV1.LabelPending, options));
    }

    private sealed record JsonSample(string SomeValue, string? OptionalValue);
}
