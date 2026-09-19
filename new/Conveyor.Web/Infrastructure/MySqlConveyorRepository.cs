using System.Globalization;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Conveyor.Web.Infrastructure;

public sealed class MySqlConveyorRepository(IOptions<ConveyorOptions> options, ILogger<MySqlConveyorRepository> logger) : IConveyorRepository
{
    public bool IsSimulation => false;
    private readonly string _connectionString = options.Value.Database.ConnectionString;

    private MySqlConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("Conveyor:Database:ConnectionString n'est pas configurée.");
        return new MySqlConnection(_connectionString);
    }

    public async Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        const string byCustomer = """
            select shipping_id, customer_id, route_id, disable_code98, dest_postal_code
            from conveyor_shipment where customer_barcode = @barcode limit 1
            """;
        var result = await QueryShipmentAsync(connection, byCustomer, barcode, cancellationToken);
        if (result is not null) return result;

        if (!long.TryParse(barcode, out _) || barcode.Length <= 10) return null;
        const string byShipping = """
            select shipping_id, customer_id, route_id, disable_code98, dest_postal_code
            from conveyor_shipment
            where shipping_id = @shippingId
               or (reference_no = @reference and customer_id = 129326)
            limit 1
            """;
        await using var command = new MySqlCommand(byShipping, connection);
        command.Parameters.AddWithValue("@shippingId", barcode[..9]);
        command.Parameters.AddWithValue("@reference", barcode[..11]);
        return await ReadShipmentAsync(command, cancellationToken);
    }

    private static async Task<Shipment?> QueryShipmentAsync(MySqlConnection connection, string sql, string barcode, CancellationToken token)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@barcode", barcode);
        return await ReadShipmentAsync(command, token);
    }

    private static async Task<Shipment?> ReadShipmentAsync(MySqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        // Legacy databases can store shipping_id as INT/BIGINT instead of VARCHAR.
        // Convert the actual value; GetString requires a text column.
        return new Shipment(Convert.ToString(reader["shipping_id"], CultureInfo.InvariantCulture)!, reader.GetInt32("customer_id"),
            reader.GetInt32("route_id"),
            !reader.IsDBNull(reader.GetOrdinal("disable_code98")) && reader.GetBoolean("disable_code98"),
            reader.IsDBNull(reader.GetOrdinal("dest_postal_code")) ? null : Convert.ToString(reader["dest_postal_code"], CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<int>> GetShiftIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("select distinct shift_id from conveyor_shift_route where shift_id is not null order by shift_id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var shifts = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) shifts.Add(reader.GetInt32(0));
        return shifts;
    }

    public Task<int?> FindChuteForRouteAsync(int shiftId, int routeId, CancellationToken token) =>
        ScalarIntAsync("select chute_no from conveyor_shift_route where shift_id=@shift and new_route_id=@route limit 1",
            [("@shift", shiftId), ("@route", routeId)], token);

    public Task<int?> FindChuteForPostalCodeAsync(int shiftId, string postalCode, CancellationToken token) =>
        ScalarIntAsync("""
            select c.chute_no from location p
            inner join conveyor_shift_route c on p.route_id=c.new_route_id
            where c.shift_id=@shift and p.postal_code=@postal limit 1
            """, [("@shift", shiftId), ("@postal", postalCode)], token);

    private async Task<int?> ScalarIntAsync(string sql, IEnumerable<(string Name, object Value)> parameters, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var command = new MySqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    public async Task<bool> ShouldUseExceptionChuteAsync(string codeType, string barcode, int retryLimit, CancellationToken token)
    {
        var table = codeType switch { "86" => "code86", "98" => "code98", _ => throw new ArgumentOutOfRangeException(nameof(codeType)) };
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var count = 0;
        await using (var select = new MySqlCommand($"select cnt from {table} where camera_data=@id for update", connection, transaction))
        {
            select.Parameters.AddWithValue("@id", barcode);
            var value = await select.ExecuteScalarAsync(token);
            count = value is null or DBNull ? 0 : Convert.ToInt32(value);
        }
        if (count >= retryLimit)
        {
            await transaction.CommitAsync(token);
            return false;
        }
        await using (var upsert = new MySqlCommand($"""
            insert into {table}(camera_data,cnt) values(@id,1)
            on duplicate key update cnt=cnt+1
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("@id", barcode);
            await upsert.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task ClearExceptionCodeAsync(string codeType, string barcode, CancellationToken token)
    {
        var table = codeType switch { "86" => "code86", "98" => "code98", _ => throw new ArgumentOutOfRangeException(nameof(codeType)) };
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var command = new MySqlCommand($"delete from {table} where camera_data=@id", connection);
        command.Parameters.AddWithValue("@id", barcode);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task SaveScanAsync(int lineId, ParcelContext parcel, SortDecision decision, CancellationToken token)
    {
        var validBarcode = decision.Barcode.Length is 11 or 12;
        var table = validBarcode ? "scan_history" : "scan_noWB";
        logger.LogInformation("Ligne {Line} : insertion prévue dans {Table}; code-barres {Barcode} ({Length} caractères), chute {Chute}",
            lineId + 1, table, decision.Barcode, decision.Barcode.Length, decision.Chute);
        var sql = validBarcode
            ? "insert into scan_history(parcel_id,l,h,w,weight,chute,date_insert,lineId,source_type) values(@data,@l,@h,@w,@weight,@chute,@date,@line,1)"
            : "insert into scan_noWB(camera_data,l,h,w,weight,chute,date_insert,lineId,source_type) values(@data,@l,@h,@w,@weight,@chute,@date,@line,1)";
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@data", validBarcode ? decision.Barcode[..11] : parcel.CameraData);
        command.Parameters.AddWithValue("@l", parcel.Dimension.Length);
        command.Parameters.AddWithValue("@h", parcel.Dimension.Height);
        command.Parameters.AddWithValue("@w", parcel.Dimension.Width);
        command.Parameters.AddWithValue("@weight", parcel.Weight);
        command.Parameters.AddWithValue("@chute", decision.Chute);
        // Keep the legacy 19-character local timestamp (also supported by VARCHAR(19) columns).
        command.Parameters.AddWithValue("@date", parcel.CameraTimestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@line", lineId);
        var affected = await command.ExecuteNonQueryAsync(token);
        if (affected != 1)
            throw new InvalidOperationException($"Insertion dans {table} : {affected} ligne(s) affectée(s), 1 attendue.");
        logger.LogInformation("Ligne {Line} : insertion confirmée dans {Database}.{Table}; 1 enregistrement, code-barres {Barcode}",
            lineId + 1, connection.Database, table, validBarcode ? decision.Barcode[..11] : "sans lecture valide");
    }

    public async Task<bool> PingAsync(CancellationToken token)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(token);
            return await connection.PingAsync(token);
        }
        catch { return false; }
    }

    public async Task<(long Parcels, long PostalCodes, long Scans, bool HasOverdueScans)> GetReferenceCountsAsync(CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = "select (select count(*) from conveyor_shipment), (select count(*) from location), (select count(*) from scan_history), exists(select 1 from scan_history where date_insert < @cutoff)";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@cutoff", DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return (0, 0, 0, false);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3));
    }
}

public sealed class SimulationConveyorRepository : IConveyorRepository
{
    public bool IsSimulation => true;
    public Task<IReadOnlyList<int>> GetShiftIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<int>>([1, 2]);
    public Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken token) =>
        Task.FromResult<Shipment?>(barcode.StartsWith("123456789", StringComparison.Ordinal)
            ? new("12345678901", 1, 10, false) : null);
    public Task<int?> FindChuteForRouteAsync(int shiftId, int routeId, CancellationToken token) => Task.FromResult<int?>(routeId == 10 ? 4 : null);
    public Task<int?> FindChuteForPostalCodeAsync(int shiftId, string postalCode, CancellationToken token) => Task.FromResult<int?>(postalCode.StartsWith('H') ? 7 : 8);
    public Task<bool> ShouldUseExceptionChuteAsync(string codeType, string barcode, int retryLimit, CancellationToken token) => Task.FromResult(true);
    public Task ClearExceptionCodeAsync(string codeType, string barcode, CancellationToken token) => Task.CompletedTask;
    public Task SaveScanAsync(int lineId, ParcelContext parcel, SortDecision decision, CancellationToken token) => Task.CompletedTask;
    public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
    public Task<(long Parcels, long PostalCodes, long Scans, bool HasOverdueScans)> GetReferenceCountsAsync(CancellationToken token) => Task.FromResult((0L, 0L, 0L, false));
}
