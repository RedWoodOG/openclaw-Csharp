namespace OpenClaw.Tools;

using OpenClaw.Core;
using OpenClaw.Security;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>
/// Bash/PowerShell shell command execution tool with security validation.
/// </summary>
public sealed class BashTool : ToolBase<BashParams>
{
    private readonly ShellSecurityAnalyzer? _securityAnalyzer;
    private readonly BashSecurityPolicy? _policy;
    
    public override string Name => "bash";
    public override string Description => "Execute shell commands with security validation and timeout support";
    
    public BashTool(ShellSecurityAnalyzer? securityAnalyzer = null, BashSecurityPolicy? policy = null)
    {
        _securityAnalyzer = securityAnalyzer ?? new ShellSecurityAnalyzer();
        _policy = policy;
    }
    
    protected override async Task<ToolResult> ExecuteCoreAsync(BashParams p, CancellationToken ct)
    {
        // Security analysis
        if (!p.SkipSecurityCheck && _securityAnalyzer is not null)
        {
            var context = new ShellContext
            {
                WorkingDirectory = p.WorkingDirectory,
                IsSandboxed = _policy?.IsSandboxed ?? false,
                AllowNetwork = _policy?.AllowNetwork ?? true,
                AllowFileSystemWrite = _policy?.AllowFileSystemWrite ?? true,
                AllowSubprocess = _policy?.AllowSubprocess ?? true
            };
            
            var securityResult = _securityAnalyzer.Analyze(p.Command, context);
            
            switch (securityResult.Classification)
            {
                case SecurityClassification.Dangerous:
                    return ToolResult.Fail($"Security: {securityResult.Reason}");
                    
                case SecurityClassification.TooComplex:
                    return ToolResult.Fail($"Security: Command too complex - {securityResult.Reason}");
                    
                case SecurityClassification.NeedsReview when _policy?.AutoApproveWarnings != true:
                    var warnings = string.Join("\n", securityResult.Warnings ?? new[] { securityResult.Reason ?? "Unknown" });
                    return ToolResult.Fail($"Security review required:\n{warnings}");
            }
        }
        
        try
        {
            var isPowerShell = IsPowerShellCommand(p.Command);
            var psi = new ProcessStartInfo
            {
                FileName = isPowerShell ? "powershell.exe" : "cmd.exe",
                Arguments = isPowerShell
                    ? $"-NoProfile -ExecutionPolicy Bypass -Command \"{p.Command}\""
                    : $"/c {p.Command}",
                WorkingDirectory = p.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            
            if (!string.IsNullOrEmpty(p.Description))
                Console.WriteLine($"[BASH] {p.Description}");
            
            using var process = new Process { StartInfo = psi };
            process.Start();
            
            if (p.RunInBackground)
                return ToolResult.Ok($"Command started in background (PID: {process.Id})");
            
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(p.TimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            
            try
            {
                await Task.Run(() => process.WaitForExit(), linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return ToolResult.Fail($"Command timed out after {p.TimeoutMs}ms");
            }
            
            var output = await stdoutTask;
            var error = await stderrTask;
            
            return process.ExitCode == 0
                ? ToolResult.Ok(string.IsNullOrEmpty(output) ? "Command completed successfully." : output)
                : ToolResult.Fail($"Exit code {process.ExitCode}: {error}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Execution failed: {ex.Message}", ex);
        }
    }
    
    private static bool IsPowerShellCommand(string command) =>
        command.StartsWith("pwsh") || command.StartsWith("powershell") ||
        command.Contains(" -Command ") || command.Contains(" -ScriptBlock ");
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "Shell command to execute" },
            working_directory = new { type = "string", description = "Working directory for command" },
            timeout_ms = new { type = "integer", description = "Timeout in milliseconds" },
            run_in_background = new { type = "boolean", description = "Run command in background" },
            description = new { type = "string", description = "Description of what the command does" },
            skip_security_check = new { type = "boolean", description = "Skip security validation" }
        },
        required = new[] { "command" }
    });
}

public sealed class BashParams
{
    public required string Command { get; init; }
    public string? WorkingDirectory { get; init; }
    public int TimeoutMs { get; init; } = 120000;
    public bool RunInBackground { get; init; }
    public string? Description { get; init; }
    public bool SkipSecurityCheck { get; init; }
}

public sealed class BashSecurityPolicy
{
    public bool IsSandboxed { get; init; }
    public bool AllowNetwork { get; init; } = true;
    public bool AllowFileSystemWrite { get; init; } = true;
    public bool AllowSubprocess { get; init; } = true;
    public bool AutoApproveWarnings { get; init; }
}
