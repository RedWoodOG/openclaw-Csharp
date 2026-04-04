namespace OpenClaw.Session;

using OpenClaw.Core;
using System.Text.Json;

/// <summary>
/// Session persistence interface.
/// </summary>
public interface ISessionPersistence
{
    Task SaveSessionAsync(Session session, CancellationToken ct = default);
    Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionListEntry>> ListSessionsAsync(CancellationToken ct = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Session list entry for listing sessions.
/// </summary>
public sealed record SessionListEntry(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? Title);

/// <summary>
/// File-based session persistence.
/// </summary>
public sealed class FileSessionPersistence : ISessionPersistence
{
    private readonly string _sessionsPath;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    public FileSessionPersistence(string? sessionsPath = null)
    {
        _sessionsPath = sessionsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw",
            "sessions");
        
        if (!Directory.Exists(_sessionsPath))
            Directory.CreateDirectory(_sessionsPath);
    }
    
    public async Task SaveSessionAsync(Session session, CancellationToken ct = default)
    {
        var filePath = GetSessionFilePath(session.Id);
        var json = JsonSerializer.Serialize(session, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }
    
    public async Task<Session?> LoadSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var filePath = GetSessionFilePath(sessionId);
        if (!File.Exists(filePath))
            return null;
        
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<Session>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
    
    public Task<IReadOnlyList<SessionListEntry>> ListSessionsAsync(CancellationToken ct = default)
    {
        var files = Directory.GetFiles(_sessionsPath, "*.json");
        var entries = new List<SessionListEntry>();
        
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllTextAsync(file, ct).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                var id = Path.GetFileNameWithoutExtension(file);
                var createdAt = root.TryGetProperty("createdAt", out var ca) 
                    ? ca.GetDateTimeOffset() 
                    : DateTimeOffset.MinValue;
                var updatedAt = root.TryGetProperty("updatedAt", out var ua)
                    ? ua.GetDateTimeOffset()
                    : DateTimeOffset.MinValue;
                var messages = root.TryGetProperty("messages", out var msgs)
                    ? msgs.GetArrayLength()
                    : 0;
                
                // Try to get title from first user message
                string? title = null;
                if (root.TryGetProperty("messages", out var messagesArr) && messagesArr.GetArrayLength() > 0)
                {
                    var first = messagesArr[0];
                    if (first.TryGetProperty("content", out var content))
                    {
                        title = content.GetString();
                        if (title?.Length > 50)
                            title = title.Substring(0, 50) + "...";
                    }
                }
                
                entries.Add(new SessionListEntry(id, createdAt, updatedAt, messages, title));
            }
            catch { }
        }
        
        return Task.FromResult<IReadOnlyList<SessionListEntry>>(entries
            .OrderByDescending(s => s.UpdatedAt)
            .ToList());
    }
    
    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var filePath = GetSessionFilePath(sessionId);
        if (File.Exists(filePath))
            File.Delete(filePath);
        
        return Task.CompletedTask;
    }
    
    private string GetSessionFilePath(string sessionId) => Path.Combine(_sessionsPath, $"{sessionId}.json");
}

/// <summary>
/// Session manager.
/// </summary>
public sealed class SessionManager
{
    private readonly ISessionPersistence _persistence;
    private readonly Dictionary<string, Session> _active = new();
    
    public SessionManager(ISessionPersistence persistence) => _persistence = persistence;
    
    public Session CreateSession(string? workingDirectory = null, PermissionMode permissionMode = PermissionMode.Default)
    {
        var session = new Session
        {
            Metadata = new SessionMetadata
            {
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
                PermissionMode = permissionMode
            }
        };
        
        _active[session.Id] = session;
        return session;
    }
    
    public async Task<Session?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_active.TryGetValue(sessionId, out var cached))
            return cached;
        
        var loaded = await _persistence.LoadSessionAsync(sessionId, ct);
        if (loaded is not null)
            _active[sessionId] = loaded;
        
        return loaded;
    }
    
    public async Task SaveSessionAsync(Session session, CancellationToken ct = default)
    {
        await _persistence.SaveSessionAsync(session, ct);
    }
    
    public async Task<IReadOnlyList<SessionListEntry>> ListSessionsAsync(CancellationToken ct = default)
    {
        return await _persistence.ListSessionsAsync(ct);
    }
    
    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _active.Remove(sessionId);
        await _persistence.DeleteSessionAsync(sessionId, ct);
    }
    
    public void CloseSession(string sessionId)
    {
        _active.Remove(sessionId);
    }
}
