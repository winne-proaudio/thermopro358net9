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
}
