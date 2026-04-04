namespace OpenClaw.Compaction;

using OpenClaw.Core;
using OpenClaw.LLM;

/// <summary>
/// Compaction urgency level.
/// </summary>
public enum CompactionUrgency
{
    NotNeeded,
    Warning,
    Needed,
    Critical
}

/// <summary>
/// Compaction type.
/// </summary>
public enum CompactionType
{
    Full,
    Partial,
    Micro
}

/// <summary>
/// Compaction check result.
/// </summary>
public sealed record CompactionCheckResult(
    CompactionUrgency Urgency,
    int CurrentTokens,
    int MaxTokens,
    string? Message = null)
{
    public double UsagePercent => (double)CurrentTokens / MaxTokens;
    public bool NeedsCompaction => Urgency >= CompactionUrgency.Needed;
}

/// <summary>
/// Compaction result.
/// </summary>
public sealed record CompactionResult(
    IReadOnlyList<Message> CompactedMessages,
    int OriginalTokens,
    int NewTokens,
    CompactionType Type)
{
    public double ReductionPercent => OriginalTokens > 0 ? 1 - (double)NewTokens / OriginalTokens : 0;
    public int TokensSaved => OriginalTokens - NewTokens;
}

/// <summary>
/// Compaction configuration.
/// </summary>
public sealed class CompactionConfig
{
    public int ContextWindowSize { get; init; } = 200000;
    public double CompactionThreshold { get; init; } = 0.80;
    public double CriticalThreshold { get; init; } = 0.90;
    public double WarningThreshold { get; init; } = 0.70;
    public TimeSpan MicroCompactMaxAge { get; init; } = TimeSpan.FromMinutes(30);
    public int DefaultKeepRecentCount { get; init; } = 10;
}

/// <summary>
/// Token counter interface.
/// </summary>
public interface ITokenCounter
{
    int CountTokens(string text);
    int CountMessages(IReadOnlyList<Message> messages);
}

/// <summary>
/// Simple token counter (approximation).
/// </summary>
public sealed class SimpleTokenCounter : ITokenCounter
{
    public int CountTokens(string text) => (int)Math.Ceiling(text.Length / 4.0);
    public int CountMessages(IReadOnlyList<Message> messages) => messages.Sum(m => CountTokens(m.Content) + 4);
}

/// <summary>
/// Compaction manager.
/// </summary>
public sealed class CompactionManager
{
    private readonly IModelClient _modelClient;
    private readonly ITokenCounter _tokenCounter;
    private readonly CompactionConfig _config;
    
    public CompactionManager(IModelClient modelClient, ITokenCounter? tokenCounter = null, CompactionConfig? config = null)
    {
        _modelClient = modelClient;
        _tokenCounter = tokenCounter ?? new SimpleTokenCounter();
        _config = config ?? new CompactionConfig();
    }
    
    public CompactionCheckResult CheckNeedsCompaction(IReadOnlyList<Message> messages, int? systemPromptTokens = null)
    {
        var totalTokens = (systemPromptTokens ?? 0) + _tokenCounter.CountMessages(messages);
        
        var threshold = (int)(_config.ContextWindowSize * _config.CompactionThreshold);
        var criticalThreshold = (int)(_config.ContextWindowSize * _config.CriticalThreshold);
        var warningThreshold = (int)(_config.ContextWindowSize * _config.WarningThreshold);
        
        if (totalTokens >= criticalThreshold)
            return new CompactionCheckResult(CompactionUrgency.Critical, totalTokens, _config.ContextWindowSize, "Context critically full");
        
        if (totalTokens >= threshold)
            return new CompactionCheckResult(CompactionUrgency.Needed, totalTokens, _config.ContextWindowSize, "Compaction recommended");
        
        if (totalTokens >= warningThreshold)
            return new CompactionCheckResult(CompactionUrgency.Warning, totalTokens, _config.ContextWindowSize, "Context usage high");
        
        return new CompactionCheckResult(CompactionUrgency.NotNeeded, totalTokens, _config.ContextWindowSize);
    }
    
    public async Task<CompactionResult> CompactFullAsync(IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        var prompt = BuildCompactionPrompt(messages, "Summarize the entire conversation:");
        var response = await _modelClient.CompleteAsync(null, new[] { new ApiMessage { Role = "user", Content = prompt } }, null, ct);
        
        var summaryMessage = new Message { Role = "assistant", Content = $"[Conversation Summary]\n{response.Content}" };
        
        var originalTokens = _tokenCounter.CountMessages(messages);
        var newTokens = _tokenCounter.CountTokens(summaryMessage.Content);
        
        return new CompactionResult(new[] { summaryMessage }, originalTokens, newTokens, CompactionType.Full);
    }
    
    public async Task<CompactionResult> CompactPartialAsync(IReadOnlyList<Message> messages, int keepRecentCount, CancellationToken ct = default)
    {
        if (messages.Count <= keepRecentCount)
            return new CompactionResult(messages, 0, 0, CompactionType.Partial);
        
        var toCompact = messages.Take(messages.Count - keepRecentCount).ToList();
        var toKeep = messages.Skip(messages.Count - keepRecentCount).ToList();
        
        var prompt = BuildCompactionPrompt(toCompact, "Summarize the earlier conversation:");
        var response = await _modelClient.CompleteAsync(null, new[] { new ApiMessage { Role = "user", Content = prompt } }, null, ct);
        
        var summaryMessage = new Message { Role = "assistant", Content = $"[Earlier Summary]\n{response.Content}" };
        
        var compacted = new List<Message> { summaryMessage };
        compacted.AddRange(toKeep);
        
        var originalTokens = _tokenCounter.CountMessages(messages);
        var newTokens = _tokenCounter.CountMessages(compacted);
        
        return new CompactionResult(compacted, originalTokens, newTokens, CompactionType.Partial);
    }
    
    public CompactionResult CompactMicro(IReadOnlyList<Message> messages, TimeSpan? maxAge = null)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge ?? _config.MicroCompactMaxAge);
        var result = messages.Where(m => m.Role is "user" or "assistant" or "system" || m.Timestamp >= cutoff).ToList();
        
        var originalTokens = _tokenCounter.CountMessages(messages);
        var newTokens = _tokenCounter.CountMessages(result);
        
        return new CompactionResult(result, originalTokens, newTokens, CompactionType.Micro);
    }
    
    private static string BuildCompactionPrompt(IReadOnlyList<Message> messages, string header)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine("---");
        
        foreach (var msg in messages)
            sb.AppendLine($"[{msg.Role.ToUpperInvariant()}]: {msg.Content}");
        
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Provide a concise summary capturing:");
        sb.AppendLine("1. Main objectives");
        sb.AppendLine("2. Key decisions");
        sb.AppendLine("3. Important context");
        sb.AppendLine("4. Pending tasks");
        
        return sb.ToString();
    }
}
