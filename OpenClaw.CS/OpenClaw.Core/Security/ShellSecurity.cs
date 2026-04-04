namespace OpenClaw.Security;

using System.Text.RegularExpressions;

/// <summary>
/// Security classification for shell commands.
/// </summary>
public enum SecurityClassification
{
    Safe,
    NeedsReview,
    Dangerous,
    TooComplex
}

/// <summary>
/// Security analysis result.
/// </summary>
public sealed record SecurityResult(
    SecurityClassification Classification,
    string? Reason = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? Suggestions = null)
{
    public static SecurityResult Safe() => new(SecurityClassification.Safe);
    public static SecurityResult NeedsReview(string? reason = null, IReadOnlyList<string>? warnings = null) 
        => new(SecurityClassification.NeedsReview, reason, warnings);
    public static SecurityResult Dangerous(string reason) => new(SecurityClassification.Dangerous, reason);
    public static SecurityResult TooComplex(string reason) => new(SecurityClassification.TooComplex, reason);
}

/// <summary>
/// Shell execution context.
/// </summary>
public sealed class ShellContext
{
    public string? WorkingDirectory { get; init; }
    public bool IsSandboxed { get; init; }
    public bool AllowNetwork { get; init; } = true;
    public bool AllowFileSystemWrite { get; init; } = true;
    public bool AllowSubprocess { get; init; } = true;
}

/// <summary>
/// Shell command security analyzer.
/// </summary>
public sealed class ShellSecurityAnalyzer
{
    private readonly List<IShellValidator> _validators;
    
    public ShellSecurityAnalyzer()
    {
        _validators = new List<IShellValidator>
        {
            new MetacharacterValidator(),
            new InjectionValidator(),
            new PrivilegeValidator(),
            new FileSystemValidator(),
            new NetworkValidator(),
            new RmValidator(),
            new CurlValidator(),
            new EvalValidator(),
            new EncodingValidator()
        };
    }
    
    public SecurityResult Analyze(string command, ShellContext? context = null)
    {
        var warnings = new List<string>();
        var normalized = CommandNormalizer.Normalize(command);
        
        foreach (var validator in _validators)
        {
            var result = validator.Validate(normalized, context);
            
            if (result.Classification == SecurityClassification.Dangerous)
                return SecurityResult.Dangerous($"{validator.Name}: {result.Reason}");
            
            if (result.Classification == SecurityClassification.TooComplex)
                return SecurityResult.TooComplex($"{validator.Name}: {result.Reason}");
            
            if (result.Warnings is not null)
                warnings.AddRange(result.Warnings);
        }
        
        return warnings.Count > 0
            ? new SecurityResult(SecurityClassification.NeedsReview, null, warnings)
            : new SecurityResult(SecurityClassification.Safe);
    }
}

/// <summary>
/// Shell validator interface.
/// </summary>
public interface IShellValidator
{
    string Name { get; }
    SecurityResult Validate(string command, ShellContext? context);
}

/// <summary>
/// Command normalizer.
/// </summary>
public static class CommandNormalizer
{
    private static readonly Regex[] SafeWrappers =
    {
        new(@"^timeout\s+\d+\s+", RegexOptions.IgnoreCase),
        new(@"^nice\s+", RegexOptions.IgnoreCase),
        new(@"^nohup\s+", RegexOptions.IgnoreCase),
        new(@"^time\s+", RegexOptions.IgnoreCase),
        new(@"^env\s+(-[iu]\s+\S+\s+)*", RegexOptions.IgnoreCase),
    };
    
    public static string Normalize(string command)
    {
        var normalized = command.Trim();
        
        bool changed;
        do
        {
            changed = false;
            foreach (var pattern in SafeWrappers)
            {
                var newCmd = pattern.Replace(normalized, "");
                if (newCmd != normalized)
                {
                    normalized = newCmd.Trim();
                    changed = true;
                }
            }
        } while (changed);
        
        return Regex.Replace(normalized, @"\s+", " ");
    }
}

// Validators
public sealed class MetacharacterValidator : IShellValidator
{
    public string Name => "Metacharacter";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (command.Contains("$(") || command.Contains("`"))
            return SecurityResult.NeedsReview("Command substitution detected");
        
        if (command.Contains("||") || command.Contains("&&"))
            return SecurityResult.NeedsReview("Command chaining detected");
        
        return SecurityResult.Safe();
    }
}

public sealed class InjectionValidator : IShellValidator
{
    public string Name => "Injection";
    
    private static readonly Regex[] Patterns =
    {
        new(@";\s*rm\s", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@";\s*curl\s.*\|\s*sh", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\|\s*rm\s+-rf\s+/", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        foreach (var pattern in Patterns)
        {
            if (pattern.IsMatch(command))
                return SecurityResult.Dangerous($"Injection pattern detected");
        }
        return SecurityResult.Safe();
    }
}

public sealed class PrivilegeValidator : IShellValidator
{
    public string Name => "Privilege";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (command.Contains("sudo ") || command.Contains(" doas "))
            return SecurityResult.NeedsReview("Privilege escalation command");
        
        if (command.Contains(" su "))
            return SecurityResult.Dangerous("User switching command");
        
        return SecurityResult.Safe();
    }
}

public sealed class FileSystemValidator : IShellValidator
{
    public string Name => "FileSystem";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (command.Contains("dd if=") || command.Contains("dd of="))
            return SecurityResult.Dangerous("Disk operation with dd");
        
        if (command.Contains("mkfs.") || command.Contains("format "))
            return SecurityResult.Dangerous("Disk formatting command");
        
        return SecurityResult.Safe();
    }
}

public sealed class NetworkValidator : IShellValidator
{
    public string Name => "Network";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (context is { AllowNetwork: false })
        {
            if (command.Contains("curl ") || command.Contains("wget ") || command.Contains("nc "))
                return SecurityResult.Dangerous("Network access not allowed");
        }
        
        return SecurityResult.Safe();
    }
}

public sealed class RmValidator : IShellValidator
{
    public string Name => "Rm";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (command.Contains("rm "))
        {
            if (command.Contains("-rf") && (command.Contains(" /") || command.Contains(" ~")))
                return SecurityResult.Dangerous("Destructive rm command");
            
            if (command.Contains("--no-preserve-root"))
                return SecurityResult.Dangerous("rm with --no-preserve-root");
            
            return SecurityResult.NeedsReview("rm command detected");
        }
        
        return SecurityResult.Safe();
    }
}

public sealed class CurlValidator : IShellValidator
{
    public string Name => "Curl";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (command.Contains("curl ") && command.Contains("|") && (command.Contains("sh") || command.Contains("bash")))
            return SecurityResult.Dangerous("curl piped to shell - remote code execution");
        
        return SecurityResult.Safe();
    }
}

public sealed class EvalValidator : IShellValidator
{
    public string Name => "Eval";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (command.Contains("eval "))
            return SecurityResult.Dangerous("eval command - arbitrary code execution");
        
        return SecurityResult.Safe();
    }
}

public sealed class EncodingValidator : IShellValidator
{
    public string Name => "Encoding";
    
    public SecurityResult Validate(string command, ShellContext? context)
    {
        if (command.Contains("base64") && command.Contains("-d") && command.Contains("|"))
            return SecurityResult.NeedsReview("Base64 decode and pipe - potential obfuscation");
        
        return SecurityResult.Safe();
    }
}
