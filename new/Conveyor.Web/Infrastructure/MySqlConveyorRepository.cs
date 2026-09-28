using System.Globalization;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Conveyor.Web.Infrastructure;

public sealed class MySqlConveyorRepository : IConveyorRepository
{
    public bool IsSimulation => false;
    private readonly string _connectionString;
    private readonly ILogger<MySqlConveyorRepository> logger;

    public MySqlConveyorRepository(IOptions<ConveyorOptions> options, ILogger<MySqlConveyorRepository> logger)
    {
        this.logger = logger;
        _connectionString = options.Value.Database.ConnectionString;
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            logger.LogWarning("Base locale MySQL : aucune adresse configurée (chaîne de connexion absente)");
            return;
        }
        try
        {
            var target = new MySqlConnectionStringBuilder(_connectionString);
            logger.LogInformation("Base locale MySQL : serveur cible {Server}, port {Port}, base {Database} — tentative de connexion au démarrage",
                target.Server, target.Port, target.Database);
        }
        catch (ArgumentException)
        {
            // Never log the connection string or parsing exception: they can contain credentials.
            logger.LogWarning("Base locale MySQL : impossible de déterminer le serveur cible, chaîne de connexion invalide");
        }
    }

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
            where left(trim(cast(shipping_id as char)), 9) = @shippingId
               or (reference_no = @reference and customer_id = 129326)
            limit 1
            """;
        await using var command = new MySqlCommand(byShipping, connection);
        command.Parameters.AddWithValue("@shippingId", barcode[..9]);
        command.Parameters.AddWithValue("@reference", barcode[..11]);
        return await ReadShipmentAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Shipment>> FindShipmentsAsync(
        IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken)
    {
        var candidates = barcodes.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0)
            return new Dictionary<string, Shipment>(StringComparer.OrdinalIgnoreCase);

        var numericCandidates = candidates.Where(x => x.Length > 10 && long.TryParse(x, out _)).ToArray();
        var conditions = new List<string>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var barcodeParameters = AddStringParameters(command, "barcode", candidates);
        conditions.Add($"customer_barcode in ({string.Join(", ", barcodeParameters)})");
        if (numericCandidates.Length > 0)
        {
            var shippingPrefixes = numericCandidates.Select(x => x[..9]).Distinct().ToArray();
            var references = numericCandidates.Select(x => x[..11]).Distinct().ToArray();
            var shippingParameters = AddStringParameters(command, "shipping", shippingPrefixes);
            var referenceParameters = AddStringParameters(command, "reference", references);
            conditions.Add($"left(trim(cast(shipping_id as char)), 9) in ({string.Join(", ", shippingParameters)})");
            conditions.Add($"(customer_id = 129326 and reference_no in ({string.Join(", ", referenceParameters)}))");
        }

        command.CommandText = $"""
            select shipping_id, customer_id, route_id, disable_code98, dest_postal_code,
                   customer_barcode, reference_no
            from conveyor_shipment
            where {string.Join(" or ", conditions)}
            """;

        var rows = new List<ShipmentLookupRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var shipment = new Shipment(
                    Convert.ToString(reader["shipping_id"], CultureInfo.InvariantCulture)!,
                    reader.GetInt32("customer_id"),
                    reader.GetInt32("route_id"),
                    !reader.IsDBNull(reader.GetOrdinal("disable_code98")) && reader.GetBoolean("disable_code98"),
                    reader.IsDBNull(reader.GetOrdinal("dest_postal_code"))
                        ? null : Convert.ToString(reader["dest_postal_code"], CultureInfo.InvariantCulture));
                rows.Add(new ShipmentLookupRow(
                    shipment,
                    reader.IsDBNull(reader.GetOrdinal("customer_barcode"))
                        ? null : Convert.ToString(reader["customer_barcode"], CultureInfo.InvariantCulture),
                    reader.IsDBNull(reader.GetOrdinal("reference_no"))
                        ? null : Convert.ToString(reader["reference_no"], CultureInfo.InvariantCulture)));
            }
        }

        var results = new Dictionary<string, Shipment>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var row = rows.FirstOrDefault(x => string.Equals(x.CustomerBarcode, candidate, StringComparison.OrdinalIgnoreCase));
            if (row is null && candidate.Length > 10 && long.TryParse(candidate, out _))
            {
                var shippingPrefix = candidate[..9];
                var reference = candidate[..11];
                row = rows.FirstOrDefault(x =>
                    ShippingIdMatchesPrefix(x.Shipment.ShippingId, shippingPrefix) ||
                    (x.Shipment.CustomerId == 129326 &&
                     string.Equals(x.ReferenceNumber, reference, StringComparison.OrdinalIgnoreCase)));
            }
            if (row is not null) results[candidate] = row.Shipment;
        }
        return results;
    }

    private static string[] AddStringParameters(MySqlCommand command, string prefix, IReadOnlyList<string> values)
    {
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            names[index] = $"@{prefix}{index}";
            command.Parameters.AddWithValue(names[index], values[index]);
        }
        return names;
    }

    private static bool ShippingIdMatchesPrefix(string shippingId, string prefix)
    {
        var normalized = shippingId.Trim();
        return normalized.Length >= 9 &&
               string.Equals(normalized[..9], prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ShipmentLookupRow(Shipment Shipment, string? CustomerBarcode, string? ReferenceNumber);

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

    public async Task<IReadOnlyList<ConveyorShift>> GetShiftsAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("select id, name from conveyor_shift order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var shifts = new List<ConveyorShift>();
        while (await reader.ReadAsync(cancellationToken)) shifts.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? $"Shift {reader.GetInt32(0)}" : reader.GetString(1)));
        return shifts;
    }

    public async Task<IReadOnlyDictionary<int, string>> GetChuteDestinationsAsync(int shiftId, CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            select distinct c.chute_no, trim(d.depot_name_short) as depot_name
            from conveyor_shift_route c
            inner join location l on l.route_id = c.new_route_id
            inner join depot d on d.depot_id = l.depot_id
            where c.shift_id = @shift and c.chute_no is not null
              and d.depot_name_short is not null and trim(d.depot_name_short) <> ''
            order by c.chute_no, depot_name
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@shift", shiftId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var names = new Dictionary<int, List<string>>();
        while (await reader.ReadAsync(token))
        {
            var chute = reader.GetInt32(0);
            if (!names.TryGetValue(chute, out var depots)) names[chute] = depots = [];
            depots.Add(reader.GetString(1));
        }
        return names.ToDictionary(pair => pair.Key, pair => string.Join(" / ", pair.Value));
    }

    public async Task<IReadOnlyList<string>> GetShipmentPrefixesAsync(CancellationToken token)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        const string sql = """
            select CUSTOMER_ID, left(SHIPPING_ID, 3) as prefixe, count(*)
            from conveyor_shipment
            where INSERT_DATE > @since and left(SHIPPING_ID, 3) <> '518'
              and SHIPPING_ID > 105000000
            group by CUSTOMER_ID, left(SHIPPING_ID, 3)
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@since", new DateTime(2026, 9, 20));
        await using var reader = await command.ExecuteReaderAsync(token);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token))
            if (!reader.IsDBNull(1)) prefixes.Add(reader.GetString(1));
        return prefixes.ToArray();
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

    public async Task<int> RecordExceptionPassAsync(string codeType, string barcode, CancellationToken token)
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
        await using (var upsert = new MySqlCommand($"""
            insert into {table}(camera_data,cnt) values(@id,1)
            on duplicate key update cnt=cnt+1
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("@id", barcode);
            await upsert.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return count + 1;
    }

    public async Task SaveConveyorActionAsync(int conveyorId, bool start, int? cause, CancellationToken cancellationToken)
    {
        if (start ? cause is not null : cause is not (0 or 1 or 2))
            throw new ArgumentException("Cause invalide : PAUSE=0, JAM=1, DOWN=2 ; aucune cause pour un démarrage.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("insert into conveyor_action (DATE_INSERT, CONVEYOR_ID, ACTION, CAUSE) values (@date, @conveyor, @action, @cause)", connection);
        command.Parameters.AddWithValue("@date", DateTime.Now);
        command.Parameters.AddWithValue("@conveyor", conveyorId);
        command.Parameters.AddWithValue("@action", start);
        command.Parameters.AddWithValue("@cause", cause is null ? DBNull.Value : (object)cause.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetDataAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // TRUNCATE commits independently in MySQL; do not imply transactional rollback.
        foreach (var sql in new[]
        {
            "truncate table scan_history", "truncate table wb", "truncate table conveyor_shipment",
            "truncate table postalcode", "truncate table location", "delete from code98", "delete from code86"
        })
        {
            try
            {
                await using var command = new MySqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation("Reset Data : {Operation} terminé dans la base {Database}", sql, connection.Database);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Reset Data interrompu sur {Operation}; les opérations précédentes restent appliquées", sql);
                throw;
            }
        }
        logger.LogInformation("Reset Data terminé : scan_history, wb, conveyor_shipment, postalcode, location, code98 et code86 vidées dans la base {Database}", connection.Database);
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

    public async Task SaveScanAsync(int lineId, int? databaseLineId, ParcelContext parcel, SortDecision decision, CancellationToken token)
    {
        var validBarcode = decision.Barcode.Length is 11 or 12;
        var table = validBarcode ? "scan_history" : "scan_noWB";
        logger.LogInformation("Ligne {Line}, lineId MySQL {DatabaseLineId} : insertion prévue dans {Table}; code-barres {Barcode} ({Length} caractères), chute {Chute}",
            lineId + 1, databaseLineId, table, decision.Barcode, decision.Barcode.Length, decision.Chute);
        var sql = validBarcode
            ? "insert into scan_history(parcel_id,l,h,w,weight,chute,date_insert,lineId,source_type) values(@data,@l,@h,@w,@weight,@chute,@date,@line,200)"
            : "insert into scan_noWB(camera_data,l,h,w,weight,chute,date_insert,lineId,source_type) values(@data,@l,@h,@w,@weight,@chute,@date,@line,200)";
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
        command.Parameters.AddWithValue("@line", databaseLineId is null ? DBNull.Value : databaseLineId.Value);
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

    public async Task<bool?> HasRecentShipmentUpdatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "select exists(select 1 from conveyor_shipment where UPDATE_DATE > DATE_SUB(NOW(), INTERVAL 60 MINUTE))";
        await using var command = new MySqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    public async Task<DateTimeOffset?> GetLastShipmentUpdateAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "select greatest(coalesce(max(UPDATE_DATE), max(INSERT_DATE)), coalesce(max(INSERT_DATE), max(UPDATE_DATE))) from conveyor_shipment";
        await using var command = new MySqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null
            : new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Local));
    }

    public async Task<(long Parcels, long PostalCodes, long Scans, bool HasOverdueScans)> GetReferenceCountsAsync(CancellationToken token, long? cachedPostalCodes = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(token);
        var postalCount = cachedPostalCodes.HasValue ? "@postalCount" : "(select count(*) from location)";
        var sql = $"select (select count(*) from conveyor_shipment), {postalCount}, (select count(*) from scan_history), exists(select 1 from scan_history where date_insert < @cutoff)";
        await using var command = new MySqlCommand(sql, connection);
        if (cachedPostalCodes.HasValue) command.Parameters.AddWithValue("@postalCount", cachedPostalCodes.Value);
        command.Parameters.AddWithValue("@cutoff", DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return (0, 0, 0, false);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3));
    }
}

public sealed class SimulationConveyorRepository : IConveyorRepository
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Type, string Barcode), int> _exceptionPasses = new();
    private int _savedScans;
    public int SavedScans => Volatile.Read(ref _savedScans);
    public bool IsSimulation => true;
    public Task SaveConveyorActionAsync(int conveyorId, bool start, int? cause, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<DateTimeOffset?> GetLastShipmentUpdateAsync(CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
    public Task ResetDataAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<ConveyorShift>> GetShiftsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConveyorShift>>([new(1, "Jour"), new(2, "Soir")]);
    public Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken token) =>
        Task.FromResult<Shipment?>(barcode.StartsWith("123456789", StringComparison.Ordinal)
            ? new("12345678901", 1, 10, false) : null);
    public Task<int?> FindChuteForRouteAsync(int shiftId, int routeId, CancellationToken token) => Task.FromResult<int?>(routeId == 10 ? 4 : null);
    public Task<int?> FindChuteForPostalCodeAsync(int shiftId, string postalCode, CancellationToken token) => Task.FromResult<int?>(postalCode.StartsWith('H') ? 7 : 8);
    public Task<bool> ShouldUseExceptionChuteAsync(string codeType, string barcode, int retryLimit, CancellationToken token) => Task.FromResult(true);
    public Task<int> RecordExceptionPassAsync(string codeType, string barcode, CancellationToken token) =>
        Task.FromResult(_exceptionPasses.AddOrUpdate((codeType, barcode), 1, (_, count) => count + 1));
    public Task ClearExceptionCodeAsync(string codeType, string barcode, CancellationToken token) => Task.CompletedTask;
    public Task SaveScanAsync(int lineId, int? databaseLineId, ParcelContext parcel, SortDecision decision, CancellationToken token)
    {
        Interlocked.Increment(ref _savedScans);
        return Task.CompletedTask;
    }
    public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
    public Task<(long Parcels, long PostalCodes, long Scans, bool HasOverdueScans)> GetReferenceCountsAsync(CancellationToken token, long? cachedPostalCodes = null) => Task.FromResult((0L, cachedPostalCodes ?? 0L, 0L, false));
}
