namespace OpenClaw.Tools;

using OpenClaw.Core;
using OpenClaw.LLM;
using OpenClaw.Permissions;
using System.Text;
using System.Text.Json;

/// <summary>
/// Ask user tool for interactive prompts.
/// </summary>
public sealed class AskUserTool : ToolBase<AskUserParams>
{
    public override string Name => "ask_user";
    public override string Description => "Ask the user a question with predefined options";
    
    private static readonly List<QuestionOption> DefaultOptions = new()
    {
        new("Yes", "Proceed with the action"),
        new("No", "Cancel the action"),
        new("Explain", "Explain what you're doing first")
    };
    
    protected override Task<ToolResult> ExecuteCoreAsync(AskUserParams p, CancellationToken ct)
    {
        // In a real implementation, this would integrate with UI
        // For now, return the question for the caller to handle
        var options = p.Options.Count > 0 ? p.Options : DefaultOptions;
        
        var result = new AskUserResult
        {
            Question = p.Question,
            Options = options,
            AllowMultiple = p.AllowMultiple
        };
        
        return Task.FromResult(ToolResult.Ok(JsonSerializer.Serialize(result)));
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            question = new { type = "string", description = "Question to ask the user" },
            options = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        label = new { type = "string" },
                        description = new { type = "string" }
                    }
                }
            },
            allow_multiple = new { type = "boolean", description = "Allow multiple selections" }
        },
        required = new[] { "question" }
    });
}

public sealed class AskUserParams
{
    public required string Question { get; init; }
    public List<QuestionOption> Options { get; init; } = new();
    public bool AllowMultiple { get; init; }
}

public sealed record QuestionOption(string Label, string? Description = null);

public sealed class AskUserResult
{
    public string Question { get; init; } = "";
    public List<QuestionOption> Options { get; init; } = new();
    public bool AllowMultiple { get; init; }
}

/// <summary>
/// Todo write tool for task management.
/// </summary>
public sealed class TodoWriteTool : ToolBase<TodoWriteParams>
{
    private static readonly List<TodoItem> _todos = new();
    
    public override string Name => "todo_write";
    public override string Description => "Create, update, or manage a todo list";
    
    protected override Task<ToolResult> ExecuteCoreAsync(TodoWriteParams p, CancellationToken ct)
    {
        _todos.Clear();
        _todos.AddRange(p.Todos.Select(t => new TodoItem(
            t.Id ?? Guid.NewGuid().ToString(),
            t.Content,
            Enum.Parse<TodoStatus>(t.Status ?? "pending", true),
            Enum.Parse<TodoPriority>(t.Priority ?? "medium", true)
        )));
        
        var summary = FormatTodoList(_todos);
        return Task.FromResult(ToolResult.Ok(summary));
    }
    
    private static string FormatTodoList(IReadOnlyList<TodoItem> todos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Todo list updated:");
        
        foreach (var todo in todos)
        {
            var status = todo.Status switch
            {
                TodoStatus.Completed => "✓",
                TodoStatus.InProgress => "►",
                _ => "○"
            };
            
            var priority = todo.Priority switch
            {
                TodoPriority.High => " [HIGH]",
                TodoPriority.Low => " [low]",
                _ => ""
            };
            
            sb.AppendLine($"  {status} {todo.Content}{priority}");
        }
        
        var completed = todos.Count(t => t.Status == TodoStatus.Completed);
        sb.AppendLine($"\nProgress: {completed}/{todos.Count} completed");
        
        return sb.ToString();
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            todos = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string" },
                        content = new { type = "string" },
                        status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } },
                        priority = new { type = "string", @enum = new[] { "high", "medium", "low" } }
                    }
                }
            }
        },
        required = new[] { "todos" }
    });
}

public sealed class TodoWriteParams
{
    public List<TodoItemInput> Todos { get; init; } = new();
}

public sealed class TodoItemInput
{
    public string? Id { get; init; }
    public required string Content { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
}

public sealed record TodoItem(string Id, string Content, TodoStatus Status, TodoPriority Priority);

public enum TodoStatus { Pending, InProgress, Completed }
public enum TodoPriority { High, Medium, Low }

/// <summary>
/// Agent tool for spawning subagents.
/// </summary>
public sealed class AgentTool : ToolBase<AgentParams>
{
    private readonly IModelClient? _modelClient;
    private readonly IToolRegistry? _toolRegistry;
    private readonly Func<IModelClient, IToolRegistry, IPermissionManager, OpenClawAgent>? _agentFactory;
    
    public override string Name => "agent";
    public override string Description => "Spawn a subagent for specialized tasks";
    
    public AgentTool(
        IModelClient? modelClient = null, 
        IToolRegistry? toolRegistry = null,
        Func<IModelClient, IToolRegistry, IPermissionManager, OpenClawAgent>? agentFactory = null)
    {
        _modelClient = modelClient;
        _toolRegistry = toolRegistry;
        _agentFactory = agentFactory;
    }
    
    protected override async Task<ToolResult> ExecuteCoreAsync(AgentParams p, CancellationToken ct)
    {
        if (_modelClient == null || _toolRegistry == null)
        {
            return ToolResult.Fail("Agent tool requires IModelClient and IToolRegistry to be configured");
        }
        
        var definition = GetAgentDefinition(p.AgentType);
        
        var output = new StringBuilder();
        output.AppendLine($"[Agent: {definition.Name}] Started");
        output.AppendLine($"Task: {p.Task}");
        output.AppendLine($"Context: {p.Context ?? "None provided"}");
        output.AppendLine("---");
        
        try
        {
            // Create filtered tool registry for this agent type
            var filteredRegistry = new FilteredToolRegistry(_toolRegistry, definition.AllowedTools);
            
            // Create a simple permission manager that allows all tools in the allowed list
            var permissionManager = new SubagentPermissionManager(definition.AllowedTools);
            
            // Create the subagent
            var subagent = _agentFactory != null 
                ? _agentFactory(_modelClient, filteredRegistry, permissionManager)
                : new OpenClawAgent(_modelClient, filteredRegistry, permissionManager);
            
            // Create a session for the subagent
            var session = new Session
            {
                Metadata = new SessionMetadata
                {
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    Model = null // Use default
                }
            };
            
            // Add the task as a user message with system prompt
            var fullTask = BuildSubagentTask(definition, p.Task, p.Context);
            session.AddMessage(new Message { Role = "user", Content = fullTask });
            
            // Run the subagent and collect output
            var responseBuilder = new StringBuilder();
            var toolCalls = new List<(string Name, string Args, string Result)>();
            
            await foreach (var evt in subagent.RunAsync(session, ct))
            {
                switch (evt)
                {
                    case AgentEvent.MessageDelta delta:
                        responseBuilder.Append(delta.Message.Content);
                        break;
                        
                    case AgentEvent.ToolCall toolCall:
                        var argsJson = toolCall.Input.GetRawText();
                        toolCalls.Add((toolCall.Name, argsJson, ""));
                        break;
                        
                    case AgentEvent.ToolResultEvent toolResult:
                        if (toolCalls.Count > 0)
                        {
                            var last = toolCalls[^1];
                            toolCalls[^1] = (last.Name, last.Args, toolResult.Result.Output);
                        }
                        break;
                        
                    case AgentEvent.Complete complete:
                        // Log any tool calls made
                        if (toolCalls.Count > 0)
                        {
                            output.AppendLine($"Tools used ({toolCalls.Count}):");
                            foreach (var (name, args, result) in toolCalls.Take(5))
                            {
                                var resultPreview = string.IsNullOrEmpty(result) 
                                    ? "(no output)" 
                                    : result.Length > 100 ? result[..100] + "..." : result;
                                output.AppendLine($"  - {name}: {resultPreview}");
                            }
                            if (toolCalls.Count > 5)
                            {
                                output.AppendLine($"  ... and {toolCalls.Count - 5} more");
                            }
                            output.AppendLine("---");
                        }
                        break;
                        
                    case AgentEvent.Error error:
                        output.AppendLine($"Error: {error.Ex.Message}");
                        break;
                }
            }
            
            // Add the response
            var response = responseBuilder.ToString();
            if (!string.IsNullOrEmpty(response))
            {
                output.AppendLine("Response:");
                output.AppendLine(response.Length > 2000 ? response[..2000] + "..." : response);
            }
            
            output.AppendLine("---");
            output.AppendLine($"[Agent: {definition.Name}] Completed");
            
            return ToolResult.Ok(output.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Subagent failed: {ex.Message}", ex);
        }
    }
    
    private static string BuildSubagentTask(AgentDefinition definition, string task, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine(definition.SystemPrompt);
        sb.AppendLine();
        if (!string.IsNullOrEmpty(context))
        {
            sb.AppendLine("Additional context:");
            sb.AppendLine(context);
            sb.AppendLine();
        }
        sb.AppendLine("Task:");
        sb.AppendLine(task);
        return sb.ToString();
    }
    
    private static AgentDefinition GetAgentDefinition(string agentType) => agentType.ToLowerInvariant() switch
    {
        "researcher" => new AgentDefinition("Researcher",
            "You are a research specialist. Find and synthesize information accurately.",
            new[] { "webfetch", "websearch", "read_file", "glob", "grep" }),
        
        "coder" => new AgentDefinition("Coder",
            "You are a coding specialist. Write clean, efficient, well-documented code.",
            new[] { "read_file", "write_file", "edit_file", "glob", "grep", "bash" }),
        
        "analyst" => new AgentDefinition("Analyst",
            "You are an analysis specialist. Break down complex problems systematically.",
            new[] { "read_file", "glob", "grep", "bash" }),
        
        "reviewer" => new AgentDefinition("Reviewer",
            "You are a code review specialist. Provide constructive feedback on code quality.",
            new[] { "read_file", "glob", "grep" }),
        
        _ => new AgentDefinition("General",
            "You are a helpful assistant.",
            new[] { "read_file", "write_file", "edit_file", "glob", "grep", "bash", "webfetch", "websearch", "ask_user", "todo_write" })
    };
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            agent_type = new { type = "string", description = "Type of agent to spawn", @enum = new[] { "researcher", "coder", "analyst", "reviewer", "general" } },
            task = new { type = "string", description = "Task for the agent" },
            context = new { type = "string", description = "Additional context to provide" }
        },
        required = new[] { "agent_type", "task" }
    });
}

/// <summary>
/// Filtered tool registry that only exposes allowed tools.
/// </summary>
public sealed class FilteredToolRegistry : IToolRegistry
{
    private readonly IToolRegistry _inner;
    private readonly HashSet<string> _allowedTools;
    
    public FilteredToolRegistry(IToolRegistry inner, string[] allowedTools)
    {
        _inner = inner;
        _allowedTools = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
    }
    
    public ITool? GetTool(string name) => 
        _allowedTools.Contains(name) ? _inner.GetTool(name) : null;
    
    public IReadOnlyList<ITool> GetAllTools() => 
        _inner.GetAllTools().Where(t => _allowedTools.Contains(t.Name)).ToList();
    
    public void RegisterTool(ITool tool)
    {
        if (_allowedTools.Contains(tool.Name))
            _inner.RegisterTool(tool);
    }
    
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
        _inner.GetToolDefinitions().Where(d => _allowedTools.Contains(d.Name)).ToList();
}

/// <summary>
/// Permission manager that allows only specific tools.
/// </summary>
public sealed class SubagentPermissionManager : IPermissionManager
{
    private readonly HashSet<string> _allowedTools;
    
    public SubagentPermissionManager(string[] allowedTools)
    {
        _allowedTools = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
    }
    
    public Task<PermissionDecision> CheckPermissionAsync(string toolName, object? input, PermissionMode mode, CancellationToken ct = default)
    {
        if (_allowedTools.Contains(toolName))
            return Task.FromResult(new PermissionDecision(true));
        return Task.FromResult(new PermissionDecision(false, $"Tool '{toolName}' is not allowed for this subagent"));
    }
    
    public void AddRule(PermissionRule rule) { }
    public void RemoveRule(string ruleId) { }
    public IReadOnlyList<PermissionRule> GetRules() => Array.Empty<PermissionRule>();
}

public sealed class AgentParams
{
    public required string AgentType { get; init; }
    public required string Task { get; init; }
    public string? Context { get; init; }
}

public sealed record AgentDefinition(string Name, string SystemPrompt, string[] AllowedTools);

/// <summary>
/// Schedule cron tool.
/// </summary>
public sealed class ScheduleCronTool : ToolBase<ScheduleCronParams>
{
    public override string Name => "schedule_cron";
    public override string Description => "Schedule a task using cron expressions";
    
    protected override Task<ToolResult> ExecuteCoreAsync(ScheduleCronParams p, CancellationToken ct)
    {
        // Validate cron expression
        try
        {
            var cron = Cronos.CronExpression.Parse(p.CronExpression);
            var nextRun = cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            
            var result = $"Scheduled task '{p.Name ?? "unnamed"}'\n" +
                        $"Cron: {p.CronExpression}\n" +
                        $"Next run: {nextRun?.ToString("o") ?? "unknown"}\n" +
                        $"Prompt: {p.Prompt}";
            
            return Task.FromResult(ToolResult.Ok(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Invalid cron expression: {ex.Message}", ex));
        }
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "Task name" },
            cron_expression = new { type = "string", description = "Cron expression" },
            prompt = new { type = "string", description = "Prompt to execute" },
            recurring = new { type = "boolean", description = "Recurring task" }
        },
        required = new[] { "cron_expression", "prompt" }
    });
}

public sealed class ScheduleCronParams
{
    public string? Name { get; init; }
    public required string CronExpression { get; init; }
    public required string Prompt { get; init; }
    public bool Recurring { get; init; } = true;
}

/// <summary>
/// LSP tool for code intelligence.
/// </summary>
public sealed class LspTool : ToolBase<LspParams>
{
    public override string Name => "lsp";
    public override string Description => "Query Language Server Protocol for code intelligence";
    
    protected override Task<ToolResult> ExecuteCoreAsync(LspParams p, CancellationToken ct)
    {
        // Placeholder - real implementation would connect to LSP server
        var result = $"LSP action: {p.Action}\n" +
                    $"File: {p.FilePath}\n" +
                    $"Line: {p.Line ?? 0}, Column: {p.Column ?? 0}";
        
        return Task.FromResult(ToolResult.Ok(result));
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            action = new { type = "string", description = "LSP action (definition, references, hover, completion)" },
            file_path = new { type = "string", description = "File path" },
            line = new { type = "integer", description = "Line number (0-indexed)" },
            column = new { type = "integer", description = "Column number (0-indexed)" },
            new_name = new { type = "string", description = "New name for rename action" }
        },
        required = new[] { "action", "file_path" }
    });
}

public sealed class LspParams
{
    public required string Action { get; init; }
    public required string FilePath { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? NewName { get; init; }
}
