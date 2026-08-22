using Microsoft.AspNetCore.Http;
using Post.BuildingBlocks.Correlation;

namespace PostShared.Tests.BuildingBlocks;

public sealed class CorrelationTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("trace-123_ABC")]
    [InlineData("00-ab.cd/ef:12")]
    [InlineData(" surrounded ")]
    public void CorrelationIdAcceptsSafeValues(string value)
    {
        Assert.True(CorrelationId.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    [InlineData("unsafe header")]
    [InlineData("<script>")]
    public void CorrelationIdRejectsUnsafeValues(string? value)
    {
        Assert.False(CorrelationId.IsValid(value));
    }

    [Fact]
    public void CorrelationIdRejectsOverlongValue()
    {
        Assert.False(CorrelationId.IsValid(new string('a', CorrelationId.MaximumLength + 1)));
    }

    [Fact]
    public void NormalizeOrCreateTrimsValidValueOrCreatesValidUniqueValue()
    {
        Assert.Equal("abc", CorrelationId.NormalizeOrCreate(" abc "));

        var first = CorrelationId.NormalizeOrCreate("invalid value");
        var second = CorrelationId.NormalizeOrCreate(null);

        Assert.True(CorrelationId.IsValid(first));
        Assert.True(CorrelationId.IsValid(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ContextPushNestsAndRestoresPreviousValueEvenWhenDisposedTwice()
    {
        var context = new CorrelationContext();
        Assert.Null(context.Current);

        using (context.Push("outer"))
        {
            Assert.Equal("outer", context.Current);
            var inner = context.Push("inner");
            Assert.Equal("inner", context.Current);
            inner.Dispose();
            inner.Dispose();
            Assert.Equal("outer", context.Current);
        }

        Assert.Null(context.Current);
        Assert.Throws<ArgumentException>(() => context.Push("unsafe value"));
    }

    [Fact]
    public async Task ContextFlowsThroughAsyncCallsButNotAfterScope()
    {
        var context = new CorrelationContext();
        using (context.Push("async-id"))
        {
            await Task.Yield();
            Assert.Equal("async-id", context.Current);
        }

        Assert.Null(context.Current);
    }

    [Fact]
    public void ContextGetOrCreateReusesCurrentOrStoresNewValue()
    {
        var context = new CorrelationContext();
        var created = context.GetOrCreate();

        Assert.Equal(created, context.Current);
        Assert.Equal(created, context.GetOrCreate());
        Assert.True(CorrelationId.IsValid(created));
    }

    [Fact]
    public void MiddlewareConstructorRejectsNullNext()
    {
        Assert.Throws<ArgumentNullException>(() => new CorrelationContextMiddleware(null!));
    }

    [Fact]
    public async Task MiddlewareUsesSafeRequestHeaderSetsResponseAndRestoresContext()
    {
        var context = new CorrelationContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[CorrelationId.HeaderName] = " client-id ";
        string? observed = null;
        var middleware = new CorrelationContextMiddleware(current =>
        {
            observed = context.Current;
            Assert.Equal("client-id", current.TraceIdentifier);
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext, context);

        Assert.Equal("client-id", observed);
        Assert.Equal("client-id", httpContext.Response.Headers[CorrelationId.HeaderName]);
        Assert.Null(context.Current);
    }

    [Fact]
    public async Task MiddlewareReplacesUnsafeHeaderAndValidatesArguments()
    {
        var context = new CorrelationContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[CorrelationId.HeaderName] = "header injection\r\n";
        var middleware = new CorrelationContextMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(httpContext, context);

        Assert.True(CorrelationId.IsValid(httpContext.TraceIdentifier));
        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(null!, context));
        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(new DefaultHttpContext(), null!));
    }
}
