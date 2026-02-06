using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Tp358.Backend;

public sealed class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;
    private bool _isAvailable = false;

    public bool IsAvailable => _isAvailable;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _connectionString = configuration.GetConnectionString("MariaDB")
            ?? throw new InvalidOperationException("MariaDB connection string not found in configuration");
        _logger = logger;
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

            _logger.LogDebug("Messung in Datenbank gespeichert: {Mac} | Temp={Temp}°C, Humidity={Hum}%",
                deviceMac, temperature, humidity);
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Laden der Messwerte aus der Datenbank");
        }

        return results;
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
            await using var connection = new MySqlConnection(_connectionString);
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
            await using var connection = new MySqlConnection(_connectionString);
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
            await using var connection = new MySqlConnection(_connectionString);
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
public sealed record ExternalTemperatureMeasurement(string DeviceId, DateTime Timestamp, double? Temperature);
public sealed record ExternalTemperatureStats(long Count, DateTime? MinTimestamp, DateTime? MaxTimestamp);
