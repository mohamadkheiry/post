using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.ValueObjects;
using Post.Edge.Infrastructure.Integrations.Postal;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Tests;

public sealed class SqlPostalClientTests
{
    [Fact]
    public async Task InboundClientMapsSqlSuccessFailurePendingAndTrackingLookup()
    {
        var gateway = new FakePostalSqlGateway
        {
            InboundState = new SqlInboundState(true, string.Empty)
        };
        var client = new SqlPostalInboundClient(gateway, SqlOptions(), new FixedClock());
        var registered = await client.RegisterAsync(InboundCommand(TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);

        gateway.InboundState = new SqlInboundState(true, "sensitive sql and parcel detail");
        var failed = await client.GetPendingResultAsync("42", TestContext.Current.CancellationToken);
        gateway.InboundState = new SqlInboundState(true, null);
        var pending = await client.RegisterAsync(InboundCommand(TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Equal(PostalInboundStatus.Registered, registered.Status);
        Assert.Equal(PostalInboundStatus.Failed, failed.Status);
        Assert.DoesNotContain("sensitive", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("parcel detail", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PostalInboundStatus.Pending, pending.Status);
        Assert.Equal("42", pending.TrackingId);
        Assert.Equal(PostalInboundStatus.Failed, (await client.GetPendingResultAsync("bad", TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task BagClientMapsSqlFailureBarcodeBackToReadIdAndConvertsWeight()
    {
        var failedRead = Guid.NewGuid();
        var gateway = new FakePostalSqlGateway
        {
            BagResult = new SqlBagResult("DSP", "BAG-1", 1, 1, [new SqlBagFailure("P2", "E2")])
        };
        var clock = new FixedClock();
        var client = new SqlPostalBagClient(gateway, SqlOptions(), clock);
        var result = await client.CloseAsync(new PostalBagCommand(
            Guid.NewGuid(), "D1", "00123", "S", "Road",
            [
                new PostalBagParcel(Guid.NewGuid(), "P1", false, 500, "Express"),
                new PostalBagParcel(failedRead, "P2", true, 750, "Express")
            ]), TestContext.Current.CancellationToken);

        Assert.Equal(PostalBagStatus.Completed, result.Status);
        Assert.Equal("BAG-1", result.BagBarcode);
        Assert.Equal(failedRead, Assert.Single(result.Failures).ReadId);
        Assert.Equal(1.25, gateway.LastBagCommand!.TotalWeightKg, 3);
        Assert.All(gateway.LastBagCommand.Parcels, parcel => Assert.Equal(clock.UtcNow, parcel.CreatedAt));
        Assert.False(gateway.LastBagCommand.DispatchClose);
    }

    [Fact]
    public async Task BagClientRejectsInconsistentSqlCountsFailuresAndMissingSuccessfulBagIdentity()
    {
        var gateway = new FakePostalSqlGateway();
        var client = new SqlPostalBagClient(gateway, SqlOptions(), new FixedClock());
        var command = new PostalBagCommand(
            Guid.NewGuid(), "D1", "00123", "S", "Road",
            [
                new PostalBagParcel(Guid.NewGuid(), "P1", false, 500, "Express"),
                new PostalBagParcel(Guid.NewGuid(), "P2", false, 500, "Express")
            ]);

        gateway.BagResult = new SqlBagResult(null, "BAG", 1, 0, []);
        AssertProtocolError(await client.CloseAsync(command, TestContext.Current.CancellationToken));
        gateway.BagResult = new SqlBagResult(null, "BAG", 1, 1, [new SqlBagFailure("UNKNOWN", "E")]);
        AssertProtocolError(await client.CloseAsync(command, TestContext.Current.CancellationToken));
        gateway.BagResult = new SqlBagResult(null, "BAG", 0, 2,
            [new SqlBagFailure("P1", "E"), new SqlBagFailure("P1", "E")]);
        AssertProtocolError(await client.CloseAsync(command, TestContext.Current.CancellationToken));
        gateway.BagResult = new SqlBagResult(null, null, 2, 0, []);
        AssertProtocolError(await client.CloseAsync(command, TestContext.Current.CancellationToken));

        gateway.BagResult = new SqlBagResult(null, null, 0, 2,
            [new SqlBagFailure("P1", "E1"), new SqlBagFailure("P2", "E2")]);
        var allFailed = await client.CloseAsync(command, TestContext.Current.CancellationToken);
        Assert.Equal(PostalBagStatus.Completed, allFailed.Status);
        Assert.Null(allFailed.BagBarcode);
        Assert.Equal(2, allFailed.Failures.Count);

        static void AssertProtocolError(PostalBagResult result)
        {
            Assert.Equal(PostalBagStatus.Failed, result.Status);
            Assert.Equal("POSTAL_PROTOCOL_ERROR", result.ErrorCode);
            Assert.Empty(result.Failures);
        }
    }

    [Fact]
    public async Task DispatchClientUsesEmptyParcelTvpAndRequiresDispatchId()
    {
        var gateway = new FakePostalSqlGateway
        {
            BagResult = new SqlBagResult("DSP-1", null, 0, 0, [])
        };
        var client = new SqlPostalDispatchClient(gateway, SqlOptions());

        var success = await client.CloseAsync(new PostalDispatchCommand(
            Guid.NewGuid(), "D1", "00123", ["BAG-1"]), TestContext.Current.CancellationToken);
        gateway.BagResult = new SqlBagResult(null, null, 0, 0, []);
        var error = await client.CloseAsync(new PostalDispatchCommand(
            Guid.NewGuid(), "D1", "00123", ["BAG-1"]), TestContext.Current.CancellationToken);

        Assert.True(success.IsSuccess);
        Assert.False(error.IsSuccess);
        Assert.True(gateway.LastBagCommand!.DispatchClose);
        Assert.Empty(gateway.LastBagCommand.Parcels);
    }

    [Fact]
    public async Task RealSqlGatewayRejectsMissingConnectionStringBeforeIo()
    {
        var gateway = new PostalSqlGateway(
            Microsoft.Extensions.Options.Options.Create(new PostalSqlOptions()),
            new Post.Edge.Infrastructure.Resilience.PostalSqlResilienceExecutor(
                Microsoft.Extensions.Options.Options.Create(new EdgeResilienceOptions()),
                new FixedClock()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReadInboundStateAsync(
            1,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.InsertInboundAsync(
            new SqlInboundInsert(
                "123450000000005954400001",
                1,
                "127.0.0.1",
                DateTimeOffset.UnixEpoch,
                59_544),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.CreateBagAsync(
            new SqlBagCommand(
                59_544,
                123,
                1,
                1,
                1,
                0.5,
                [new SqlBagParcel("123450000000005954400001", DateTimeOffset.UnixEpoch, 500)],
                "test-user",
                false),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RealSqlGatewayBuildsTypedParcelTableWithoutDatabaseIo()
    {
        var createdAt = new DateTimeOffset(2026, 8, 16, 1, 2, 3, TimeSpan.FromHours(3.5));

        using var table = PostalSqlGateway.CreateParcelTable([
            new SqlBagParcel("P1", createdAt, 750)
        ]);

        Assert.Equal(["Parcel", "CreateDate", "Weight"],
            table.Columns.Cast<System.Data.DataColumn>().Select(column => column.ColumnName));
        Assert.Equal("P1", table.Rows[0]["Parcel"]);
        Assert.Equal(createdAt.UtcDateTime, table.Rows[0]["CreateDate"]);
        Assert.Equal(750, table.Rows[0]["Weight"]);
    }

    [Fact]
    public void TableValuedParameterCarriesMoreThanOneThousandRowsWithoutSplittingExternalEffect()
    {
        var parcels = Enumerable.Range(0, 1_001)
            .Select(index => new SqlBagParcel(
                $"P{index:D4}",
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                index))
            .ToArray();

        using var table = PostalSqlGateway.CreateParcelTable(parcels);

        Assert.Equal(1_001, table.Rows.Count);
        Assert.Equal("P0000", table.Rows[0]["Parcel"]);
        Assert.Equal("P1000", table.Rows[1_000]["Parcel"]);
    }

    private static PostalInboundCommand InboundCommand(TimeSpan timeout) => new(
        Guid.NewGuid(), Guid.NewGuid(), "D1",
        BarcodeSet.Create(["123450000000005954400001"]),
        "59544",
        new PhysicalAttributes(100, null),
        "Parcel",
        "Express",
        DateTimeOffset.UtcNow,
        timeout);

    private static IOptions<PostalSqlOptions> SqlOptions() => Microsoft.Extensions.Options.Options.Create(new PostalSqlOptions
    {
        UserId = 1,
        SourceId = 59544,
        DeviceIp = "127.0.0.1",
        PostUsername = "test",
        PollInterval = TimeSpan.FromMilliseconds(1)
    });

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakePostalSqlGateway : IPostalSqlGateway
    {
        internal SqlInboundState InboundState { get; set; } = new(true, string.Empty);
        internal SqlBagResult BagResult { get; set; } = new("DSP", "BAG", 0, 0, []);
        internal SqlBagCommand? LastBagCommand { get; private set; }

        public Task<long> InsertInboundAsync(SqlInboundInsert command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(42L);
        }

        public Task<SqlInboundState> ReadInboundStateAsync(long sorterId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(InboundState);
        }

        public Task<SqlBagResult> CreateBagAsync(SqlBagCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastBagCommand = command;
            return Task.FromResult(BagResult);
        }
    }
}
