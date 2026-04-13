using ChatClient.Application.Services.TaskSessions;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace ChatClient.Infrastructure.Services.TaskSessions;

public sealed class SqliteTaskSessionRepository : ITaskSessionRepository
{
    public async Task<TaskSessionSnapshot> CreateSessionAsync(
        string databaseFilePath,
        string sessionId,
        string? title,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var now = DateTime.UtcNow;

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO task_sessions (session_id, title, description, phase, status, created_at_utc, updated_at_utc)
            VALUES ($sessionId, $title, $description, NULL, 'active', $createdAtUtc, $updatedAtUtc);
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$title", (object?)NormalizeOptional(title) ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)NormalizeOptional(description) ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetSessionAsync(normalizedDatabaseFilePath, normalizedSessionId, cancellationToken);
    }

    public async Task<TaskSessionSnapshot> GetSessionAsync(
        string databaseFilePath,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, title, description, phase, status, created_at_utc, updated_at_utc
            FROM task_sessions
            WHERE session_id = $sessionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("session_not_found");
        }

        var documents = await ListDocumentInfoAsync(connection, normalizedSessionId, cancellationToken);
        var parameters = await ListParameterInfoAsync(connection, normalizedSessionId, cancellationToken);
        var summaries = await ListSummaryInfoAsync(connection, normalizedSessionId, cancellationToken);
        var turnCount = await GetTurnCountAsync(connection, normalizedSessionId, cancellationToken);

        return new TaskSessionSnapshot(
            SessionId: reader.GetString(0),
            Title: ReadNullableString(reader, 1),
            Description: ReadNullableString(reader, 2),
            Phase: ReadNullableString(reader, 3),
            Status: reader.GetString(4),
            CreatedAtUtc: ReadDateTime(reader, 5),
            UpdatedAtUtc: ReadDateTime(reader, 6),
            TurnCount: turnCount,
            Documents: documents,
            Parameters: parameters,
            Summaries: summaries);
    }

    public async Task<TaskSessionSnapshot> SetPhaseAsync(
        string databaseFilePath,
        string sessionId,
        string phase,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedPhase = NormalizeRequired(phase, "phase_required");

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await EnsureSessionExistsAsync(connection, normalizedSessionId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE task_sessions
            SET phase = $phase,
                updated_at_utc = $updatedAtUtc
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$phase", normalizedPhase);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetSessionAsync(normalizedDatabaseFilePath, normalizedSessionId, cancellationToken);
    }

    public async Task<TaskSessionDocumentSnapshot> UpsertDocumentAsync(
        string databaseFilePath,
        string sessionId,
        string kind,
        string markdown,
        string? title,
        string? source,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedKind = NormalizeRequired(kind, "document_kind_required");
        var normalizedMarkdown = NormalizeRequired(markdown, "document_markdown_required");
        var now = DateTime.UtcNow;

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await EnsureSessionExistsAsync(connection, normalizedSessionId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO task_session_documents (session_id, kind, title, markdown, source, created_at_utc, updated_at_utc)
            VALUES ($sessionId, $kind, $title, $markdown, $source, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(session_id, kind) DO UPDATE SET
                title = excluded.title,
                markdown = excluded.markdown,
                source = excluded.source,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$kind", normalizedKind);
        command.Parameters.AddWithValue("$title", (object?)NormalizeOptional(title) ?? DBNull.Value);
        command.Parameters.AddWithValue("$markdown", normalizedMarkdown);
        command.Parameters.AddWithValue("$source", (object?)NormalizeOptional(source) ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetDocumentAsync(normalizedDatabaseFilePath, normalizedSessionId, normalizedKind, cancellationToken);
    }

    public async Task<TaskSessionDocumentSnapshot> GetDocumentAsync(
        string databaseFilePath,
        string sessionId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedKind = NormalizeRequired(kind, "document_kind_required");

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, kind, title, markdown, source, created_at_utc, updated_at_utc
            FROM task_session_documents
            WHERE session_id = $sessionId
              AND kind = $kind
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$kind", normalizedKind);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("document_not_found");
        }

        return new TaskSessionDocumentSnapshot(
            SessionId: reader.GetString(0),
            Kind: reader.GetString(1),
            Title: ReadNullableString(reader, 2),
            Markdown: reader.GetString(3),
            Source: ReadNullableString(reader, 4),
            CreatedAtUtc: ReadDateTime(reader, 5),
            UpdatedAtUtc: ReadDateTime(reader, 6));
    }

    public async Task<TaskSessionParameterSnapshot> UpsertParameterAsync(
        string databaseFilePath,
        string sessionId,
        string key,
        string valueKind,
        string value,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedKey = NormalizeRequired(key, "parameter_key_required");
        var normalizedValueKind = NormalizeRequired(valueKind, "parameter_value_kind_required");
        var normalizedValue = NormalizeRequired(value, "parameter_value_required");
        var now = DateTime.UtcNow;

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await EnsureSessionExistsAsync(connection, normalizedSessionId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO task_session_parameters (session_id, parameter_key, value_kind, value_text, created_at_utc, updated_at_utc)
            VALUES ($sessionId, $key, $valueKind, $value, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(session_id, parameter_key) DO UPDATE SET
                value_kind = excluded.value_kind,
                value_text = excluded.value_text,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$key", normalizedKey);
        command.Parameters.AddWithValue("$valueKind", normalizedValueKind);
        command.Parameters.AddWithValue("$value", normalizedValue);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetParameterAsync(normalizedDatabaseFilePath, normalizedSessionId, normalizedKey, cancellationToken);
    }

    public async Task<TaskSessionParameterSnapshot> GetParameterAsync(
        string databaseFilePath,
        string sessionId,
        string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedKey = NormalizeRequired(key, "parameter_key_required");

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, parameter_key, value_kind, value_text, created_at_utc, updated_at_utc
            FROM task_session_parameters
            WHERE session_id = $sessionId
              AND parameter_key = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$key", normalizedKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("parameter_not_found");
        }

        return new TaskSessionParameterSnapshot(
            SessionId: reader.GetString(0),
            Key: reader.GetString(1),
            ValueKind: reader.GetString(2),
            Value: reader.GetString(3),
            CreatedAtUtc: ReadDateTime(reader, 4),
            UpdatedAtUtc: ReadDateTime(reader, 5));
    }

    public async Task<TaskSessionTurnSnapshot> AppendTurnAsync(
        string databaseFilePath,
        string sessionId,
        string role,
        string content,
        string? speakerId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedRole = NormalizeRequired(role, "turn_role_required");
        var normalizedContent = NormalizeRequired(content, "turn_content_required");
        var now = DateTime.UtcNow;

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await using var beginCommand = connection.CreateCommand();
        beginCommand.CommandText = "BEGIN IMMEDIATE;";
        await beginCommand.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await EnsureSessionExistsAsync(connection, normalizedSessionId, cancellationToken);
            var sequence = await GetNextSequenceAsync(connection, normalizedSessionId, cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO task_session_turns (session_id, sequence, role, speaker_id, content, created_at_utc)
                VALUES ($sessionId, $sequence, $role, $speakerId, $content, $createdAtUtc);

                UPDATE task_sessions
                SET updated_at_utc = $updatedAtUtc
                WHERE session_id = $sessionId;
                """;
            command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$role", normalizedRole);
            command.Parameters.AddWithValue("$speakerId", (object?)NormalizeOptional(speakerId) ?? DBNull.Value);
            command.Parameters.AddWithValue("$content", normalizedContent);
            command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var commitCommand = connection.CreateCommand();
            commitCommand.CommandText = "COMMIT;";
            await commitCommand.ExecuteNonQueryAsync(cancellationToken);

            return new TaskSessionTurnSnapshot(
                SessionId: normalizedSessionId,
                Sequence: sequence,
                Role: normalizedRole,
                SpeakerId: NormalizeOptional(speakerId),
                Content: normalizedContent,
                CreatedAtUtc: now);
        }
        catch
        {
            try
            {
                await using var rollbackCommand = connection.CreateCommand();
                rollbackCommand.CommandText = "ROLLBACK;";
                await rollbackCommand.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<TaskSessionTurnSnapshot>> ListTurnsAsync(
        string databaseFilePath,
        string sessionId,
        long afterSequence,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var effectiveAfterSequence = Math.Max(0, afterSequence);
        var effectiveMaxCount = Math.Clamp(maxCount, 1, 200);

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await EnsureSessionExistsAsync(connection, normalizedSessionId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, sequence, role, speaker_id, content, created_at_utc
            FROM task_session_turns
            WHERE session_id = $sessionId
              AND sequence > $afterSequence
            ORDER BY sequence ASC
            LIMIT $maxCount;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$afterSequence", effectiveAfterSequence);
        command.Parameters.AddWithValue("$maxCount", effectiveMaxCount);

        List<TaskSessionTurnSnapshot> turns = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            turns.Add(new TaskSessionTurnSnapshot(
                SessionId: reader.GetString(0),
                Sequence: reader.GetInt64(1),
                Role: reader.GetString(2),
                SpeakerId: ReadNullableString(reader, 3),
                Content: reader.GetString(4),
                CreatedAtUtc: ReadDateTime(reader, 5)));
        }

        return turns;
    }

    public async Task<TaskSessionSummarySnapshot> SaveSummaryAsync(
        string databaseFilePath,
        string sessionId,
        string label,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedLabel = NormalizeRequired(label, "summary_label_required");
        var normalizedMarkdown = NormalizeRequired(markdown, "summary_markdown_required");
        var now = DateTime.UtcNow;

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await EnsureSessionExistsAsync(connection, normalizedSessionId, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO task_session_summaries (session_id, label, markdown, created_at_utc, updated_at_utc)
            VALUES ($sessionId, $label, $markdown, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(session_id, label) DO UPDATE SET
                markdown = excluded.markdown,
                updated_at_utc = excluded.updated_at_utc;

            UPDATE task_sessions
            SET updated_at_utc = $updatedAtUtc
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$label", normalizedLabel);
        command.Parameters.AddWithValue("$markdown", normalizedMarkdown);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new TaskSessionSummarySnapshot(
            SessionId: normalizedSessionId,
            Label: normalizedLabel,
            Markdown: normalizedMarkdown,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
    }

    public async Task<TaskSessionSummarySnapshot> GetSummaryAsync(
        string databaseFilePath,
        string sessionId,
        string label,
        CancellationToken cancellationToken = default)
    {
        var normalizedDatabaseFilePath = NormalizeDatabaseFilePath(databaseFilePath);
        var normalizedSessionId = NormalizeRequired(sessionId, "session_id_required");
        var normalizedLabel = NormalizeRequired(label, "summary_label_required");

        await using var connection = await OpenConnectionAsync(normalizedDatabaseFilePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, label, markdown, created_at_utc, updated_at_utc
            FROM task_session_summaries
            WHERE session_id = $sessionId
              AND label = $label
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", normalizedSessionId);
        command.Parameters.AddWithValue("$label", normalizedLabel);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("summary_not_found");
        }

        return new TaskSessionSummarySnapshot(
            SessionId: reader.GetString(0),
            Label: reader.GetString(1),
            Markdown: reader.GetString(2),
            CreatedAtUtc: ReadDateTime(reader, 3),
            UpdatedAtUtc: ReadDateTime(reader, 4));
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string databaseFilePath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databaseFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            ForeignKeys = true
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureInitializedAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task EnsureInitializedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS task_sessions (
                session_id TEXT NOT NULL PRIMARY KEY,
                title TEXT NULL,
                description TEXT NULL,
                phase TEXT NULL,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS task_session_documents (
                session_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NULL,
                markdown TEXT NOT NULL,
                source TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (session_id, kind),
                FOREIGN KEY (session_id) REFERENCES task_sessions(session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS task_session_turns (
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                role TEXT NOT NULL,
                speaker_id TEXT NULL,
                content TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                PRIMARY KEY (session_id, sequence),
                FOREIGN KEY (session_id) REFERENCES task_sessions(session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS task_session_parameters (
                session_id TEXT NOT NULL,
                parameter_key TEXT NOT NULL,
                value_kind TEXT NOT NULL,
                value_text TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (session_id, parameter_key),
                FOREIGN KEY (session_id) REFERENCES task_sessions(session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS task_session_summaries (
                session_id TEXT NOT NULL,
                label TEXT NOT NULL,
                markdown TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (session_id, label),
                FOREIGN KEY (session_id) REFERENCES task_sessions(session_id) ON DELETE CASCADE
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSessionExistsAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(1)
            FROM task_sessions
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (count == 0)
        {
            throw new InvalidOperationException("session_not_found");
        }
    }

    private static async Task<long> GetNextSequenceAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(sequence), 0)
            FROM task_session_turns
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var currentMax = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return currentMax + 1;
    }

    private static async Task<int> GetTurnCountAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(1)
            FROM task_session_turns
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<IReadOnlyList<TaskSessionDocumentInfo>> ListDocumentInfoAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT kind, title, source, created_at_utc, updated_at_utc
            FROM task_session_documents
            WHERE session_id = $sessionId
            ORDER BY kind ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        List<TaskSessionDocumentInfo> documents = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new TaskSessionDocumentInfo(
                Kind: reader.GetString(0),
                Title: ReadNullableString(reader, 1),
                Source: ReadNullableString(reader, 2),
                CreatedAtUtc: ReadDateTime(reader, 3),
                UpdatedAtUtc: ReadDateTime(reader, 4)));
        }

        return documents;
    }

    private static async Task<IReadOnlyList<TaskSessionParameterInfo>> ListParameterInfoAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT parameter_key, value_kind, created_at_utc, updated_at_utc
            FROM task_session_parameters
            WHERE session_id = $sessionId
            ORDER BY parameter_key ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        List<TaskSessionParameterInfo> parameters = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            parameters.Add(new TaskSessionParameterInfo(
                Key: reader.GetString(0),
                ValueKind: reader.GetString(1),
                CreatedAtUtc: ReadDateTime(reader, 2),
                UpdatedAtUtc: ReadDateTime(reader, 3)));
        }

        return parameters;
    }

    private static async Task<IReadOnlyList<TaskSessionSummaryInfo>> ListSummaryInfoAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT label, created_at_utc, updated_at_utc
            FROM task_session_summaries
            WHERE session_id = $sessionId
            ORDER BY label ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        List<TaskSessionSummaryInfo> summaries = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new TaskSessionSummaryInfo(
                Label: reader.GetString(0),
                CreatedAtUtc: ReadDateTime(reader, 1),
                UpdatedAtUtc: ReadDateTime(reader, 2)));
        }

        return summaries;
    }

    private static string NormalizeDatabaseFilePath(string databaseFilePath)
    {
        var normalized = NormalizeRequired(databaseFilePath, "database_file_required");
        return Path.GetFullPath(normalized);
    }

    private static string NormalizeRequired(string? value, string code)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(code);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime ReadDateTime(SqliteDataReader reader, int ordinal) =>
        DateTime.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
