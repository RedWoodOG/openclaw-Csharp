namespace OpenClaw.Tools;

using OpenClaw.Core;
using OpenClaw.LLM;
using System.Text.Json;

/// <summary>
/// Tool registry interface.
/// </summary>
public interface IToolRegistry
{
    ITool? GetTool(string name);
    IReadOnlyList<ITool> GetAllTools();
    void RegisterTool(ITool tool);
    IReadOnlyList<ToolDefinition> GetToolDefinitions();
}

/// <summary>
/// Default tool registry implementation.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    
    public ITool? GetTool(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;
    public IReadOnlyList<ITool> GetAllTools() => _tools.Values.ToList();
    
    public void RegisterTool(ITool tool)
    {
        _tools[tool.Name] = tool;
    }
    
    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        return _tools.Values.Select(t => new ToolDefinition(
            t.Name,
            t.Description,
            t.GetInputSchema() ?? GetDefaultSchema()
        )).ToList();
    }
    
    private static JsonElement GetDefaultSchema()
    {
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        });
    }
}

/// <summary>
/// Extension methods for tool registration.
/// </summary>
public static class ToolRegistryExtensions
{
    public static void RegisterDefaultTools(this IToolRegistry registry, Security.ShellSecurityAnalyzer? analyzer = null)
    {
        // File tools
        registry.RegisterTool(new ReadFileTool());
        registry.RegisterTool(new WriteFileTool());
        registry.RegisterTool(new EditFileTool());
        registry.RegisterTool(new GlobTool());
        registry.RegisterTool(new GrepTool());
        registry.RegisterTool(new DeleteFileTool());
        
        // Shell tools
        registry.RegisterTool(new BashTool(analyzer));
        
        // Web tools
        registry.RegisterTool(new WebFetchTool());
        registry.RegisterTool(new WebSearchTool());
        
        // Agent tools
        registry.RegisterTool(new AskUserTool());
        registry.RegisterTool(new TodoWriteTool());
        registry.RegisterTool(new AgentTool());
        
        // Schedule tools
        registry.RegisterTool(new ScheduleCronTool());
        
        // LSP tools
        registry.RegisterTool(new LspTool());
    }
}
