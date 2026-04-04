namespace OpenClaw.Memory;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Memory entry.
/// </summary>
public sealed class MemoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public double RelevanceScore { get; set; }
    public string? Source { get; init; }
}

/// <summary>
/// Memory manager interface.
/// </summary>
public interface IMemoryManager
{
    Task<IReadOnlyList<MemoryEntry>> LoadRelevantAsync(string query, int maxResults = 10, CancellationToken ct = default);
    Task SaveAsync(MemoryEntry entry, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> GetAllAsync(CancellationToken ct = default);
}

/// <summary>
/// File-based memory manager.
/// </summary>
public sealed class FileMemoryManager : IMemoryManager
{
    private readonly string _memoryPath;
    private readonly IRelevanceScorer _scorer;
    private readonly List<MemoryEntry> _cache = new();
    private bool _loaded;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    public FileMemoryManager(string? memoryPath = null, IRelevanceScorer? scorer = null)
    {
        _memoryPath = memoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "memory");
        _scorer = scorer ?? new SimpleRelevanceScorer();
        
        if (!Directory.Exists(_memoryPath))
            Directory.CreateDirectory(_memoryPath);
    }
    
    public async Task<IReadOnlyList<MemoryEntry>> LoadRelevantAsync(string query, int maxResults = 10, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        
        var scored = _cache.Select(m => (m, score: _scorer.Score(m, query)))
            .OrderByDescending(x => x.score)
            .Take(maxResults)
            .Select(x => { x.m.RelevanceScore = x.score; return x.m; })
            .ToList();
        
        return scored;
    }
    
    public async Task SaveAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        
        var existing = _cache.FirstOrDefault(m => m.Id == entry.Id);
        if (existing is not null)
        {
            _cache.Remove(existing);
        }
        
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        _cache.Add(entry);
        await PersistAsync(ct);
    }
    
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _cache.RemoveAll(m => m.Id == id);
        await PersistAsync(ct);
    }
    
    public async Task<IReadOnlyList<MemoryEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _cache.ToList();
    }
    
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        
        _cache.Clear();
        
        foreach (var file in Directory.GetFiles(_memoryPath, "*.md"))
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                var entry = ParseMemoryFile(file, content);
                _cache.Add(entry);
            }
            catch { }
        }
        
        foreach (var file in Directory.GetFiles(_memoryPath, "*.json"))
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                var entry = JsonSerializer.Deserialize<MemoryEntry>(content, JsonOptions);
                if (entry is not null)
                    _cache.Add(entry);
            }
            catch { }
        }
        
        _loaded = true;
    }
    
    private static MemoryEntry ParseMemoryFile(string path, string content)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        var title = "";
        var tags = new List<string>();
        var body = content;
        
        // Parse frontmatter
        if (content.StartsWith("---"))
        {
            var end = content.IndexOf("---", 3);
            if (end > 0)
            {
                var frontmatter = content.Substring(3, end - 3);
                body = content.Substring(end + 3).Trim();
                
                foreach (var line in frontmatter.Split('\n'))
                {
                    if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                        title = line.Substring(6).Trim();
                    else if (line.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                        tags = line.Substring(5).Split(',').Select(t => t.Trim()).ToList();
                }
            }
        }
        
        return new MemoryEntry
        {
            Id = id,
            Title = title,
            Content = body,
            Tags = tags
        };
    }
    
    private async Task PersistAsync(CancellationToken ct)
    {
        foreach (var entry in _cache)
        {
            var file = Path.Combine(_memoryPath, $"{entry.Id}.json");
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(file, json, ct);
        }
    }
}

/// <summary>
/// Relevance scorer interface.
/// </summary>
public interface IRelevanceScorer
{
    double Score(MemoryEntry entry, string query);
}

/// <summary>
/// Simple relevance scorer using keyword matching.
/// </summary>
public sealed class SimpleRelevanceScorer : IRelevanceScorer
{
    public double Score(MemoryEntry entry, string query)
    {
        var score = 0.0;
        var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        var titleLower = entry.Title.ToLowerInvariant();
        var contentLower = entry.Content.ToLowerInvariant();
        
        foreach (var term in queryTerms)
        {
            if (titleLower.Contains(term))
                score += 3.0;
            
            if (contentLower.Contains(term))
                score += 1.0;
            
            if (entry.Tags.Any(t => t.ToLowerInvariant().Contains(term)))
                score += 2.0;
        }
        
        // Freshness bonus
        var age = DateTimeOffset.UtcNow - entry.UpdatedAt;
        if (age.TotalDays < 1)
            score += 0.5;
        else if (age.TotalDays < 7)
            score += 0.2;
        
        return score;
    }
}

/// <summary>
/// Memory context for agent sessions.
/// </summary>
public sealed class MemoryContext
{
    private readonly IMemoryManager _manager;
    private readonly List<MemoryEntry> _active = new();
    
    public MemoryContext(IMemoryManager manager) => _manager = manager;
    
    public IReadOnlyList<MemoryEntry> ActiveMemories => _active;
    
    public async Task LoadForQueryAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        var relevant = await _manager.LoadRelevantAsync(query, maxResults, ct);
        _active.Clear();
        _active.AddRange(relevant);
    }
    
    public async Task AddMemoryAsync(string title, string content, string[]? tags = null, CancellationToken ct = default)
    {
        var entry = new MemoryEntry
        {
            Title = title,
            Content = content,
            Tags = tags ?? Array.Empty<string>()
        };
        
        await _manager.SaveAsync(entry, ct);
        _active.Add(entry);
    }
    
    public string FormatForPrompt()
    {
        if (_active.Count == 0) return "";
        
        var sb = new StringBuilder();
        sb.AppendLine("## Relevant Context from Memory");
        sb.AppendLine();
        
        foreach (var m in _active)
        {
            sb.AppendLine($"### {m.Title}");
            sb.AppendLine(m.Content);
            if (m.Tags.Count > 0)
                sb.AppendLine($"Tags: {string.Join(", ", m.Tags)}");
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
}
