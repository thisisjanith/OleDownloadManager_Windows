using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Infrastructure.Persistence;

namespace OleDownloadManager.Infrastructure.Settings;

/// <summary>Stores <see cref="AppSettings"/> as one JSON document in the Settings table.</summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private const string SettingsKey = "app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteDatabase _database;
    private readonly ILogger<SqliteSettingsStore> _logger;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private AppSettings _current = new();

    public SqliteSettingsStore(SqliteDatabase database, ILogger<SqliteSettingsStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    public AppSettings Current => Volatile.Read(ref _current);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _database.EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var json = await Task.Run(() =>
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM Settings WHERE Key = $key";
            command.Parameters.AddWithValue("$key", SettingsKey);
            return command.ExecuteScalar() as string;
        }, cancellationToken).ConfigureAwait(false);

        var settings = new AppSettings();
        if (json is not null)
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Settings were unreadable; defaults are used");
            }
        }

        settings.Normalize();
        Volatile.Write(ref _current, settings);
    }

    public async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        AppSettings updated;
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updated = Current.Clone();
            update(updated);
            updated.Normalize();
            var json = JsonSerializer.Serialize(updated, JsonOptions);
            await _database.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    using var connection = _database.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
                    command.Parameters.AddWithValue("$key", SettingsKey);
                    command.Parameters.AddWithValue("$value", json);
                    command.ExecuteNonQuery();
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _database.WriteLock.Release();
            }

            Volatile.Write(ref _current, updated);
        }
        finally
        {
            _updateLock.Release();
        }

        SettingsChanged?.Invoke(this, updated);
    }
}
