using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Tp358.Backend;

public sealed class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _esp32ConnectionString;
    private readonly ILogger<DatabaseService> _logger;
    private bool _isAvailable = false;
    private readonly string _tp358HostLabel;
    private readonly string _esp32HostLabel;
    private readonly string _tp358DbHost;
    private readonly string _esp32DbHost;

    public bool IsAvailable => _isAvailable;
    public string Tp358HostLabel => _tp358HostLabel;
    public string Esp32HostLabel => _esp32HostLabel;
    public string Tp358DbHost => _tp358DbHost;
    public string Esp32DbHost => _esp32DbHost;

    public async Task LogDbServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var tp358Info = await TryGetServerInfoAsync(_connectionString, cancellationToken);
        if (tp358Info is not null)
        {
            _logger.LogInformation("TP358 DB server reports {Host}:{Port} (db={Database}).",
                tp358Info.Hostname ?? "n/a", tp358Info.Port?.ToString() ?? "n/a", tp358Info.DatabaseName ?? "n/a");
        }

        var esp32Info = await TryGetServerInfoAsync(_esp32ConnectionString, cancellationToken);
        if (esp32Info is not null)
        {
            _logger.LogInformation("ESP32 DB server reports {Host}:{Port} (db={Database}).",
                esp32Info.Hostname ?? "n/a", esp32Info.Port?.ToString() ?? "n/a", esp32Info.DatabaseName ?? "n/a");
        }

        await WarnIfTp358TableInEsp32Async(cancellationToken);
    }

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _logger = logger;
        var host = Environment.MachineName;
        var resolved = ResolveConnectionString(configuration, "MariaDB", host, out var source, out var tp358HostLabel, out var tp358HostOverride);
        _connectionString = resolved
            ?? throw new InvalidOperationException("MariaDB connection string not found in configuration");
        _logger.LogInformation("MariaDB connection resolved from {Source}.", source);
        _tp358HostLabel = tp358HostLabel;

        var esp32Resolved = ResolveConnectionString(configuration, "Esp32MariaDB", host, out var esp32Source, out var esp32HostLabel, out var esp32HostOverride);
        _esp32ConnectionString = esp32Resolved ?? _connectionString;
        if (esp32Resolved is null)
        {
            _logger.LogWarning("Esp32MariaDB not configured. Falling back to MariaDB connection.");
            _logger.LogInformation("Esp32MariaDB connection resolved from ConnectionStrings:MariaDB (fallback).");
        }
        else
        {
            _logger.LogInformation("Esp32MariaDB connection resolved from {Source}.", esp32Source);
        }
        _esp32HostLabel = esp32HostLabel;

        _tp358DbHost = ExtractDbHost(_connectionString) ?? "n/a";
        _esp32DbHost = ExtractDbHost(_esp32ConnectionString) ?? "n/a";

        _logger.LogInformation(
            "DB-Targets: tp358={Tp358Host} (override={Tp358Override}, dbHost={Tp358DbHost}), esp32={Esp32Host} (override={Esp32Override}, dbHost={Esp32DbHost}).",
            _tp358HostLabel, tp358HostOverride, _tp358DbHost,
            _esp32HostLabel, esp32HostOverride, _esp32DbHost);
    }

    private static string? ResolveConnectionString(
        IConfiguration configuration,
        string key,
        string host,
        out string source,
        out string hostLabel,
        out bool usedHostOverride)
    {
        var hostSection = configuration.GetSection("ConnectionStrings:Hosts");
        if (hostSection.Exists())
        {
            foreach (var child in hostSection.GetChildren())
            {
                if (!string.Equals(child.Key, host, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hostConnection = child[key];
                if (!string.IsNullOrWhiteSpace(hostConnection))
                {
                    source = $"ConnectionStrings:Hosts:{child.Key}:{key}";
                    hostLabel = child.Key;
                    usedHostOverride = true;
                    return hostConnection;
                }
            }
        }

        source = $"ConnectionStrings:{key} (fallback)";
        hostLabel = host;
        usedHostOverride = false;
        return configuration.GetConnectionString(key);
    }

    private static string? ExtractDbHost(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            var host = builder.Server;
            return string.IsNullOrWhiteSpace(host) ? null : host;
        }
        catch
        {
            return null;
        }
    }

    private sealed record DbServerInfo(string? Hostname, int? Port, string? DatabaseName);

    private async Task<DbServerInfo?> TryGetServerInfoAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = "SELECT @@hostname, @@port, DATABASE();";
            await using var command = new MySqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var hostname = reader.IsDBNull(0) ? null : reader.GetString(0);
                var port = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                var databaseName = reader.IsDBNull(2) ? null : reader.GetString(2);
                return new DbServerInfo(hostname, port, databaseName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Konnte DB-Server-Info nicht abrufen.");
        }

        return null;
    }

    private async Task WarnIfTp358TableInEsp32Async(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(_esp32ConnectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'measurements';
            ";

            await using var command = new MySqlCommand(sql, connection);
            var countObj = await command.ExecuteScalarAsync(cancellationToken);
            var count = countObj is null ? 0 : Convert.ToInt64(countObj);
            if (count > 0)
            {
                _logger.LogWarning("ESP32 database {Database} contains unexpected table 'measurements'.", connection.Database);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Konnte ESP32-Schema nicht auf unerwartete TP358-Tabellen prüfen.");
        }
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS measurements (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    device_mac VARCHAR(17) NOT NULL,
                    temperature DECIMAL(5,2),
                    humidity INT,
                    measured_at DATETIME NOT NULL,
                    INDEX idx_device_mac (device_mac),
                    INDEX idx_measured_at (measured_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            ";

            await using var command = new MySqlCommand(createTableSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await EnsureMeasurementsAutoIncrementAsync(connection, cancellationToken);

            _isAvailable = true;
            _logger.LogInformation("Datenbank initialisiert. Tabelle 'measurements' ist bereit.");
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _logger.LogError(ex, "Fehler beim Initialisieren der Datenbank");
            throw;
        }
    }

    private async Task EnsureMeasurementsAutoIncrementAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string extraSql = @"
            SELECT EXTRA
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = 'measurements'
              AND COLUMN_NAME = 'id';
        ";

        await using var extraCommand = new MySqlCommand(extraSql, connection);
        var extraObj = await extraCommand.ExecuteScalarAsync(cancellationToken);
        var extra = extraObj as string;

        if (extra is not null && extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (extraObj is null)
        {
            _logger.LogWarning("measurements.id column not found; cannot enforce AUTO_INCREMENT.");
            return;
        }

        _logger.LogWarning("measurements.id is not AUTO_INCREMENT; attempting to fix.");

        try
        {
            const string alterSql = "ALTER TABLE measurements MODIFY COLUMN id INT NOT NULL AUTO_INCREMENT";
            await using var alterCommand = new MySqlCommand(alterSql, connection);
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("measurements.id set to AUTO_INCREMENT.");
        }
        catch (MySqlException ex) when (ex.Message.Contains("must be defined as a key", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("measurements.id is not indexed; adding index and retrying.");
            const string addIndexSql = "ALTER TABLE measurements ADD INDEX idx_measurements_id (id)";
            await using var indexCommand = new MySqlCommand(addIndexSql, connection);
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);

            const string retrySql = "ALTER TABLE measurements MODIFY COLUMN id INT NOT NULL AUTO_INCREMENT";
            await using var retryCommand = new MySqlCommand(retrySql, connection);
            await retryCommand.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("measurements.id set to AUTO_INCREMENT after adding index.");
        }
    }

    public async Task InsertMeasurementAsync(string deviceMac, double? temperature, int? humidity, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            return; // Silently skip if database is not available
        }

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var insertSql = @"
                INSERT INTO measurements (device_mac, temperature, humidity, measured_at)
                VALUES (@deviceMac, @temperature, @humidity, @measuredAt)
            ";

            await using var command = new MySqlCommand(insertSql, connection);
            command.Parameters.AddWithValue("@deviceMac", deviceMac);
            command.Parameters.AddWithValue("@temperature", temperature.HasValue ? (object)temperature.Value : DBNull.Value);
            command.Parameters.AddWithValue("@humidity", humidity.HasValue ? (object)humidity.Value : DBNull.Value);
            command.Parameters.AddWithValue("@measuredAt", timestamp.DateTime);

            await command.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("TP358 insert ok for {Mac}.", deviceMac);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Speichern der Messung für Gerät {Mac}", deviceMac);
        }
    }

    public async Task<IReadOnlyList<TemperatureMeasurement>> GetTemperatureMeasurementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            return Array.Empty<TemperatureMeasurement>();
        }

        var results = new List<TemperatureMeasurement>();

        try
        {
            _logger.LogInformation(
                "TP358 query measurements (dbHost={DbHost}) BETWEEN {From} AND {To}.",
                _tp358DbHost, from, to);

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var selectSql = @"
                SELECT device_mac, temperature, measured_at
                FROM measurements
                WHERE measured_at BETWEEN @from AND @to
                ORDER BY measured_at ASC;
            ";

            await using var command = new MySqlCommand(selectSql, connection);
            command.Parameters.AddWithValue("@from", from.DateTime);
            command.Parameters.AddWithValue("@to", to.DateTime);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var deviceMac = reader.GetString(0);
                double? temperature = reader.IsDBNull(1) ? null : reader.GetDouble(1);
                var measuredAt = reader.GetDateTime(2);

                results.Add(new TemperatureMeasurement(deviceMac, temperature, measuredAt));
            }

            _logger.LogInformation("TP358 query returned {Count} rows.", results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der Messwerte aus der Datenbank");
        }

        return results;
    }

    public async Task<TemperatureStats> GetTemperatureStatsAsync(CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            return new TemperatureStats(0, null, null);
        }

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var selectSql = @"
                SELECT COUNT(*), MIN(measured_at), MAX(measured_at)
                FROM measurements;
            ";

            await using var command = new MySqlCommand(selectSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var count = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                var min = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                var max = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
                return new TemperatureStats(count, min, max);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der Temperatur-Statistik aus der Datenbank");
        }

        return new TemperatureStats(0, null, null);
    }

    public async Task<IReadOnlyList<ExternalTemperatureMeasurement>> GetExternalTemperatureMeasurementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<string> deviceIds,
        CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            return Array.Empty<ExternalTemperatureMeasurement>();
        }

        var results = new List<ExternalTemperatureMeasurement>();

        try
        {
            await using var connection = new MySqlConnection(_esp32ConnectionString);
            await connection.OpenAsync(cancellationToken);

            if (deviceIds is null || deviceIds.Count == 0)
            {
                return Array.Empty<ExternalTemperatureMeasurement>();
            }

            var deviceParameters = string.Join(", ", deviceIds.Select((_, index) => $"@device{index}"));
            var selectSql = @"
                SELECT `DeviceId`, `Timestamp`, `Temperature`
                FROM `esp32`.`Measurements`
                WHERE `Timestamp` BETWEEN @from AND @to
                  AND `DeviceId` IN (" + deviceParameters + @")
                ORDER BY `Timestamp` ASC;
            ";

            await using var command = new MySqlCommand(selectSql, connection);
            command.Parameters.AddWithValue("@from", from.DateTime);
            command.Parameters.AddWithValue("@to", to.DateTime);
            for (var i = 0; i < deviceIds.Count; i++)
            {
                command.Parameters.AddWithValue($"@device{i}", deviceIds[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var deviceId = reader.GetString(0);
                var timestamp = reader.GetDateTime(1);
                double? temperature = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                results.Add(new ExternalTemperatureMeasurement(deviceId, timestamp, temperature));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der externen Messwerte aus der Datenbank");
        }

        return results;
    }

    public async Task<IReadOnlyList<ExternalTemperatureMeasurement>> GetOldExternalTemperatureMeasurementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<string> deviceIds,
        CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            return Array.Empty<ExternalTemperatureMeasurement>();
        }

        var results = new List<ExternalTemperatureMeasurement>();

        try
        {
            await using var connection = new MySqlConnection(_esp32ConnectionString);
            await connection.OpenAsync(cancellationToken);

            if (deviceIds is null || deviceIds.Count == 0)
            {
                return Array.Empty<ExternalTemperatureMeasurement>();
            }

            var deviceParameters = string.Join(", ", deviceIds.Select((_, index) => $"@device{index}"));
            var selectSql = @"
                SELECT `DeviceId`, `Timestamp`, `Temperature`
                FROM `esp32`.`OldMeasurements`
                WHERE `Timestamp` BETWEEN @from AND @to
                  AND `DeviceId` IN (" + deviceParameters + @")
                ORDER BY `Timestamp` ASC;
            ";

            await using var command = new MySqlCommand(selectSql, connection);
            command.Parameters.AddWithValue("@from", from.DateTime);
            command.Parameters.AddWithValue("@to", to.DateTime);
            for (var i = 0; i < deviceIds.Count; i++)
            {
                command.Parameters.AddWithValue($"@device{i}", deviceIds[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var deviceId = reader.GetString(0);
                var timestamp = reader.GetDateTime(1);
                double? temperature = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                results.Add(new ExternalTemperatureMeasurement(deviceId, timestamp, temperature));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der OldMeasurements aus der Datenbank");
        }

        return results;
    }

    public async Task<ExternalTemperatureStats> GetExternalTemperatureStatsAsync(CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            return new ExternalTemperatureStats(0, null, null);
        }

        try
        {
            await using var connection = new MySqlConnection(_esp32ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var selectSql = @"
                SELECT COUNT(*), MIN(`Timestamp`), MAX(`Timestamp`)
                FROM `esp32`.`Measurements`;
            ";

            await using var command = new MySqlCommand(selectSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var count = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                var min = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                var max = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
                return new ExternalTemperatureStats(count, min, max);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der externen Messwerte-Statistik aus der Datenbank");
        }

        return new ExternalTemperatureStats(0, null, null);
    }
}

public sealed record TemperatureMeasurement(string DeviceMac, double? TemperatureC, DateTime MeasuredAt);
public sealed record TemperatureStats(long Count, DateTime? MinTimestamp, DateTime? MaxTimestamp);
public sealed record ExternalTemperatureMeasurement(string DeviceId, DateTime Timestamp, double? Temperature);
public sealed record ExternalTemperatureStats(long Count, DateTime? MinTimestamp, DateTime? MaxTimestamp);
