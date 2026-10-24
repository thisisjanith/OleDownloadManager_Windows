using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OleDownloadManager.Infrastructure.Persistence;

/// <summary>
/// Owns the SQLite file: connection settings and explicit, versioned schema migrations
/// tracked with PRAGMA user_version. Each migration runs in its own transaction.
/// </summary>
public sealed class SqliteDatabase
{
    /// <summary>Ordered migrations. Never edit a shipped migration; append a new one.</summary>
    internal static readonly IReadOnlyList<string[]> Migrations =
    [
        // 1: initial schema
        [
            """
            CREATE TABLE Downloads (
                Id TEXT NOT NULL PRIMARY KEY,
                Kind INTEGER NOT NULL DEFAULT 0,
                SourceUrl TEXT NOT NULL,
                FinalUrl TEXT,
                PageUrl TEXT,
                FileName TEXT NOT NULL,
                AutoFileName INTEGER NOT NULL DEFAULT 0,
                DestinationDirectory TEXT NOT NULL,
                TemporaryDirectory TEXT,
                ExpectedSize INTEGER,
                DownloadedBytes INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL,
                Priority INTEGER NOT NULL DEFAULT 1,
                Category INTEGER NOT NULL DEFAULT 0,
                Source INTEGER NOT NULL DEFAULT 0,
                ConflictPolicy INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT,
                CompletedAt TEXT,
                ScheduledAt TEXT,
                DeletedAt TEXT,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                LastError TEXT,
                LastErrorKind INTEGER,
                HttpStatusCode INTEGER,
                ETag TEXT,
                LastModified TEXT,
                SupportsRangeRequests INTEGER,
                AccelerationEnabled INTEGER NOT NULL DEFAULT 1,
                MaxConnections INTEGER NOT NULL DEFAULT 4,
                SpeedLimit INTEGER,
                QueuePosition INTEGER NOT NULL DEFAULT 0,
                ExpectedSha256 TEXT,
                Sha256 TEXT,
                HeadersJson TEXT,
                CredentialKey TEXT,
                MediaFormat TEXT
            )
            """,
            "CREATE INDEX IX_Downloads_Status ON Downloads(Status)",
            "CREATE INDEX IX_Downloads_Queue ON Downloads(Status, Priority DESC, QueuePosition)",
            "CREATE INDEX IX_Downloads_CreatedAt ON Downloads(CreatedAt)",
            "CREATE INDEX IX_Downloads_Category ON Downloads(Category)",
            """
            CREATE TABLE Segments (
                DownloadId TEXT NOT NULL REFERENCES Downloads(Id) ON DELETE CASCADE,
                SegmentIndex INTEGER NOT NULL,
                StartOffset INTEGER NOT NULL,
                EndOffset INTEGER NOT NULL,
                DownloadedBytes INTEGER NOT NULL DEFAULT 0,
                State INTEGER NOT NULL DEFAULT 0,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                LastError TEXT,
                PRIMARY KEY (DownloadId, SegmentIndex)
            )
            """,
            "CREATE TABLE Settings (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL)",
        ],

        // 2: download history events and trash index
        [
            """
            CREATE TABLE DownloadEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DownloadId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Detail TEXT
            )
            """,
            "CREATE INDEX IX_DownloadEvents_DownloadId ON DownloadEvents(DownloadId, Timestamp)",
            "CREATE INDEX IX_Downloads_DeletedAt ON Downloads(DeletedAt)",
        ],
    ];

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private volatile bool _migrated;

    public SqliteDatabase(string databasePath, ILogger<SqliteDatabase> logger)
    {
        DatabasePath = databasePath;
        _logger = logger;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }
    public string ConnectionString { get; }

    /// <summary>Serializes writers so concurrent saves never hit SQLITE_BUSY and are applied in order.</summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public static int LatestVersion => Migrations.Count;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task EnsureMigratedAsync(CancellationToken cancellationToken = default)
    {
        if (_migrated)
        {
            return;
        }

        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrated)
            {
                return;
            }

            await Task.Run(Migrate, cancellationToken).ConfigureAwait(false);
            _migrated = true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    public int GetSchemaVersion()
    {
        using var connection = Open();
        return ReadVersion(connection);
    }

    private void Migrate()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            // WAL keeps readers unblocked during writes and survives application crashes.
            wal.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            wal.ExecuteNonQuery();
        }

        var version = ReadVersion(connection);
        if (version > LatestVersion)
        {
            throw new InvalidOperationException(
                $"The database was created by a newer version of OleDownloadManager (schema {version}, this build supports {LatestVersion}).");
        }

        for (var target = version + 1; target <= LatestVersion; target++)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var statement in Migrations[target - 1])
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            using (var setVersion = connection.CreateCommand())
            {
                setVersion.Transaction = transaction;
                setVersion.CommandText = $"PRAGMA user_version = {target}";
                setVersion.ExecuteNonQuery();
            }

            transaction.Commit();
            _logger.LogInformation("Applied database migration {Version}", target);
        }
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
