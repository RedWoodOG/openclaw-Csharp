namespace OpenClaw.Tools;

using OpenClaw.Core;
using OpenClaw.LLM;
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
    
    public override string Name => "agent";
    public override string Description => "Spawn a subagent for specialized tasks";
    
    public AgentTool(IModelClient? modelClient = null, IToolRegistry? toolRegistry = null)
    {
        _modelClient = modelClient;
        _toolRegistry = toolRegistry;
    }
    
    protected override async Task<ToolResult> ExecuteCoreAsync(AgentParams p, CancellationToken ct)
    {
        var definition = GetAgentDefinition(p.AgentType);
        
        var output = new StringBuilder();
        output.AppendLine($"[Agent: {definition.Name}] Started");
        output.AppendLine($"Task: {p.Task}");
        output.AppendLine("---");
        
        // In a real implementation, this would spawn a new agent
        // For now, return the task description
        output.AppendLine($"Agent type: {definition.Name}");
        output.AppendLine($"Allowed tools: {string.Join(", ", definition.AllowedTools)}");
        output.AppendLine("---");
        output.AppendLine($"[Agent: {definition.Name}] Completed");
        
        return ToolResult.Ok(output.ToString());
    }
    
    private static AgentDefinition GetAgentDefinition(string agentType) => agentType.ToLowerInvariant() switch
    {
        "researcher" => new AgentDefinition("Researcher",
            "You are a research specialist. Find and synthesize information.",
            new[] { "webfetch", "websearch", "read_file", "glob", "grep" }),
        
        "coder" => new AgentDefinition("Coder",
            "You are a coding specialist. Write clean, efficient code.",
            new[] { "read_file", "write_file", "edit_file", "glob", "grep", "bash" }),
        
        "analyst" => new AgentDefinition("Analyst",
            "You are an analysis specialist. Break down complex problems.",
            new[] { "read_file", "glob", "grep", "bash" }),
        
        _ => new AgentDefinition("General",
            "You are a helpful assistant.",
            new[] { "read_file", "write_file", "edit_file", "glob", "grep", "bash", "webfetch", "websearch" })
    };
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            agent_type = new { type = "string", description = "Type of agent to spawn" },
            task = new { type = "string", description = "Task for the agent" },
            context = new { type = "string", description = "Additional context" }
        },
        required = new[] { "agent_type", "task" }
    });
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
