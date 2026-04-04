namespace OpenClaw.Hooks;

using System.Text.Json;

/// <summary>
/// Hook event types.
/// </summary>
public enum HookEventType
{
    PreToolUse,
    PostToolUse,
    Stop,
    UserPromptSubmit,
    PermissionRequest,
    SessionStart,
    SessionEnd
}

/// <summary>
/// Base hook event.
/// </summary>
public abstract record HookEvent(HookEventType Type, DateTimeOffset Timestamp = default)
{
    protected HookEvent(HookEventType type) : this(type, DateTimeOffset.UtcNow) { }
}

/// <summary>
/// Tool use hook event.
/// </summary>
public sealed record ToolUseHookEvent(
    string ToolName,
    object? Input,
    string? ToolCallId,
    HookEventType Type) : HookEvent(Type);

/// <summary>
/// Post tool use event.
/// </summary>
public sealed record PostToolUseHookEvent(
    string ToolName,
    object? Input,
    string? ToolCallId,
    bool Success,
    string? Output,
    string? Error) : HookEvent(HookEventType.PostToolUse);

/// <summary>
/// Stop hook event.
/// </summary>
public sealed record StopHookEvent(string? Reason, IReadOnlyList<string>? ToolResults) : HookEvent(HookEventType.Stop);

/// <summary>
/// User prompt hook event.
/// </summary>
public sealed record UserPromptHookEvent(string Prompt, IReadOnlyDictionary<string, string>? Metadata) : HookEvent(HookEventType.UserPromptSubmit);

/// <summary>
/// Hook definition.
/// </summary>
public abstract record HookDefinition(
    string Name,
    IReadOnlySet<HookEventType> Events,
    bool Enabled = true)
{
    public abstract Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct);
}

/// <summary>
/// Hook result.
/// </summary>
public sealed record HookResult(bool Success, string? Output = null, string? Error = null)
{
    public static HookResult Ok(string? output = null) => new(true, output);
    public static HookResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Command hook - executes shell command.
/// </summary>
public sealed record CommandHook(
    string Name,
    string Command,
    IReadOnlySet<HookEventType> Events,
    bool Enabled = true) : HookDefinition(Name, Events, Enabled)
{
    public override async Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {Command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            psi.Environment["HOOK_EVENT_TYPE"] = evt.Type.ToString();
            psi.Environment["HOOK_TIMESTAMP"] = evt.Timestamp.ToString("O");
            
            if (evt is ToolUseHookEvent toolEvt)
            {
                psi.Environment["HOOK_TOOL_NAME"] = toolEvt.ToolName;
                if (toolEvt.Input is not null)
                    psi.Environment["HOOK_TOOL_INPUT"] = JsonSerializer.Serialize(toolEvt.Input);
            }
            
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return HookResult.Fail("Failed to start process");
            
            await process.WaitForExitAsync(ct);
            var output = await process.StandardOutput.ReadToEndAsync();
            
            return process.ExitCode == 0 ? HookResult.Ok(output) : HookResult.Fail($"Exit code {process.ExitCode}");
        }
        catch (Exception ex) { return HookResult.Fail(ex.Message); }
    }
}

/// <summary>
/// HTTP hook - sends POST request.
/// </summary>
public sealed record HttpHook(
    string Name,
    Uri Url,
    IReadOnlySet<HookEventType> Events,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool Enabled = true) : HookDefinition(Name, Events, Enabled)
{
    private static readonly HttpClient Client = new();
    
    public override async Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct)
    {
        try
        {
            var payload = new { eventType = evt.Type.ToString(), timestamp = evt.Timestamp, data = evt };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
            if (Headers is not null)
                foreach (var (k, v) in Headers)
                    request.Headers.Add(k, v);
            
            var response = await Client.SendAsync(request, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);
            
            return response.IsSuccessStatusCode ? HookResult.Ok(responseContent) : HookResult.Fail($"HTTP {response.StatusCode}");
        }
        catch (Exception ex) { return HookResult.Fail(ex.Message); }
    }
}

/// <summary>
/// In-process hook - executes delegate.
/// </summary>
public sealed record InProcessHook(
    string Name,
    Func<HookEvent, CancellationToken, Task<HookResult>> Handler,
    IReadOnlySet<HookEventType> Events,
    bool Enabled = true) : HookDefinition(Name, Events, Enabled)
{
    public override Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct) => Handler(evt, ct);
}

/// <summary>
/// Hook executor.
/// </summary>
public sealed class HookExecutor
{
    private readonly List<HookDefinition> _hooks = new();
    
    public void RegisterHook(HookDefinition hook) => _hooks.Add(hook);
    public void RemoveHook(string name) => _hooks.RemoveAll(h => h.Name == name);
    
    public async Task<IReadOnlyList<HookExecutionResult>> ExecuteHooksAsync(
        HookEventType eventType,
        HookEvent evt,
        CancellationToken ct = default)
    {
        var results = new List<HookExecutionResult>();
        
        var matching = _hooks.Where(h => h.Enabled && h.Events.Contains(eventType)).ToList();
        
        foreach (var hook in matching)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await hook.ExecuteAsync(evt, ct);
                sw.Stop();
                
                results.Add(new HookExecutionResult(hook.Name, result.Success, result.Output, result.Error, sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                results.Add(new HookExecutionResult(hook.Name, false, null, ex.Message, 0));
            }
        }
        
        return results;
    }
    
    public async Task<bool> ShouldBlockAsync(HookEventType eventType, HookEvent evt, CancellationToken ct = default)
    {
        var results = await ExecuteHooksAsync(eventType, evt, ct);
        return results.Any(r => !r.Success);
    }
}

/// <summary>
/// Hook execution result.
/// </summary>
public sealed record HookExecutionResult(
    string HookName,
    bool Success,
    string? Output,
    string? Error,
    long DurationMs);
