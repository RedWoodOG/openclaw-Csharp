namespace OpenClaw.Permissions;

using System.Text.Json;
using OpenClaw.Core;

/// <summary>
/// Permission manager interface.
/// </summary>
public interface IPermissionManager
{
    Task<PermissionDecision> CheckPermissionAsync(
        string toolName,
        object? input,
        PermissionMode mode,
        CancellationToken ct = default);
    
    void AddRule(PermissionRule rule);
    void RemoveRule(string ruleId);
    IReadOnlyList<PermissionRule> GetRules();
}

/// <summary>
/// Permission decision result.
/// </summary>
public sealed record PermissionDecision(
    bool Allowed,
    string? Reason = null,
    PermissionAction Action = PermissionAction.None);

/// <summary>
/// Permission actions.
/// </summary>
public enum PermissionAction
{
    None,
    AllowOnce,
    AllowAlways,
    DenyOnce,
    DenyAlways
}

/// <summary>
/// Permission rule.
/// </summary>
public sealed record PermissionRule(
    string Id,
    string? ToolName = null,
    string? InputPattern = null,
    PermissionRuleEffect Effect = PermissionRuleEffect.Allow,
    int Priority = 0);

public enum PermissionRuleEffect { Allow, Deny, Ask }

/// <summary>
/// Default permission manager implementation.
/// </summary>
public sealed class PermissionManager : IPermissionManager
{
    private readonly List<PermissionRule> _rules = new();
    private readonly HashSet<string> _alwaysAllowed = new();
    private readonly HashSet<string> _alwaysDenied = new();
    
    public PermissionManager()
    {
        // Default rules for safe tools
        AddRule(new PermissionRule("default_read_allow", "read_file", null, PermissionRuleEffect.Allow, 100));
        AddRule(new PermissionRule("default_glob_allow", "glob", null, PermissionRuleEffect.Allow, 100));
        AddRule(new PermissionRule("default_grep_allow", "grep", null, PermissionRuleEffect.Allow, 100));
        
        // Default rules for dangerous tools
        AddRule(new PermissionRule("default_bash_ask", "bash", null, PermissionRuleEffect.Ask, 100));
        AddRule(new PermissionRule("default_write_ask", "write_file", null, PermissionRuleEffect.Ask, 100));
        AddRule(new PermissionRule("default_edit_ask", "edit_file", null, PermissionRuleEffect.Ask, 100));
    }
    
    public Task<PermissionDecision> CheckPermissionAsync(
        string toolName,
        object? input,
        PermissionMode mode,
        CancellationToken ct = default)
    {
        // Bypass mode - allow everything
        if (mode == PermissionMode.BypassPermissions)
        {
            return Task.FromResult(new PermissionDecision(true, "Bypass mode"));
        }
        
        // Check always-allowed cache
        var cacheKey = GetCacheKey(toolName, input);
        if (_alwaysAllowed.Contains(cacheKey))
        {
            return Task.FromResult(new PermissionDecision(true, "Previously allowed", PermissionAction.AllowAlways));
        }
        
        // Check always-denied cache
        if (_alwaysDenied.Contains(cacheKey))
        {
            return Task.FromResult(new PermissionDecision(false, "Previously denied", PermissionAction.DenyAlways));
        }
        
        // Plan mode - deny write operations
        if (mode == PermissionMode.Plan)
        {
            var isWriteOperation = IsWriteTool(toolName);
            if (isWriteOperation)
            {
                return Task.FromResult(new PermissionDecision(false, "Plan mode - write operations disabled"));
            }
        }
        
        // Auto mode - allow safe operations
        if (mode == PermissionMode.Auto)
        {
            var isSafe = IsSafeTool(toolName);
            if (isSafe)
            {
                return Task.FromResult(new PermissionDecision(true, "Auto mode - safe tool"));
            }
        }
        
        // Check rules
        var matchingRules = _rules
            .Where(r => r.ToolName == null || r.ToolName == toolName)
            .OrderByDescending(r => r.Priority)
            .ToList();
        
        foreach (var rule in matchingRules)
        {
            if (rule.InputPattern is not null && input is not null)
            {
                var inputJson = JsonSerializer.Serialize(input);
                if (!System.Text.RegularExpressions.Regex.IsMatch(inputJson, rule.InputPattern))
                    continue;
            }
            
            var decision = rule.Effect switch
            {
                PermissionRuleEffect.Allow => new PermissionDecision(true, $"Rule: {rule.Id}"),
                PermissionRuleEffect.Deny => new PermissionDecision(false, $"Rule: {rule.Id}"),
                PermissionRuleEffect.Ask => new PermissionDecision(false, "User confirmation required"),
                _ => new PermissionDecision(false, "Unknown rule effect")
            };
            
            return Task.FromResult(decision);
        }
        
        // Default: ask for permission
        return Task.FromResult(new PermissionDecision(false, "No matching rule - user confirmation required"));
    }
    
    public void AddRule(PermissionRule rule)
    {
        _rules.Add(rule);
        _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }
    
    public void RemoveRule(string ruleId)
    {
        _rules.RemoveAll(r => r.Id == ruleId);
    }
    
    public IReadOnlyList<PermissionRule> GetRules() => _rules;
    
    public void SetAlwaysAllowed(string toolName, object? input)
    {
        var key = GetCacheKey(toolName, input);
        _alwaysAllowed.Add(key);
        _alwaysDenied.Remove(key);
    }
    
    public void SetAlwaysDenied(string toolName, object? input)
    {
        var key = GetCacheKey(toolName, input);
        _alwaysDenied.Add(key);
        _alwaysAllowed.Remove(key);
    }
    
    private static string GetCacheKey(string toolName, object? input)
    {
        if (input is null) return toolName;
        return $"{toolName}:{JsonSerializer.Serialize(input)}";
    }
    
    private static bool IsWriteTool(string toolName) => toolName switch
    {
        "write_file" or "edit_file" or "bash" or "delete_file" => true,
        _ => false
    };
    
    private static bool IsSafeTool(string toolName) => toolName switch
    {
        "read_file" or "glob" or "grep" or "webfetch" or "websearch" => true,
        _ => false
    };
}
