using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;

namespace OleDownloadManager.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed download store. Writes are serialized and each save snapshots the item
/// inside the write lock, so the most recent state is always the one persisted last.
/// Only durable (flushed) segment byte counts are stored.
/// </summary>
public sealed class SqliteDownloadRepository : IDownloadRepository
{
    private readonly SqliteDatabase _database;

    public SqliteDownloadRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _database.EnsureMigratedAsync(cancellationToken);

    public Task<IReadOnlyList<DownloadItem>> LoadAllAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var connection = _database.Open();
        var items = new Dictionary<Guid, DownloadItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM Downloads ORDER BY QueuePosition, CreatedAt";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = ReadItem(reader);
                items[item.Id] = item;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM Segments ORDER BY DownloadId, StartOffset";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(reader.GetOrdinal("DownloadId")));
                if (!items.TryGetValue(id, out var item))
                {
                    continue;
                }

                var downloaded = reader.GetInt64(reader.GetOrdinal("DownloadedBytes"));
                item.Segments.Add(new DownloadSegment
                {
                    Index = reader.GetInt32(reader.GetOrdinal("SegmentIndex")),
                    StartOffset = reader.GetInt64(reader.GetOrdinal("StartOffset")),
                    EndOffset = reader.GetInt64(reader.GetOrdinal("EndOffset")),
                    DownloadedBytes = downloaded,
                    DurableBytes = downloaded,
                    State = (SegmentState)reader.GetInt32(reader.GetOrdinal("State")),
                    RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                    LastError = GetNullableString(reader, "LastError"),
                });
            }
        }

        foreach (var item in items.Values.Where(i => i.Segments.Count > 0))
        {
            item.DownloadedBytes = item.Segments.Sum(s => s.DownloadedBytes);
        }

        return (IReadOnlyList<DownloadItem>)items.Values.ToList();
    }, cancellationToken);

    public async Task SaveAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        await _database.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var record = Snapshot(item);
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                UpsertDownload(connection, transaction, record);
                ReplaceSegments(connection, transaction, record);
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _database.WriteLock.Release();
        }
    }

    public async Task SaveProgressAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        await _database.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var record = Snapshot(item);
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE Downloads SET DownloadedBytes = $bytes WHERE Id = $id";
                    command.Parameters.AddWithValue("$bytes", record.DownloadedBytes);
                    command.Parameters.AddWithValue("$id", record.Id.ToString());
                    command.ExecuteNonQuery();
                }

                ReplaceSegments(connection, transaction, record);
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _database.WriteLock.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _database.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                foreach (var sql in new[] { "DELETE FROM Segments WHERE DownloadId = $id", "DELETE FROM DownloadEvents WHERE DownloadId = $id", "DELETE FROM Downloads WHERE Id = $id" })
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$id", id.ToString());
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _database.WriteLock.Release();
        }
    }

    public async Task UpdateQueuePositionsAsync(IReadOnlyList<(Guid Id, long Position)> positions, CancellationToken cancellationToken = default)
    {
        await _database.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Downloads SET QueuePosition = $position WHERE Id = $id";
                var position = command.Parameters.Add("$position", SqliteType.Integer);
                var id = command.Parameters.Add("$id", SqliteType.Text);
                foreach (var (downloadId, value) in positions)
                {
                    position.Value = value;
                    id.Value = downloadId.ToString();
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _database.WriteLock.Release();
        }
    }

    public async Task AddEventAsync(DownloadEvent downloadEvent, CancellationToken cancellationToken = default)
    {
        await _database.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = _database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO DownloadEvents (DownloadId, Timestamp, EventType, Detail) VALUES ($id, $ts, $type, $detail)";
                command.Parameters.AddWithValue("$id", downloadEvent.DownloadId.ToString());
                command.Parameters.AddWithValue("$ts", FormatDate(downloadEvent.Timestamp));
                command.Parameters.AddWithValue("$type", downloadEvent.EventType);
                command.Parameters.AddWithValue("$detail", (object?)downloadEvent.Detail ?? DBNull.Value);
                command.ExecuteNonQuery();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _database.WriteLock.Release();
        }
    }

    public Task<IReadOnlyList<DownloadEvent>> GetEventsAsync(Guid downloadId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Timestamp, EventType, Detail FROM DownloadEvents WHERE DownloadId = $id ORDER BY Id";
        command.Parameters.AddWithValue("$id", downloadId.ToString());
        using var reader = command.ExecuteReader();
        var events = new List<DownloadEvent>();
        while (reader.Read())
        {
            events.Add(new DownloadEvent(downloadId, ParseDate(reader.GetString(0))!.Value, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return (IReadOnlyList<DownloadEvent>)events;
    }, cancellationToken);

    // ------------------------------------------------------------ mapping

    private sealed record SegmentRecord(int Index, long Start, long End, long Durable, SegmentState State, int RetryCount, string? LastError);

    private sealed record DownloadRecord(DownloadItem Values, long DownloadedBytes, IReadOnlyList<SegmentRecord> Segments, string? HeadersJson)
    {
        public Guid Id => Values.Id;
    }

    /// <summary>Copies the item under its lock so the database never sees a torn state.</summary>
    private static DownloadRecord Snapshot(DownloadItem item)
    {
        lock (item.SyncRoot)
        {
            var copy = new DownloadItem
            {
                Id = item.Id,
                Kind = item.Kind,
                SourceUrl = item.SourceUrl,
                FinalUrl = item.FinalUrl,
                PageUrl = item.PageUrl,
                FileName = item.FileName,
                AutoFileName = item.AutoFileName,
                DestinationDirectory = item.DestinationDirectory,
                TemporaryDirectory = item.TemporaryDirectory,
                ExpectedSize = item.ExpectedSize,
                Status = item.Status,
                Priority = item.Priority,
                Category = item.Category,
                Source = item.Source,
                ConflictPolicy = item.ConflictPolicy,
                CreatedAt = item.CreatedAt,
                StartedAt = item.StartedAt,
                CompletedAt = item.CompletedAt,
                ScheduledAt = item.ScheduledAt,
                DeletedAt = item.DeletedAt,
                RetryCount = item.RetryCount,
                LastError = item.LastError,
                LastErrorKind = item.LastErrorKind,
                HttpStatusCode = item.HttpStatusCode,
                ETag = item.ETag,
                LastModified = item.LastModified,
                SupportsRangeRequests = item.SupportsRangeRequests,
                AccelerationEnabled = item.AccelerationEnabled,
                MaxConnections = item.MaxConnections,
                SpeedLimit = item.SpeedLimit,
                QueuePosition = item.QueuePosition,
                ExpectedSha256 = item.ExpectedSha256,
                Sha256 = item.Sha256,
                CredentialKey = item.CredentialKey,
                MediaFormat = item.MediaFormat,
            };

            var segments = item.Segments
                .Select(s => new SegmentRecord(s.Index, s.StartOffset, s.EndOffset, Math.Clamp(s.DurableBytes, 0, s.Length), s.State, s.RetryCount, s.LastError))
                .ToList();
            var downloaded = segments.Count > 0 ? segments.Sum(s => s.Durable) : item.DownloadedBytes;
            var headers = item.Headers.Count > 0 ? JsonSerializer.Serialize(item.Headers) : null;
            return new DownloadRecord(copy, downloaded, segments, headers);
        }
    }

    private static void UpsertDownload(SqliteConnection connection, SqliteTransaction transaction, DownloadRecord record)
    {
        var i = record.Values;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Downloads (Id, Kind, SourceUrl, FinalUrl, PageUrl, FileName, AutoFileName, DestinationDirectory, TemporaryDirectory,
                ExpectedSize, DownloadedBytes, Status, Priority, Category, Source, ConflictPolicy, CreatedAt, StartedAt, CompletedAt,
                ScheduledAt, DeletedAt, RetryCount, LastError, LastErrorKind, HttpStatusCode, ETag, LastModified, SupportsRangeRequests,
                AccelerationEnabled, MaxConnections, SpeedLimit, QueuePosition, ExpectedSha256, Sha256, HeadersJson, CredentialKey, MediaFormat)
            VALUES ($Id, $Kind, $SourceUrl, $FinalUrl, $PageUrl, $FileName, $AutoFileName, $DestinationDirectory, $TemporaryDirectory,
                $ExpectedSize, $DownloadedBytes, $Status, $Priority, $Category, $Source, $ConflictPolicy, $CreatedAt, $StartedAt, $CompletedAt,
                $ScheduledAt, $DeletedAt, $RetryCount, $LastError, $LastErrorKind, $HttpStatusCode, $ETag, $LastModified, $SupportsRangeRequests,
                $AccelerationEnabled, $MaxConnections, $SpeedLimit, $QueuePosition, $ExpectedSha256, $Sha256, $HeadersJson, $CredentialKey, $MediaFormat)
            ON CONFLICT(Id) DO UPDATE SET
                Kind = excluded.Kind, SourceUrl = excluded.SourceUrl, FinalUrl = excluded.FinalUrl, PageUrl = excluded.PageUrl,
                FileName = excluded.FileName, AutoFileName = excluded.AutoFileName, DestinationDirectory = excluded.DestinationDirectory,
                TemporaryDirectory = excluded.TemporaryDirectory, ExpectedSize = excluded.ExpectedSize, DownloadedBytes = excluded.DownloadedBytes,
                Status = excluded.Status, Priority = excluded.Priority, Category = excluded.Category, Source = excluded.Source,
                ConflictPolicy = excluded.ConflictPolicy, CreatedAt = excluded.CreatedAt, StartedAt = excluded.StartedAt,
                CompletedAt = excluded.CompletedAt, ScheduledAt = excluded.ScheduledAt, DeletedAt = excluded.DeletedAt,
                RetryCount = excluded.RetryCount, LastError = excluded.LastError, LastErrorKind = excluded.LastErrorKind,
                HttpStatusCode = excluded.HttpStatusCode, ETag = excluded.ETag, LastModified = excluded.LastModified,
                SupportsRangeRequests = excluded.SupportsRangeRequests, AccelerationEnabled = excluded.AccelerationEnabled,
                MaxConnections = excluded.MaxConnections, SpeedLimit = excluded.SpeedLimit, QueuePosition = excluded.QueuePosition,
                ExpectedSha256 = excluded.ExpectedSha256, Sha256 = excluded.Sha256, HeadersJson = excluded.HeadersJson,
                CredentialKey = excluded.CredentialKey, MediaFormat = excluded.MediaFormat
            """;
        var p = command.Parameters;
        p.AddWithValue("$Id", i.Id.ToString());
        p.AddWithValue("$Kind", (int)i.Kind);
        p.AddWithValue("$SourceUrl", i.SourceUrl.AbsoluteUri);
        p.AddWithValue("$FinalUrl", Db(i.FinalUrl?.AbsoluteUri));
        p.AddWithValue("$PageUrl", Db(i.PageUrl?.AbsoluteUri));
        p.AddWithValue("$FileName", i.FileName);
        p.AddWithValue("$AutoFileName", i.AutoFileName ? 1 : 0);
        p.AddWithValue("$DestinationDirectory", i.DestinationDirectory);
        p.AddWithValue("$TemporaryDirectory", Db(i.TemporaryDirectory));
        p.AddWithValue("$ExpectedSize", Db(i.ExpectedSize));
        p.AddWithValue("$DownloadedBytes", record.DownloadedBytes);
        p.AddWithValue("$Status", (int)i.Status);
        p.AddWithValue("$Priority", (int)i.Priority);
        p.AddWithValue("$Category", (int)i.Category);
        p.AddWithValue("$Source", (int)i.Source);
        p.AddWithValue("$ConflictPolicy", (int)i.ConflictPolicy);
        p.AddWithValue("$CreatedAt", FormatDate(i.CreatedAt));
        p.AddWithValue("$StartedAt", Db(FormatDate(i.StartedAt)));
        p.AddWithValue("$CompletedAt", Db(FormatDate(i.CompletedAt)));
        p.AddWithValue("$ScheduledAt", Db(FormatDate(i.ScheduledAt)));
        p.AddWithValue("$DeletedAt", Db(FormatDate(i.DeletedAt)));
        p.AddWithValue("$RetryCount", i.RetryCount);
        p.AddWithValue("$LastError", Db(i.LastError));
        p.AddWithValue("$LastErrorKind", Db(i.LastErrorKind is { } kind ? (int)kind : null));
        p.AddWithValue("$HttpStatusCode", Db(i.HttpStatusCode));
        p.AddWithValue("$ETag", Db(i.ETag));
        p.AddWithValue("$LastModified", Db(FormatDate(i.LastModified)));
        p.AddWithValue("$SupportsRangeRequests", Db(i.SupportsRangeRequests is { } ranges ? (ranges ? 1 : 0) : null));
        p.AddWithValue("$AccelerationEnabled", i.AccelerationEnabled ? 1 : 0);
        p.AddWithValue("$MaxConnections", i.MaxConnections);
        p.AddWithValue("$SpeedLimit", Db(i.SpeedLimit));
        p.AddWithValue("$QueuePosition", i.QueuePosition);
        p.AddWithValue("$ExpectedSha256", Db(i.ExpectedSha256));
        p.AddWithValue("$Sha256", Db(i.Sha256));
        p.AddWithValue("$HeadersJson", Db(record.HeadersJson));
        p.AddWithValue("$CredentialKey", Db(i.CredentialKey));
        p.AddWithValue("$MediaFormat", Db(i.MediaFormat));
        command.ExecuteNonQuery();
    }

    private static void ReplaceSegments(SqliteConnection connection, SqliteTransaction transaction, DownloadRecord record)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Segments WHERE DownloadId = $id";
            delete.Parameters.AddWithValue("$id", record.Id.ToString());
            delete.ExecuteNonQuery();
        }

        if (record.Segments.Count == 0)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Segments (DownloadId, SegmentIndex, StartOffset, EndOffset, DownloadedBytes, State, RetryCount, LastError)
            VALUES ($id, $index, $start, $end, $bytes, $state, $retries, $error)
            """;
        insert.Parameters.AddWithValue("$id", record.Id.ToString());
        var index = insert.Parameters.Add("$index", SqliteType.Integer);
        var start = insert.Parameters.Add("$start", SqliteType.Integer);
        var end = insert.Parameters.Add("$end", SqliteType.Integer);
        var bytes = insert.Parameters.Add("$bytes", SqliteType.Integer);
        var state = insert.Parameters.Add("$state", SqliteType.Integer);
        var retries = insert.Parameters.Add("$retries", SqliteType.Integer);
        var error = insert.Parameters.Add("$error", SqliteType.Text);
        foreach (var segment in record.Segments)
        {
            index.Value = segment.Index;
            start.Value = segment.Start;
            end.Value = segment.End;
            bytes.Value = segment.Durable;
            state.Value = (int)(segment.Durable >= segment.End - segment.Start + 1 ? SegmentState.Completed : SegmentState.Pending);
            retries.Value = segment.RetryCount;
            error.Value = Db(segment.LastError);
            insert.ExecuteNonQuery();
        }
    }

    private static DownloadItem ReadItem(SqliteDataReader r)
    {
        var item = new DownloadItem
        {
            Id = Guid.Parse(r.GetString(r.GetOrdinal("Id"))),
            Kind = (DownloadKind)r.GetInt32(r.GetOrdinal("Kind")),
            SourceUrl = new Uri(r.GetString(r.GetOrdinal("SourceUrl"))),
            FinalUrl = ParseUri(GetNullableString(r, "FinalUrl")),
            PageUrl = ParseUri(GetNullableString(r, "PageUrl")),
            FileName = r.GetString(r.GetOrdinal("FileName")),
            AutoFileName = r.GetInt32(r.GetOrdinal("AutoFileName")) != 0,
            DestinationDirectory = r.GetString(r.GetOrdinal("DestinationDirectory")),
            TemporaryDirectory = GetNullableString(r, "TemporaryDirectory"),
            ExpectedSize = GetNullableInt64(r, "ExpectedSize"),
            DownloadedBytes = r.GetInt64(r.GetOrdinal("DownloadedBytes")),
            Status = (DownloadStatus)r.GetInt32(r.GetOrdinal("Status")),
            Priority = (DownloadPriority)r.GetInt32(r.GetOrdinal("Priority")),
            Category = (DownloadCategory)r.GetInt32(r.GetOrdinal("Category")),
            Source = (DownloadSource)r.GetInt32(r.GetOrdinal("Source")),
            ConflictPolicy = (ConflictPolicy)r.GetInt32(r.GetOrdinal("ConflictPolicy")),
            CreatedAt = ParseDate(r.GetString(r.GetOrdinal("CreatedAt")))!.Value,
            StartedAt = ParseDate(GetNullableString(r, "StartedAt")),
            CompletedAt = ParseDate(GetNullableString(r, "CompletedAt")),
            ScheduledAt = ParseDate(GetNullableString(r, "ScheduledAt")),
            DeletedAt = ParseDate(GetNullableString(r, "DeletedAt")),
            RetryCount = r.GetInt32(r.GetOrdinal("RetryCount")),
            LastError = GetNullableString(r, "LastError"),
            LastErrorKind = GetNullableInt64(r, "LastErrorKind") is { } kind ? (DownloadErrorKind)kind : null,
            HttpStatusCode = GetNullableInt64(r, "HttpStatusCode") is { } code ? (int)code : null,
            ETag = GetNullableString(r, "ETag"),
            LastModified = ParseDate(GetNullableString(r, "LastModified")),
            SupportsRangeRequests = GetNullableInt64(r, "SupportsRangeRequests") is { } ranges ? ranges != 0 : null,
            AccelerationEnabled = r.GetInt32(r.GetOrdinal("AccelerationEnabled")) != 0,
            MaxConnections = r.GetInt32(r.GetOrdinal("MaxConnections")),
            SpeedLimit = GetNullableInt64(r, "SpeedLimit"),
            QueuePosition = r.GetInt64(r.GetOrdinal("QueuePosition")),
            ExpectedSha256 = GetNullableString(r, "ExpectedSha256"),
            Sha256 = GetNullableString(r, "Sha256"),
            CredentialKey = GetNullableString(r, "CredentialKey"),
            MediaFormat = GetNullableString(r, "MediaFormat"),
        };

        if (GetNullableString(r, "HeadersJson") is { } json)
        {
            try
            {
                item.Headers = new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                // Corrupt optional data: drop the headers rather than the download.
            }
        }

        return item;
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    internal static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatDate(DateTimeOffset? value) => value is { } v ? FormatDate(v) : null;

    internal static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : null;

    private static Uri? ParseUri(string? value) => value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? GetNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}
