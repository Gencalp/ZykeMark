using System.Text.Json;
using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;

namespace ZykeMark.Core.Services;

public sealed class FileSystemLocalStore : ILocalStore
{
    private readonly string _rootPath;
    private readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

    public FileSystemLocalStore(string? basePath = null)
    {
        _rootPath = string.IsNullOrWhiteSpace(basePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZykeMark",
                "sessions")
            : basePath;
    }

    public string CreateSession(SessionMetadata metadata)
    {
        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        var sessionFolder = GetSessionFolder(metadata.SessionId);

        if (Directory.Exists(sessionFolder))
        {
            throw new InvalidOperationException($"Session folder already exists for '{metadata.SessionId}'.");
        }

        Directory.CreateDirectory(sessionFolder);
        Directory.CreateDirectory(GetChunksFolder(metadata.SessionId));

        WriteMetadata(metadata);

        return sessionFolder;
    }

    public void WriteMetadata(SessionMetadata metadata)
    {
        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        var sessionFolder = GetSessionFolder(metadata.SessionId);
        Directory.CreateDirectory(sessionFolder);

        var metadataPath = Path.Combine(sessionFolder, "metadata.json");
        WriteJson(metadataPath, metadata);
    }

    public SessionMetadata ReadMetadata(string sessionId)
    {
        var metadataPath = Path.Combine(GetSessionFolder(sessionId), "metadata.json");

        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("Metadata file not found.", metadataPath);
        }

        using var stream = File.OpenRead(metadataPath);
        var metadata = JsonSerializer.Deserialize<SessionMetadata>(stream, _serializerOptions);

        return metadata ?? throw new InvalidOperationException("Metadata file could not be deserialized.");
    }

    public void AppendChunk(string sessionId, RawSampleChunk chunk)
    {
        if (chunk is null)
        {
            throw new ArgumentNullException(nameof(chunk));
        }

        var chunksFolder = GetChunksFolder(sessionId);
        Directory.CreateDirectory(chunksFolder);

        var chunkPath = Path.Combine(chunksFolder, $"chunk_{chunk.ChunkIndex:D4}.json");
        WriteJson(chunkPath, chunk);
    }

    public IReadOnlyList<RawSampleChunk> ReadChunks(string sessionId)
    {
        var chunksFolder = GetChunksFolder(sessionId);

        if (!Directory.Exists(chunksFolder))
        {
            return Array.Empty<RawSampleChunk>();
        }

        var chunkFiles = Directory.GetFiles(chunksFolder, "chunk_*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var chunks = new List<RawSampleChunk>(chunkFiles.Length);

        foreach (var chunkFile in chunkFiles)
        {
            using var stream = File.OpenRead(chunkFile);
            var chunk = JsonSerializer.Deserialize<RawSampleChunk>(stream, _serializerOptions);

            if (chunk is null)
            {
                throw new InvalidOperationException($"Chunk file '{chunkFile}' could not be deserialized.");
            }

            chunks.Add(chunk);
        }

        return chunks;
    }

    public string WriteSummary(string sessionId, object summaryPayload)
    {
        if (summaryPayload is null)
        {
            throw new ArgumentNullException(nameof(summaryPayload));
        }

        var sessionFolder = GetSessionFolder(sessionId);
        Directory.CreateDirectory(sessionFolder);

        var summaryPath = Path.Combine(sessionFolder, "summary.json");
        WriteJson(summaryPath, summaryPayload);

        return summaryPath;
    }

    private void WriteJson<T>(string path, T payload)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, payload, _serializerOptions);
    }

    private string GetSessionFolder(string sessionId) => Path.Combine(_rootPath, sessionId);

    private string GetChunksFolder(string sessionId) => Path.Combine(GetSessionFolder(sessionId), "chunks");
}
