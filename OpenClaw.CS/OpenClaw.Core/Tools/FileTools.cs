namespace OpenClaw.Tools;

using OpenClaw.Core;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Read file tool.
/// </summary>
public sealed class ReadFileTool : ToolBase<ReadFileParams>
{
    public override string Name => "read_file";
    public override string Description => "Read contents of a file";
    
    protected override async Task<ToolResult> ExecuteCoreAsync(ReadFileParams p, CancellationToken ct)
    {
        if (!File.Exists(p.FilePath))
            return ToolResult.Fail($"File not found: {p.FilePath}");
        
        try
        {
            if (p.Offset.HasValue || p.Limit.HasValue)
            {
                var lines = await File.ReadAllLinesAsync(p.FilePath, ct);
                var start = p.Offset ?? 0;
                var count = p.Limit ?? lines.Length - start;
                
                var result = new StringBuilder();
                for (var i = start; i < Math.Min(start + count, lines.Length); i++)
                {
                    result.AppendLine($"{i + 1,6}\t{lines[i]}");
                }
                return ToolResult.Ok(result.ToString());
            }
            
            var content = await File.ReadAllTextAsync(p.FilePath, ct);
            return ToolResult.Ok(content);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to read file: {ex.Message}", ex);
        }
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            file_path = new { type = "string", description = "Path to the file to read" },
            offset = new { type = "integer", description = "Line number to start reading from (1-indexed)" },
            limit = new { type = "integer", description = "Number of lines to read" }
        },
        required = new[] { "file_path" }
    });
}

public sealed class ReadFileParams
{
    public required string FilePath { get; init; }
    public int? Offset { get; init; }
    public int? Limit { get; init; }
}

/// <summary>
/// Write file tool.
/// </summary>
public sealed class WriteFileTool : ToolBase<WriteFileParams>
{
    public override string Name => "write_file";
    public override string Description => "Write content to a file, creating it if it doesn't exist";
    
    protected override async Task<ToolResult> ExecuteCoreAsync(WriteFileParams p, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(p.FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            await File.WriteAllTextAsync(p.FilePath, p.Content, ct);
            return ToolResult.Ok($"Successfully wrote to {p.FilePath}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to write file: {ex.Message}", ex);
        }
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            file_path = new { type = "string", description = "Path to the file to write" },
            content = new { type = "string", description = "Content to write to the file" }
        },
        required = new[] { "file_path", "content" }
    });
}

public sealed class WriteFileParams
{
    public required string FilePath { get; init; }
    public required string Content { get; init; }
}

/// <summary>
/// Edit file tool with exact string matching.
/// </summary>
public sealed class EditFileTool : ToolBase<EditFileParams>
{
    public override string Name => "edit_file";
    public override string Description => "Edit a file by replacing exact string matches";
    
    protected override async Task<ToolResult> ExecuteCoreAsync(EditFileParams p, CancellationToken ct)
    {
        if (!File.Exists(p.FilePath))
            return ToolResult.Fail($"File not found: {p.FilePath}");
        
        try
        {
            var content = await File.ReadAllTextAsync(p.FilePath, ct);
            
            if (!content.Contains(p.OldString))
                return ToolResult.Fail($"Old string not found in file");
            
            var newContent = p.ReplaceAll
                ? content.Replace(p.OldString, p.NewString)
                : ReplaceFirst(content, p.OldString, p.NewString);
            
            await File.WriteAllTextAsync(p.FilePath, newContent, ct);
            return ToolResult.Ok($"Successfully edited {p.FilePath}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to edit file: {ex.Message}", ex);
        }
    }
    
    private static string ReplaceFirst(string text, string search, string replace)
    {
        var index = text.IndexOf(search);
        if (index < 0) return text;
        return text.Substring(0, index) + replace + text.Substring(index + search.Length);
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            file_path = new { type = "string", description = "Path to the file to edit" },
            old_string = new { type = "string", description = "Exact string to find and replace" },
            new_string = new { type = "string", description = "String to replace with" },
            replace_all = new { type = "boolean", description = "Replace all occurrences" }
        },
        required = new[] { "file_path", "old_string", "new_string" }
    });
}

public sealed class EditFileParams
{
    public required string FilePath { get; init; }
    public required string OldString { get; init; }
    public required string NewString { get; init; }
    public bool ReplaceAll { get; init; }
}

/// <summary>
/// Glob tool for file pattern matching.
/// </summary>
public sealed class GlobTool : ToolBase<GlobParams>
{
    public override string Name => "glob";
    public override string Description => "Find files matching glob patterns";
    
    protected override Task<ToolResult> ExecuteCoreAsync(GlobParams p, CancellationToken ct)
    {
        try
        {
            var directory = p.Path ?? Directory.GetCurrentDirectory();
            var files = new List<string>();
            
            var options = EnumerationOptions ?? new EnumerationOptions
            {
                MatchCasing = MatchCasing.PlatformDefault,
                RecurseSubdirectories = true
            };
            
            foreach (var pattern in p.Patterns)
            {
                var matches = Directory.EnumerateFiles(directory, pattern, options);
                files.AddRange(matches);
            }
            
            if (p.Exclude is not null)
            {
                foreach (var exclude in p.Exclude)
                {
                    files.RemoveAll(f => Regex.IsMatch(f, WildcardToRegex(exclude)));
                }
            }
            
            var result = string.Join("\n", files.Distinct().OrderBy(f => f));
            return Task.FromResult(ToolResult.Ok(string.IsNullOrEmpty(result) ? "No files found" : result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Glob failed: {ex.Message}", ex));
        }
    }
    
    private static string WildcardToRegex(string pattern) => "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    
    private static EnumerationOptions? EnumerationOptions => new()
    {
        MatchCasing = MatchCasing.PlatformDefault,
        RecurseSubdirectories = true
    };
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            patterns = new { type = "array", items = new { type = "string" }, description = "Glob patterns to match" },
            path = new { type = "string", description = "Directory to search in" },
            exclude = new { type = "array", items = new { type = "string" }, description = "Patterns to exclude" }
        },
        required = new[] { "patterns" }
    });
}

public sealed class GlobParams
{
    public required string[] Patterns { get; init; }
    public string? Path { get; init; }
    public string[]? Exclude { get; init; }
}

/// <summary>
/// Grep tool for content search.
/// </summary>
public sealed class GrepTool : ToolBase<GrepParams>
{
    public override string Name => "grep";
    public override string Description => "Search for patterns in file contents";
    
    protected override async Task<ToolResult> ExecuteCoreAsync(GrepParams p, CancellationToken ct)
    {
        try
        {
            var directory = p.Path ?? Directory.GetCurrentDirectory();
            var files = new List<string>();
            
            if (p.FilePattern is not null)
            {
                files.AddRange(Directory.EnumerateFiles(directory, p.FilePattern, SearchOption.AllDirectories));
            }
            else
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
            }
            
            var regex = new Regex(p.Pattern, p.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            var results = new StringBuilder();
            var matchCount = 0;
            
            foreach (var file in files)
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            results.AppendLine($"{file}:{i + 1}: {lines[i]}");
                            matchCount++;
                            
                            if (matchCount >= p.MaxResults)
                                break;
                        }
                    }
                }
                catch { /* Skip unreadable files */ }
                
                if (matchCount >= p.MaxResults)
                    break;
            }
            
            return ToolResult.Ok(results.Length > 0 ? results.ToString() : "No matches found");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Grep failed: {ex.Message}", ex);
        }
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string", description = "Regex pattern to search for" },
            path = new { type = "string", description = "Directory to search in" },
            file_pattern = new { type = "string", description = "Glob pattern for files to search" },
            ignore_case = new { type = "boolean", description = "Case insensitive search" },
            max_results = new { type = "integer", description = "Maximum number of results" }
        },
        required = new[] { "pattern" }
    });
}

public sealed class GrepParams
{
    public required string Pattern { get; init; }
    public string? Path { get; init; }
    public string? FilePattern { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public int MaxResults { get; init; } = 100;
}

/// <summary>
/// Delete file tool.
/// </summary>
public sealed class DeleteFileTool : ToolBase<DeleteFileParams>
{
    public override string Name => "delete_file";
    public override string Description => "Delete a file or directory";
    
    protected override Task<ToolResult> ExecuteCoreAsync(DeleteFileParams p, CancellationToken ct)
    {
        try
        {
            if (File.Exists(p.Path))
            {
                File.Delete(p.Path);
                return Task.FromResult(ToolResult.Ok($"Deleted file: {p.Path}"));
            }
            
            if (Directory.Exists(p.Path))
            {
                Directory.Delete(p.Path, p.Recursive);
                return Task.FromResult(ToolResult.Ok($"Deleted directory: {p.Path}"));
            }
            
            return Task.FromResult(ToolResult.Fail($"Path not found: {p.Path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Delete failed: {ex.Message}", ex));
        }
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Path to file or directory to delete" },
            recursive = new { type = "boolean", description = "Delete directory recursively" }
        },
        required = new[] { "path" }
    });
}

public sealed class DeleteFileParams
{
    public required string Path { get; init; }
    public bool Recursive { get; init; }
}
