namespace OpenClaw.Agents;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenClaw.LLM;
using OpenClaw.Permissions;
using OpenClaw.Tools;

#pragma warning disable CS8981

/// <summary>
/// Default implementation of IAgentRuntime that wires up LLM and tool execution.
/// </summary>
public sealed class DefaultAgentRuntime : IAgentRuntime
{
    private readonly DefaultAgentRuntimeOptions _options;
    private readonly IModelClientFactory _modelClientFactory;
    private readonly IToolRegistry _toolRegistry;
    private readonly IPermissionManager _permissionManager;
    private readonly ModelRouter _modelRouter;
    private readonly ILogger<DefaultAgentRuntime>? _logger;
    
    public DefaultAgentRuntime(
        DefaultAgentRuntimeOptions? options = null,
        IModelClientFactory? modelClientFactory = null,
        IToolRegistry? toolRegistry = null,
        IPermissionManager? permissionManager = null,
        ModelRouter? modelRouter = null,
        ILogger<DefaultAgentRuntime>? logger = null)
    {
        _options = options ?? new DefaultAgentRuntimeOptions();
        _modelClientFactory = modelClientFactory ?? throw new ArgumentNullException(nameof(modelClientFactory));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _permissionManager = permissionManager ?? throw new ArgumentNullException(nameof(permissionManager));
        _modelRouter = modelRouter ?? new ModelRouter();
        _logger = logger;
    }
    
    /// <inheritdoc />
    public async Task<AgentResponse> GenerateAsync(
        IEnumerable<AgentMessage> messages, 
        CancellationToken ct = default)
    {
        // Convert AgentMessage to ApiMessage
        var apiMessages = messages.Select(m => new ApiMessage
        {
            Role = m.Role switch
            {
                AgentMessageRole.System => "system",
                AgentMessageRole.User => "user",
                AgentMessageRole.Assistant => "assistant",
                AgentMessageRole.Tool => "tool",
                _ => "user"
            },
            Content = m.Content
        }).ToList();
        
        // Get tool definitions
        var toolDefinitions = _toolRegistry.GetToolDefinitions();
        
        // Create model client for the session
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ResponseTimeout);
        
        try
        {
            var response = await _modelClientFactory.CreateClient()
                .CompleteAsync(null, apiMessages, toolDefinitions, cts.Token);
            
            // Convert tool uses to ToolCalls
            var toolCalls = response.ToolUses?.Select(tu => new ToolCall
            {
                Id = tu.Id,
                Name = tu.Name,
                Input = tu.Arguments
            }).ToList();
            
            return new AgentResponse
            {
                Content = response.Content,
                ToolCalls = toolCalls,
                Usage = response.Usage != null ? new ModelUsage
                {
                    TotalTokens = response.Usage.InputTokens + response.Usage.OutputTokens,
                    PromptTokens = response.Usage.InputTokens,
                    CompletionTokens = response.Usage.OutputTokens
                } : null,
                StopReason = response.StopReason
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GenerateAsync failed");
            return new AgentResponse
            {
                Content = $"Error: {ex.Message}",
                StopReason = "error"
            };
        }
    }
    
    /// <inheritdoc />
    public async Task<ToolResult> ExecuteToolAsync(ToolCall toolCall, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            // Get tool from registry
            var tool = _toolRegistry.GetTool(toolCall.Name);
            if (tool == null)
            {
                sw.Stop();
                return new ToolResult
                {
                    Id = toolCall.Id,
                    Name = toolCall.Name,
                    Success = false,
                    Error = $"Tool not found: {toolCall.Name}",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            
            // Execute tool
            var result = await tool.ExecuteAsync(toolCall.Input, ct);
            sw.Stop();
            
            return new ToolResult
            {
                Id = toolCall.Id,
                Name = toolCall.Name,
                Success = result.Success,
                Output = result.Output,
                Error = result.Error,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "ExecuteToolAsync failed for {ToolName}", toolCall.Name);
            return new ToolResult
            {
                Id = toolCall.Id,
                Name = toolCall.Name,
                Success = false,
                Error = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
    
    /// <inheritdoc />
    public Task<ToolPolicyResult> CheckToolPolicyAsync(ToolCall toolCall, CancellationToken ct = default)
    {
        // Delegate to permission manager
        var tool = _toolRegistry.GetTool(toolCall.Name);
        if (tool == null)
        {
            return Task.FromResult(new ToolPolicyResult
            {
                Decision = ToolPolicyDecision.Deny,
                Reason = "Tool not found"
            });
        }
        
        // Check dangerous flag
        var metadata = tool.GetMetadata?.Invoke();
        if (metadata?.Dangerous == true)
        {
            return Task.FromResult(new ToolPolicyResult
            {
                Decision = ToolPolicyDecision.RequireApproval,
                Reason = "Dangerous tool"
            });
        }
        
        return Task.FromResult(new ToolPolicyResult
        {
            Decision = ToolPolicyDecision.Allow
        });
    }
    
    /// <inheritdoc />
    public Task<ApprovalResult> RequestApprovalAsync(ToolCall toolCall, CancellationToken ct = default)
    {
        // For now, auto-approve. In a full implementation, this would
        // integrate with a UI or notification system.
        return Task.FromResult(new ApprovalResult
        {
            Status = ApprovalStatus.Approved,
            ApprovedBy = "system"
        });
    }
    
    /// <inheritdoc />
    public async Task<ModelSelection> ResolveModelAsync(string? modelHint = null, CancellationToken ct = default)
    {
        return await _modelRouter.ResolveAsync(modelHint, ct);
    }
}

/// <summary>
/// Options for DefaultAgentRuntime.
/// </summary>
public sealed class DefaultAgentRuntimeOptions
{
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool AllowDangerousTools { get; init; } = false;
}

/// <summary>
/// Interface for creating model clients.
/// </summary>
public interface IModelClientFactory
{
    IModelClient CreateClient();
}

#pragma warning restore CS8981
