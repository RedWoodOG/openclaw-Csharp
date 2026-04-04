namespace OpenClaw.LLM;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Model client interface for LLM API calls.
/// </summary>
public interface IModelClient
{
    IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<ApiMessage> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default);
    
    Task<CompletionResponse> CompleteAsync(
        string? systemPrompt,
        IEnumerable<ApiMessage> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default);
}

/// <summary>
/// API message format.
/// </summary>
public sealed class ApiMessage
{
    public string Role { get; init; } = "user";
    public string? Content { get; init; }
    public IReadOnlyList<ApiContentBlock>? ContentBlocks { get; init; }
}

/// <summary>
/// API content block.
/// </summary>
public abstract record ApiContentBlock
{
    public sealed record Text(string Value) : ApiContentBlock;
    public sealed record Image(string Source, string? MediaType = null) : ApiContentBlock;
    public sealed record ToolUse(string Id, string Name, JsonElement Input) : ApiContentBlock;
    public sealed record ToolResult(string ToolUseId, string Content, bool IsError = false) : ApiContentBlock;
}

/// <summary>
/// Streaming events from model.
/// </summary>
public abstract record StreamEvent
{
    public sealed record TokenDelta(string Text) : StreamEvent;
    public sealed record ThinkingDelta(string Text) : StreamEvent;
    public sealed record ToolUseStart(string Id, string Name) : StreamEvent;
    public sealed record ToolUseDelta(string Id, string PartialJson) : StreamEvent;
    public sealed record ToolUseComplete(string Id, string Name, JsonElement Arguments) : StreamEvent;
    public sealed record MessageComplete(string StopReason, UsageStats? Usage = null) : StreamEvent;
    public sealed record StreamError(Exception Error) : StreamEvent;
}

/// <summary>
/// Token usage statistics.
/// </summary>
public sealed record UsageStats(
    int InputTokens,
    int OutputTokens,
    int? CacheCreationTokens = null,
    int? CacheReadTokens = null);

/// <summary>
/// Non-streaming completion response.
/// </summary>
public sealed class CompletionResponse
{
    public string Content { get; init; } = "";
    public string StopReason { get; init; } = "end_turn";
    public IReadOnlyList<ToolUseBlock>? ToolUses { get; init; }
    public UsageStats? Usage { get; init; }
}

/// <summary>
/// Tool use block in response.
/// </summary>
public sealed record ToolUseBlock(string Id, string Name, JsonElement Arguments);

/// <summary>
/// Tool definition for function calling.
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// Model configuration.
/// </summary>
public sealed class ModelConfig
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public double Temperature { get; init; } = 0.7;
    public int MaxTokens { get; init; } = 4096;
}
