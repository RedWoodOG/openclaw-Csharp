namespace OpenClaw.Core;

using System.Text.Json;

/// <summary>
/// Core message types for the agent system.
/// </summary>
public sealed class Message
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public IReadOnlyList<ContentBlock>? ContentBlocks { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? ParentId { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString();
}

/// <summary>
/// Content block for multimodal messages.
/// </summary>
public abstract record ContentBlock
{
    public sealed record Text(string Value) : ContentBlock;
    public sealed record Image(string Source, string? MediaType = null) : ContentBlock;
    public sealed record ToolUse(string Id, string Name, JsonElement Input) : ContentBlock;
    public sealed record ToolResult(string ToolUseId, string Content, bool IsError = false) : ContentBlock;
}

/// <summary>
/// Session representing a conversation.
/// </summary>
public sealed class Session
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Message> Messages { get; init; } = new();
    public SessionMetadata Metadata { get; init; } = new();
    
    public void AddMessage(Message message)
    {
        Messages.Add(message);
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Session metadata.
/// </summary>
public sealed class SessionMetadata
{
    public string? WorkingDirectory { get; set; }
    public string? Model { get; set; }
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Default;
    public int TotalTokens { get; set; }
    public int TotalCostCents { get; set; }
    public IReadOnlyDictionary<string, string>? CustomData { get; set; }
    
    public SessionMetadata() { }
}

/// <summary>
/// Permission modes.
/// </summary>
public enum PermissionMode
{
    Default,
    Plan,
    AcceptEdits,
    BypassPermissions,
    DontAsk,
    Auto
}

/// <summary>
/// Tool execution result.
/// </summary>
public sealed class ToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public Exception? Exception { get; init; }
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
    
    public static ToolResult Ok(string output) => new() { Success = true, Output = output };
    public static ToolResult Fail(string error, Exception? ex = null) => new() { Success = false, Error = error, Exception = ex };
}

/// <summary>
/// Tool interface.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    Type ParametersType { get; }
    Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct);
    JsonElement? GetInputSchema();
}

/// <summary>
/// Base tool implementation.
/// </summary>
public abstract class ToolBase<TParams> : ITool where TParams : class
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public Type ParametersType => typeof(TParams);
    
    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        if (parameters is not TParams p)
            return ToolResult.Fail($"Invalid parameters type: expected {typeof(TParams).Name}");
        
        return await ExecuteCoreAsync(p, ct);
    }
    
    protected abstract Task<ToolResult> ExecuteCoreAsync(TParams parameters, CancellationToken ct);
    
    public virtual JsonElement? GetInputSchema() => null;
}

/// <summary>
/// Agent interface.
/// </summary>
public interface IAgent
{
    string Id { get; }
    string Name { get; }
    IAsyncEnumerable<AgentEvent> RunAsync(Session session, CancellationToken ct);
    Task StopAsync();
}

/// <summary>
/// Events emitted by agent during execution.
/// </summary>
public abstract record AgentEvent
{
    public sealed record MessageDelta(Message Message) : AgentEvent;
    public sealed record ToolCall(string Id, string Name, JsonElement Input) : AgentEvent;
    public sealed record ToolResultEvent(string ToolCallId, ToolResult Result) : AgentEvent;
    public sealed record Thinking(string Content) : AgentEvent;
    public sealed record Complete(string StopReason) : AgentEvent;
    public sealed record Error(Exception Ex) : AgentEvent;
    public sealed record PermissionRequest(string ToolName, object Input, string Reason) : AgentEvent;
}
