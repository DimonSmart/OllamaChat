using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;

namespace ChatClient.Api.Services.Rag;

public sealed class RagIndexMetadataStore(IConfiguration configuration, ILogger<RagIndexMetadataStore> logger) : IRagIndexMetadataStore
{
    private const int CurrentSchemaVersion = 3;
    private const string Complete = "complete";
    private const string InProgress = "in_progress";
    private const string Failed = "failed";
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string _databasePath = StoragePathResolver.ResolveUserPath(configuration, configuration["RagVectorStore:DatabasePath"], FilePathConstants.DefaultRagVectorDatabaseFile);
    private bool _initialized;

    public async Task RemoveFileAsync(Guid agentId, string fileName, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("DELETE FROM rag_file_index WHERE agent_id = $agentId AND file_name = $fileName;", agentId, fileName, cancellationToken);
    }

    public async Task<bool> HasFileAsync(Guid agentId, string fileName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM rag_file_index WHERE agent_id = $agentId AND file_name = $fileName AND status = $status AND processed_chunks >= total_chunks AND total_chunks > 0);";
        command.Parameters.AddWithValue("$agentId", agentId.ToString("N"));
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$status", Complete);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
    }

    public async Task<RagIndexResumePlan> BeginIndexingAsync(Guid agentId, string fileName, RagIndexBuildMetadata metadata, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_hash, embedding_model, embedding_dimension, max_tokens, overlap_tokens, ingestion_version, processed_chunks, total_chunks FROM rag_file_index WHERE agent_id = $agentId AND file_name = $fileName;";
        command.Parameters.AddWithValue("$agentId", agentId.ToString("N"));
        command.Parameters.AddWithValue("$fileName", fileName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var matches = false;
        var start = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            matches = reader.GetString(0) == metadata.SourceHash && reader.GetString(1) == metadata.EmbeddingModel && reader.GetInt32(2) == metadata.EmbeddingDimension && reader.GetInt32(3) == metadata.MaxTokensPerChunk && reader.GetInt32(4) == metadata.OverlapTokens && reader.GetString(5) == metadata.IngestionVersion && reader.GetInt32(7) == metadata.TotalChunks;
            start = matches ? Math.Clamp(reader.GetInt32(6), 0, metadata.TotalChunks) : 0;
        }
        await reader.DisposeAsync();
        await UpsertAsync(connection, agentId, fileName, metadata, start, InProgress, null, cancellationToken);
        return new RagIndexResumePlan(start, !matches);
    }

    public Task ReportProgressAsync(Guid agentId, string fileName, int processedChunks, CancellationToken cancellationToken = default) =>
        ExecuteAsync("UPDATE rag_file_index SET processed_chunks = MAX(processed_chunks, $processed), updated_utc = $now WHERE agent_id = $agentId AND file_name = $fileName;", agentId, fileName, cancellationToken, processedChunks);

    public Task CompleteIndexingAsync(Guid agentId, string fileName, int totalChunks, CancellationToken cancellationToken = default) =>
        ExecuteAsync("UPDATE rag_file_index SET status = $status, processed_chunks = $total, total_chunks = $total, last_error = NULL, updated_utc = $now WHERE agent_id = $agentId AND file_name = $fileName;", agentId, fileName, cancellationToken, totalChunks, Complete);

    public Task MarkIndexingFailedAsync(Guid agentId, string fileName, string error, CancellationToken cancellationToken = default) =>
        ExecuteAsync("UPDATE rag_file_index SET status = $status, last_error = $error, updated_utc = $now WHERE agent_id = $agentId AND file_name = $fileName;", agentId, fileName, cancellationToken, 0, Failed, error);

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rag_file_index;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(string sql, Guid agentId, string fileName, CancellationToken cancellationToken, int value = 0, string? status = null, string? error = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$agentId", agentId.ToString("N"));
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$processed", value);
        command.Parameters.AddWithValue("$total", value);
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertAsync(SqliteConnection connection, Guid agentId, string fileName, RagIndexBuildMetadata metadata, int processed, string status, string? error, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """INSERT INTO rag_file_index (agent_id,file_name,source_hash,source_modified_utc,embedding_model,embedding_dimension,max_tokens,overlap_tokens,ingestion_version,total_chunks,processed_chunks,status,created_utc,updated_utc,last_error) VALUES ($agentId,$fileName,$hash,$modified,$model,$dimension,$maxTokens,$overlap,$version,$total,$processed,$status,$now,$now,$error) ON CONFLICT(agent_id,file_name) DO UPDATE SET source_hash=excluded.source_hash,source_modified_utc=excluded.source_modified_utc,embedding_model=excluded.embedding_model,embedding_dimension=excluded.embedding_dimension,max_tokens=excluded.max_tokens,overlap_tokens=excluded.overlap_tokens,ingestion_version=excluded.ingestion_version,total_chunks=excluded.total_chunks,processed_chunks=excluded.processed_chunks,status=excluded.status,updated_utc=excluded.updated_utc,last_error=excluded.last_error;""";
        command.Parameters.AddWithValue("$agentId", agentId.ToString("N"));
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$hash", metadata.SourceHash);
        command.Parameters.AddWithValue("$modified", metadata.SourceModifiedUtc.ToString("O"));
        command.Parameters.AddWithValue("$model", metadata.EmbeddingModel);
        command.Parameters.AddWithValue("$dimension", metadata.EmbeddingDimension);
        command.Parameters.AddWithValue("$maxTokens", metadata.MaxTokensPerChunk);
        command.Parameters.AddWithValue("$overlap", metadata.OverlapTokens);
        command.Parameters.AddWithValue("$version", metadata.IngestionVersion);
        command.Parameters.AddWithValue("$total", metadata.TotalChunks);
        command.Parameters.AddWithValue("$processed", processed);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS rag_index_schema (version INTEGER NOT NULL); SELECT version FROM rag_index_schema LIMIT 1;";
            var schemaVersion = await command.ExecuteScalarAsync(cancellationToken) as long?;
            if (schemaVersion != CurrentSchemaVersion)
            {
                command.CommandText = $"""DROP TABLE IF EXISTS rag_chunks; DROP TABLE IF EXISTS rag_vector_entries; DROP TABLE IF EXISTS rag_file_index; DELETE FROM rag_index_schema; CREATE TABLE rag_file_index (agent_id TEXT NOT NULL,file_name TEXT NOT NULL,source_hash TEXT NOT NULL,source_modified_utc TEXT NOT NULL,embedding_model TEXT NOT NULL,embedding_dimension INTEGER NOT NULL,max_tokens INTEGER NOT NULL,overlap_tokens INTEGER NOT NULL,ingestion_version TEXT NOT NULL,total_chunks INTEGER NOT NULL,processed_chunks INTEGER NOT NULL,status TEXT NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,last_error TEXT NULL,PRIMARY KEY(agent_id,file_name)); INSERT INTO rag_index_schema(version) VALUES ({CurrentSchemaVersion});""";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            logger.LogInformation("RAG indexing metadata store initialized at {Path}", _databasePath);
            _initialized = true;
        }
        finally { _initializationLock.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()); await connection.OpenAsync(cancellationToken); return connection; }
}
