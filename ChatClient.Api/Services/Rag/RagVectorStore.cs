using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorStore(
    IConfiguration configuration,
    ILogger<RagVectorStore> logger) : IRagVectorStore
{
    private const string StatusInProgress = "in_progress";
    private const string StatusComplete = "complete";
    private const string StatusFailed = "failed";

    private readonly ILogger<RagVectorStore> _logger = logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private readonly string _databasePath = ResolveDatabasePath(configuration);
    private readonly string _connectionString = BuildConnectionString(ResolveDatabasePath(configuration));

    public async Task UpsertFileAsync(
        Guid agentId,
        string fileName,
        IReadOnlyList<RagVectorStoreEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = new RagVectorBuildMetadata(
            SourceHash: ComputeEntriesHash(fileName, entries),
            SourceModifiedUtc: DateTime.UtcNow,
            EmbeddingModel: "manual",
            LineChunkSize: 0,
            ParagraphChunkSize: 0,
            ParagraphOverlap: 0,
            TotalChunks: entries.Count);

        await BeginIndexingAsync(agentId, fileName, metadata, forceReset: true, cancellationToken);

        var ordered = entries.OrderBy(e => e.Index).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertEntryAsync(agentId, fileName, ordered[i], i + 1, ordered.Count, cancellationToken);
        }

        await CompleteIndexingAsync(agentId, fileName, ordered.Count, cancellationToken);
    }

    public async Task RemoveFileAsync(
        Guid agentId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM rag_file_index
            WHERE agent_id = $agentId AND file_name = $fileName;
            """;
        command.Parameters.AddWithValue("$agentId", ToAgentKey(agentId));
        command.Parameters.AddWithValue("$fileName", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> HasFileAsync(
        Guid agentId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM rag_file_index
                WHERE agent_id = $agentId
                  AND file_name = $fileName
                  AND status = $status
                  AND processed_chunks > 0
                  AND processed_chunks >= total_chunks
            );
            """;
        command.Parameters.AddWithValue("$agentId", ToAgentKey(agentId));
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$status", StatusComplete);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long exists && exists == 1L;
    }

    public async Task<IReadOnlyList<RagVectorStoreEntry>> ReadAgentEntriesAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        var result = new List<RagVectorStoreEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entries.file_name, entries.chunk_index, entries.chunk_text, entries.vector
            FROM rag_vector_entries AS entries
            INNER JOIN rag_file_index AS files
                ON entries.agent_id = files.agent_id
               AND entries.file_name = files.file_name
            WHERE entries.agent_id = $agentId
              AND files.status = $status
              AND files.processed_chunks >= files.total_chunks
            ORDER BY entries.file_name COLLATE NOCASE, entries.chunk_index;
            """;
        command.Parameters.AddWithValue("$agentId", ToAgentKey(agentId));
        command.Parameters.AddWithValue("$status", StatusComplete);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = reader.GetString(0);
            var index = reader.GetInt32(1);
            var text = reader.GetString(2);
            var vectorBlob = (byte[])reader["vector"];
            result.Add(new RagVectorStoreEntry(fileName, index, text, DeserializeVector(vectorBlob)));
        }

        return result;
    }

    public async Task<RagVectorResumePlan> BeginIndexingAsync(
        Guid agentId,
        string fileName,
        RagVectorBuildMetadata metadata,
        bool forceReset = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        var agentKey = ToAgentKey(agentId);
        var now = DateTime.UtcNow.ToString("O");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existing = await ReadIndexMetadataAsync(connection, transaction, agentKey, fileName, cancellationToken);
        var sameProfile = existing is not null && !forceReset && IsSameProfile(existing, metadata);

        if (!sameProfile)
        {
            await ClearFileEntriesAsync(connection, transaction, agentKey, fileName, cancellationToken);
            await UpsertIndexMetadataAsync(connection, transaction, agentKey, fileName, metadata, 0, StatusInProgress, null, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RagVectorResumePlan(StartIndex: 0, Rebuilt: true);
        }

        var startIndex = await GetNextChunkIndexAsync(connection, transaction, agentKey, fileName, cancellationToken);
        startIndex = Math.Clamp(startIndex, 0, metadata.TotalChunks);
        await UpsertIndexMetadataAsync(connection, transaction, agentKey, fileName, metadata, startIndex, StatusInProgress, null, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new RagVectorResumePlan(StartIndex: startIndex, Rebuilt: false);
    }

    public async Task UpsertEntryAsync(
        Guid agentId,
        string fileName,
        RagVectorStoreEntry entry,
        int processedChunks,
        int totalChunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        var agentKey = ToAgentKey(agentId);
        var now = DateTime.UtcNow.ToString("O");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO rag_vector_entries (
                    agent_id,
                    file_name,
                    chunk_index,
                    chunk_text,
                    vector,
                    created_utc
                ) VALUES (
                    $agentId,
                    $fileName,
                    $chunkIndex,
                    $chunkText,
                    $vector,
                    $createdUtc
                )
                ON CONFLICT(agent_id, file_name, chunk_index) DO UPDATE SET
                    chunk_text = excluded.chunk_text,
                    vector = excluded.vector,
                    created_utc = excluded.created_utc;
                """;
            command.Parameters.AddWithValue("$agentId", agentKey);
            command.Parameters.AddWithValue("$fileName", fileName);
            command.Parameters.AddWithValue("$chunkIndex", entry.Index);
            command.Parameters.AddWithValue("$chunkText", entry.Text ?? string.Empty);
            command.Parameters.AddWithValue("$vector", SerializeVector(entry.Vector));
            command.Parameters.AddWithValue("$createdUtc", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var progressCommand = connection.CreateCommand())
        {
            progressCommand.Transaction = transaction;
            progressCommand.CommandText = """
                UPDATE rag_file_index
                SET processed_chunks = CASE
                        WHEN processed_chunks < $processedChunks THEN $processedChunks
                        ELSE processed_chunks
                    END,
                    total_chunks = $totalChunks,
                    status = $status,
                    updated_utc = $updatedUtc,
                    last_error = NULL
                WHERE agent_id = $agentId AND file_name = $fileName;
                """;
            progressCommand.Parameters.AddWithValue("$processedChunks", processedChunks);
            progressCommand.Parameters.AddWithValue("$totalChunks", totalChunks);
            progressCommand.Parameters.AddWithValue("$status", StatusInProgress);
            progressCommand.Parameters.AddWithValue("$updatedUtc", now);
            progressCommand.Parameters.AddWithValue("$agentId", agentKey);
            progressCommand.Parameters.AddWithValue("$fileName", fileName);
            await progressCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteIndexingAsync(
        Guid agentId,
        string fileName,
        int totalChunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rag_file_index
            SET status = $status,
                total_chunks = $totalChunks,
                processed_chunks = $totalChunks,
                updated_utc = $updatedUtc,
                last_error = NULL
            WHERE agent_id = $agentId AND file_name = $fileName;
            """;
        command.Parameters.AddWithValue("$status", StatusComplete);
        command.Parameters.AddWithValue("$totalChunks", totalChunks);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$agentId", ToAgentKey(agentId));
        command.Parameters.AddWithValue("$fileName", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkIndexingFailedAsync(
        Guid agentId,
        string fileName,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rag_file_index
            SET status = $status,
                last_error = $error,
                updated_utc = $updatedUtc
            WHERE agent_id = $agentId AND file_name = $fileName;
            """;
        command.Parameters.AddWithValue("$status", StatusFailed);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$agentId", ToAgentKey(agentId));
        command.Parameters.AddWithValue("$fileName", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM rag_vector_entries;
            DELETE FROM rag_file_index;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ResolveDatabasePath(IConfiguration configuration)
    {
        var configuredDatabasePath = configuration["RagVectorStore:DatabasePath"];
        return StoragePathResolver.ResolveUserPath(
            configuration,
            configuredDatabasePath,
            FilePathConstants.DefaultRagVectorDatabaseFile);
    }

    private static string BuildConnectionString(string databasePath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS rag_file_index (
                    agent_id TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    source_hash TEXT NOT NULL,
                    source_modified_utc TEXT NOT NULL,
                    embedding_model TEXT NOT NULL,
                    line_chunk_size INTEGER NOT NULL,
                    paragraph_chunk_size INTEGER NOT NULL,
                    paragraph_overlap INTEGER NOT NULL,
                    total_chunks INTEGER NOT NULL,
                    processed_chunks INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    last_error TEXT NULL,
                    PRIMARY KEY(agent_id, file_name)
                );

                CREATE TABLE IF NOT EXISTS rag_vector_entries (
                    agent_id TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    chunk_text TEXT NOT NULL,
                    vector BLOB NOT NULL,
                    created_utc TEXT NOT NULL,
                    PRIMARY KEY(agent_id, file_name, chunk_index),
                    FOREIGN KEY(agent_id, file_name)
                        REFERENCES rag_file_index(agent_id, file_name)
                        ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_rag_entries_agent
                    ON rag_vector_entries(agent_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("RAG vector SQLite store initialized at {Path}", _databasePath);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
        await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private static async Task<int> GetNextChunkIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT IFNULL(MAX(chunk_index), -1)
            FROM rag_vector_entries
            WHERE agent_id = $agentId AND file_name = $fileName;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$fileName", fileName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var maxChunk = value is long number ? (int)number : -1;
        return maxChunk + 1;
    }

    private static async Task<IndexMetadataRow?> ReadIndexMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_hash,
                   embedding_model,
                   line_chunk_size,
                   paragraph_chunk_size,
                   paragraph_overlap,
                    total_chunks
            FROM rag_file_index
            WHERE agent_id = $agentId AND file_name = $fileName;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$fileName", fileName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IndexMetadataRow(
            SourceHash: reader.GetString(0),
            EmbeddingModel: reader.GetString(1),
            LineChunkSize: reader.GetInt32(2),
            ParagraphChunkSize: reader.GetInt32(3),
            ParagraphOverlap: reader.GetInt32(4),
            TotalChunks: reader.GetInt32(5));
    }

    private static bool IsSameProfile(IndexMetadataRow existing, RagVectorBuildMetadata metadata) =>
        existing.SourceHash == metadata.SourceHash &&
        existing.EmbeddingModel == metadata.EmbeddingModel &&
        existing.LineChunkSize == metadata.LineChunkSize &&
        existing.ParagraphChunkSize == metadata.ParagraphChunkSize &&
        existing.ParagraphOverlap == metadata.ParagraphOverlap &&
        existing.TotalChunks == metadata.TotalChunks;

    private static async Task ClearFileEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM rag_vector_entries
            WHERE agent_id = $agentId AND file_name = $fileName;
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$fileName", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertIndexMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        string fileName,
        RagVectorBuildMetadata metadata,
        int processedChunks,
        string status,
        string? error,
        string now,
        CancellationToken cancellationToken)
    {
        var normalizedAgentId = RequireText(agentId, "Agent id");
        var normalizedFileName = RequireText(fileName, "File name");
        var normalizedSourceHash = RequireText(metadata.SourceHash, "Source hash");
        var normalizedEmbeddingModel = RequireText(metadata.EmbeddingModel, "Embedding model");
        var normalizedStatus = RequireText(status, "Index status");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rag_file_index (
                agent_id,
                file_name,
                source_hash,
                source_modified_utc,
                embedding_model,
                line_chunk_size,
                paragraph_chunk_size,
                paragraph_overlap,
                total_chunks,
                processed_chunks,
                status,
                created_utc,
                updated_utc,
                last_error
            ) VALUES (
                $agentId,
                $fileName,
                $sourceHash,
                $sourceModifiedUtc,
                $embeddingModel,
                $lineChunkSize,
                $paragraphChunkSize,
                $paragraphOverlap,
                $totalChunks,
                $processedChunks,
                $status,
                $createdUtc,
                $updatedUtc,
                $lastError
            )
            ON CONFLICT(agent_id, file_name) DO UPDATE SET
                source_hash = excluded.source_hash,
                source_modified_utc = excluded.source_modified_utc,
                embedding_model = excluded.embedding_model,
                line_chunk_size = excluded.line_chunk_size,
                paragraph_chunk_size = excluded.paragraph_chunk_size,
                paragraph_overlap = excluded.paragraph_overlap,
                total_chunks = excluded.total_chunks,
                processed_chunks = excluded.processed_chunks,
                status = excluded.status,
                updated_utc = excluded.updated_utc,
                last_error = excluded.last_error;
            """;
        command.Parameters.AddWithValue("$agentId", normalizedAgentId);
        command.Parameters.AddWithValue("$fileName", normalizedFileName);
        command.Parameters.AddWithValue("$sourceHash", normalizedSourceHash);
        command.Parameters.AddWithValue("$sourceModifiedUtc", metadata.SourceModifiedUtc.ToString("O"));
        command.Parameters.AddWithValue("$embeddingModel", normalizedEmbeddingModel);
        command.Parameters.AddWithValue("$lineChunkSize", metadata.LineChunkSize);
        command.Parameters.AddWithValue("$paragraphChunkSize", metadata.ParagraphChunkSize);
        command.Parameters.AddWithValue("$paragraphOverlap", metadata.ParagraphOverlap);
        command.Parameters.AddWithValue("$totalChunks", metadata.TotalChunks);
        command.Parameters.AddWithValue("$processedChunks", processedChunks);
        command.Parameters.AddWithValue("$status", normalizedStatus);
        command.Parameters.AddWithValue("$createdUtc", now);
        command.Parameters.AddWithValue("$updatedUtc", now);
        command.Parameters.AddWithValue("$lastError", error is null ? DBNull.Value : error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToAgentKey(Guid agentId) => agentId.ToString("N");

    private static string RequireText(string? value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"{name} is missing for RAG vector persistence.");
    }

    private static string ComputeEntriesHash(string fileName, IReadOnlyList<RagVectorStoreEntry> entries)
    {
        var builder = new StringBuilder(fileName.Length + entries.Count * 16);
        builder.Append(fileName);

        foreach (var entry in entries.OrderBy(e => e.Index))
        {
            builder.Append('|');
            builder.Append(entry.Index);
            builder.Append(':');
            builder.Append(entry.Text ?? string.Empty);
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static byte[] SerializeVector(IReadOnlyList<float> vector)
    {
        var data = vector.ToArray();
        var bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeVector(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private sealed record IndexMetadataRow(
        string SourceHash,
        string EmbeddingModel,
        int LineChunkSize,
        int ParagraphChunkSize,
        int ParagraphOverlap,
        int TotalChunks);
}
