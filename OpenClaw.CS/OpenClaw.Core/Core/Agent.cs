namespace OpenClaw.Core;

using Microsoft.Extensions.Logging;
using OpenClaw.LLM;
using OpenClaw.Permissions;
using OpenClaw.Tools;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Main agent implementation.
/// </summary>
public sealed class OpenClawAgent : IAgent, IAsyncDisposable
{
    private readonly IModelClient _modelClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly IPermissionManager _permissionManager;
    private readonly ILogger<OpenClawAgent> _logger;
    private readonly AgentConfig _config;
    private readonly CancellationTokenSource _stopCts = new();
    
    public string Id { get; } = Guid.NewGuid().ToString();
    public string Name => "OpenClaw";
    
    public OpenClawAgent(
        IModelClient modelClient,
        IToolRegistry toolRegistry,
        IPermissionManager permissionManager,
        AgentConfig? config = null,
        ILogger<OpenClawAgent>? logger = null)
    {
        _modelClient = modelClient;
        _toolRegistry = toolRegistry;
        _permissionManager = permissionManager;
        _config = config ?? new AgentConfig();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenClawAgent>.Instance;
    }
    
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        Session session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
        var linkedCt = linkedCts.Token;
        
        var systemPrompt = BuildSystemPrompt(session);
        var tools = _toolRegistry.GetToolDefinitions();
        
        var pendingToolCalls = new List<PendingToolCall>();
        
        try
        {
            while (!linkedCt.IsCancellationRequested)
            {
                // Build messages for API
                var messages = BuildApiMessages(session);
                
                // Stream from model
                await foreach (var evt in _modelClient.StreamAsync(systemPrompt, messages, tools, linkedCt))
                {
                    switch (evt)
                    {
                        case StreamEvent.TokenDelta delta:
                            yield return new AgentEvent.MessageDelta(new Message
                            {
                                Role = "assistant",
                                Content = delta.Text
                            });
                            break;
                            
                        case StreamEvent.ThinkingDelta thinking:
                            yield return new AgentEvent.Thinking(thinking.Text);
                            break;
                            
                        case StreamEvent.ToolUseStart toolStart:
                            pendingToolCalls.Add(new PendingToolCall(toolStart.Id, toolStart.Name));
                            break;
                            
                        case StreamEvent.ToolUseDelta toolDelta:
                            var pending = pendingToolCalls.FirstOrDefault(p => p.Id == toolDelta.Id);
                            pending?.AppendJson(toolDelta.PartialJson);
                            break;
                            
                        case StreamEvent.ToolUseComplete toolComplete:
                            yield return new AgentEvent.ToolCall(
                                toolComplete.Id,
                                toolComplete.Name,
                                toolComplete.Arguments);
                            break;
                            
                        case StreamEvent.MessageComplete complete:
                            // Execute pending tool calls
                            foreach (var toolCall in pendingToolCalls)
                            {
                                if (toolCall.IsComplete)
                                {
                                    var result = await ExecuteToolCallAsync(toolCall, session, linkedCt);
                                    yield return new AgentEvent.ToolResultEvent(toolCall.Id, result);
                                }
                            }
                            
                            // Check if we need to continue
                            if (complete.StopReason == "tool_use" && pendingToolCalls.Count > 0)
                            {
                                // Add tool results to session and continue
                                foreach (var toolCall in pendingToolCalls)
                                {
                                    if (toolCall.IsComplete)
                                    {
                                        var result = await ExecuteToolCallAsync(toolCall, session, linkedCt);
                                        session.AddMessage(new Message
                                        {
                                            Role = "tool",
                                            Content = result.Output,
                                            ContentBlocks = new[]
                                            {
                                                new ContentBlock.ToolResult(toolCall.Id, result.Output, !result.Success)
                                            }
                                        });
                                    }
                                }
                                
                                pendingToolCalls.Clear();
                                continue; // Next iteration
                            }
                            
                            yield return new AgentEvent.Complete(complete.StopReason);
                            yield break;
                            
                        case StreamEvent.StreamError error:
                            yield return new AgentEvent.Error(error.Error);
                            yield break;
                    }
                }
            }
        }
        finally
        {
            linkedCts.Dispose();
        }
    }
    
    public Task StopAsync()
    {
        _stopCts.Cancel();
        return Task.CompletedTask;
    }
    
    private async Task<ToolResult> ExecuteToolCallAsync(PendingToolCall toolCall, Session session, CancellationToken ct)
    {
        var tool = _toolRegistry.GetTool(toolCall.Name);
        if (tool is null)
        {
            return ToolResult.Fail($"Unknown tool: {toolCall.Name}");
        }
        
        // Check permission
        var permission = await _permissionManager.CheckPermissionAsync(
            toolCall.Name,
            toolCall.GetInput(),
            session.Metadata.PermissionMode,
            ct);
        
        if (!permission.Allowed)
        {
            return ToolResult.Fail($"Permission denied: {permission.Reason}");
        }
        
        // Execute tool
        try
        {
            var input = toolCall.GetInput();
            var result = await tool.ExecuteAsync(input, ct);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool execution failed: {ToolName}", toolCall.Name);
            return ToolResult.Fail($"Tool execution failed: {ex.Message}", ex);
        }
    }
    
    private string BuildSystemPrompt(Session session)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("You are OpenClaw, an AI coding assistant.");
        sb.AppendLine();
        sb.AppendLine("You help users with:");
        sb.AppendLine("- Reading, writing, and editing code");
        sb.AppendLine("- Executing shell commands");
        sb.AppendLine("- Searching and analyzing files");
        sb.AppendLine("- Web searches and fetching");
        sb.AppendLine("- Managing tasks and schedules");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(session.Metadata.WorkingDirectory))
        {
            sb.AppendLine($"Working directory: {session.Metadata.WorkingDirectory}");
        }
        
        if (!string.IsNullOrEmpty(session.Metadata.Model))
        {
            sb.AppendLine($"Model: {session.Metadata.Model}");
        }
        
        sb.AppendLine();
        sb.AppendLine("Use the available tools to complete tasks. Ask for clarification when needed.");
        
        return sb.ToString();
    }
    
    private List<ApiMessage> BuildApiMessages(Session session)
    {
        return session.Messages.Select(m => new ApiMessage
        {
            Role = m.Role,
            Content = m.Content,
            ContentBlocks = m.ContentBlocks?.Select<ContentBlock, ApiContentBlock>(b => b switch
            {
                ContentBlock.Text t => new ApiContentBlock.Text(t.Value),
                ContentBlock.ToolUse tu => new ApiContentBlock.ToolUse(tu.Id, tu.Name, tu.Input),
                ContentBlock.ToolResult tr => new ApiContentBlock.ToolResult(tr.ToolUseId, tr.Content, tr.IsError),
                _ => new ApiContentBlock.Text("")
            }).ToList()
        }).ToList();
    }
    
    public ValueTask DisposeAsync()
    {
        _stopCts.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Agent configuration.
/// </summary>
public sealed class AgentConfig
{
    public int MaxTokens { get; init; } = 4096;
    public double Temperature { get; init; } = 0.7;
    public int MaxToolCalls { get; init; } = 50;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Pending tool call state.
/// </summary>
internal sealed class PendingToolCall
{
    public string Id { get; }
    public string Name { get; }
    public StringBuilder JsonBuilder { get; } = new();
    
    public PendingToolCall(string id, string name)
    {
        Id = id;
        Name = name;
    }
    
    public void AppendJson(string json) => JsonBuilder.Append(json);
    
    public bool IsComplete => JsonBuilder.Length > 0;
    
    public object GetInput()
    {
        var json = JsonBuilder.ToString();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
        }
        catch
        {
            return new { raw = json };
        }
    }
}
