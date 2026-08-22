using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Resilience;

namespace Post.Edge.Infrastructure.Integrations.Postal;

public sealed record SqlInboundInsert(
    string ParcelCode,
    int UserId,
    string DeviceIp,
    DateTimeOffset ParcelDate,
    int SourceId);

public sealed record SqlInboundState(bool Exists, string? Error);

public sealed record SqlBagParcel(string Barcode, DateTimeOffset CreatedAt, int WeightGrams);

public sealed record SqlBagCommand(
    int OriginCode,
    int DestinationCode,
    int BagServiceType,
    int BagType,
    int ServiceTypeId,
    double TotalWeightKg,
    IReadOnlyCollection<SqlBagParcel> Parcels,
    string PostUsername,
    bool DispatchClose);

public sealed record SqlBagFailure(string Barcode, string ErrorCode);

public sealed record SqlBagResult(
    string? DispatchId,
    string? BagBarcode,
    int SuccessfulCount,
    int FailedCount,
    IReadOnlyCollection<SqlBagFailure> Failures);

public interface IPostalSqlGateway
{
    Task<long> InsertInboundAsync(SqlInboundInsert command, CancellationToken cancellationToken);
    Task<SqlInboundState> ReadInboundStateAsync(long sorterId, CancellationToken cancellationToken);
    Task<SqlBagResult> CreateBagAsync(SqlBagCommand command, CancellationToken cancellationToken);
}

public sealed class PostalSqlGateway(
    IOptions<PostalSqlOptions> options,
    IPostalSqlResilienceExecutor resilience) : IPostalSqlGateway
{
    public Task<long> InsertInboundAsync(SqlInboundInsert command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return resilience.ExecuteAsync(
            token => InsertInboundCoreAsync(command, token),
            retrySafe: false,
            cancellationToken);
    }

    public Task<SqlInboundState> ReadInboundStateAsync(long sorterId, CancellationToken cancellationToken) =>
        resilience.ExecuteAsync(
            token => ReadInboundStateCoreAsync(sorterId, token),
            retrySafe: true,
            cancellationToken);

    public Task<SqlBagResult> CreateBagAsync(SqlBagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return resilience.ExecuteAsync(
            token => CreateBagCoreAsync(command, token),
            retrySafe: false,
            cancellationToken);
    }

    private async Task<long> InsertInboundCoreAsync(SqlInboundInsert command, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var sql = connection.CreateCommand();
        sql.CommandTimeout = options.Value.CommandTimeoutSeconds;
        sql.CommandText = """
            INSERT INTO dbo.IncomingParcels
                (ParcelCode, UserID, ImportDate, DigitalImportDate, SentStatus, IP, ParcelDate,
                 IsDamaged, UserDigitalID, Turn, SourceID, isTransmit, Error)
            OUTPUT INSERTED.sorterid
            VALUES
                (@parcel, @user, NULL, NULL, 0, @ip, @date, 0, NULL, 90, @source, 1, NULL);
            """;
        sql.Parameters.Add(new SqlParameter("@parcel", SqlDbType.NVarChar, 24) { Value = command.ParcelCode });
        sql.Parameters.Add(new SqlParameter("@user", SqlDbType.Int) { Value = command.UserId });
        sql.Parameters.Add(new SqlParameter("@ip", SqlDbType.NVarChar, 255) { Value = command.DeviceIp });
        sql.Parameters.Add(new SqlParameter("@date", SqlDbType.DateTime) { Value = command.ParcelDate.UtcDateTime });
        sql.Parameters.Add(new SqlParameter("@source", SqlDbType.Int) { Value = command.SourceId });
        var result = await sql.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<SqlInboundState> ReadInboundStateCoreAsync(long sorterId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var sql = connection.CreateCommand();
        sql.CommandTimeout = options.Value.CommandTimeoutSeconds;
        sql.CommandText = "SELECT Error FROM dbo.IncomingParcels WHERE sorterid = @id";
        sql.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = sorterId });
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new SqlInboundState(false, null);
        return new SqlInboundState(true, reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    private async Task<SqlBagResult> CreateBagCoreAsync(SqlBagCommand command, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var sql = connection.CreateCommand();
        sql.CommandTimeout = options.Value.CommandTimeoutSeconds;
        sql.CommandType = CommandType.Text;
        sql.CommandText = """
            EXEC dbo.CreateBag
                @origin, @destination, @bag_service_type, @bag_type, @service_type_id,
                @weight_kg, @parcels, @post_username, @dispatch_close;
            """;
        sql.Parameters.Add(new SqlParameter("@origin", SqlDbType.Int) { Value = command.OriginCode });
        sql.Parameters.Add(new SqlParameter("@destination", SqlDbType.Int) { Value = command.DestinationCode });
        sql.Parameters.Add(new SqlParameter("@bag_service_type", SqlDbType.Int) { Value = command.BagServiceType });
        sql.Parameters.Add(new SqlParameter("@bag_type", SqlDbType.Int) { Value = command.BagType });
        sql.Parameters.Add(new SqlParameter("@service_type_id", SqlDbType.Int) { Value = command.ServiceTypeId });
        sql.Parameters.Add(new SqlParameter("@weight_kg", SqlDbType.Float) { Value = command.TotalWeightKg });
        sql.Parameters.Add(new SqlParameter("@parcels", SqlDbType.Structured)
        {
            TypeName = "dbo.BagOfficeParcelType",
            Value = CreateParcelTable(command.Parcels)
        });
        sql.Parameters.Add(new SqlParameter("@post_username", SqlDbType.VarChar, 256) { Value = command.PostUsername });
        sql.Parameters.Add(new SqlParameter("@dispatch_close", SqlDbType.Bit) { Value = command.DispatchClose });

        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        string? dispatchId = null;
        string? bagBarcode = null;
        var successful = 0;
        var failed = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            dispatchId = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            bagBarcode = reader.FieldCount > 1 && !reader.IsDBNull(1) ? Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) : null;
            successful = reader.FieldCount > 3 && !reader.IsDBNull(3) ? Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) : 0;
            failed = reader.FieldCount > 4 && !reader.IsDBNull(4) ? Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) : 0;
        }

        var failures = new List<SqlBagFailure>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                failures.Add(new SqlBagFailure(
                    Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
                    reader.FieldCount > 1 ? Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "UNKNOWN" : "UNKNOWN"));
            }
        }

        return new SqlBagResult(dispatchId, bagBarcode, successful, failed, failures);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ConnectionString))
            throw new InvalidOperationException("Postal SQL connection string is not configured.");
        var connection = new SqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    internal static DataTable CreateParcelTable(IEnumerable<SqlBagParcel> parcels)
    {
        // This is a single table-valued RPC parameter, not a multi-row INSERT statement;
        // SQL Server's 1,000-row VALUES limit therefore does not apply and the stored
        // procedure is still invoked exactly once for one external bag-close effect.
        var table = new DataTable();
        table.Columns.Add("Parcel", typeof(string));
        table.Columns.Add("CreateDate", typeof(DateTime));
        table.Columns.Add("Weight", typeof(int));
        foreach (var parcel in parcels)
        {
            table.Rows.Add(parcel.Barcode, parcel.CreatedAt.UtcDateTime, parcel.WeightGrams);
        }
        return table;
    }
}
