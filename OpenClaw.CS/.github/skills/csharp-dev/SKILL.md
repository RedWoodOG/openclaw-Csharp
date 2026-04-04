---
name: csharp-dev
description: 'C# and .NET development workflow for building, testing, debugging, and maintaining .NET applications. Use when working with .NET, .NET Core, ASP.NET, Blazor, WPF, WinUI, or any C# project. Triggers: dotnet build, dotnet run, dotnet test, csproj, NuGet, package restore, debugging C#, memory issues, async patterns.'
user-invocable: true
---

# C# Development

## When to Use This Skill

- Building, running, or testing .NET applications
- Working with .NET projects (csproj, sln files)
- NuGet package management
- Debugging C# applications
- ASP.NET Core, Blazor, WPF, WinUI, or console apps
- Async/await patterns, Tasks, concurrency
- Memory profiling or performance issues
- Entity Framework, LINQ questions

## Common Workflows

### Building Projects

```powershell
# Build entire solution
dotnet build <solution.sln>

# Build specific project
dotnet build <project.csproj>

# Build with configuration
dotnet build <project.csproj> -c Release

# Build for specific runtime
dotnet build <project.csproj> -r win-x64
```

### Running Projects

```powershell
# Run console app
dotnet run --project <project.csproj>

# Run with arguments
dotnet run --project <project.csproj> -- --arg1 value

# Run in watch mode (for development)
dotnet watch run --project <project.csproj>
```

### Testing

```powershell
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test project
dotnet test <test.csproj>

# Run tests matching pattern
dotnet test --filter "FullyQualifiedName~TestClassName"
```

### NuGet Package Management

```powershell
# Add package
dotnet add <project.csproj> package <PackageName>

# Add specific version
dotnet add <project.csproj> package <PackageName> --version <version>

# Remove package
dotnet remove <project.csproj> package <PackageName>

# List packages
dotnet list <project.csproj> package
```

### Project Structure Conventions

| Item | Location |
|------|----------|
| Solution file | `.sln` in root |
| Project file | `.csproj` in project folder |
| Source code | `src/` or project root |
| Tests | `tests/` or `*.Tests/` |
| NuGet packages | `packages/` or global cache |
| Build output | `bin/Debug` or `bin/Release` |

### Key File Patterns

- `*.csproj` - Project file (SDK style preferred)
- `*.sln` - Solution file
- `Directory.Build.props` - MSBuild properties shared across projects
- `Directory.Build.targets` - MSBuild targets shared across projects
- `global.json` - SDK version pinning
- `nuget.config` - NuGet sources configuration

## Project Type Detection

Check the `<Project Sdk="">` attribute in csproj files:
- `Microsoft.NET.Sdk` - Modern .NET SDK project
- `Microsoft.NET.Sdk.Web` - ASP.NET Core web app
- `Microsoft.NET.Sdk.Razor` - Blazor/Razor
- `Microsoft.NET.Sdk.Windows` - WPF/WinUI/Windows desktop

## Async Patterns in C#

```csharp
// Proper async pattern
public async Task<Result> DoSomethingAsync()
{
    await Task.Delay(100);
    return Result.Success;
}

// CancellationToken usage
public async Task DoWorkAsync(CancellationToken ct)
{
    for (int i = 0; i < 10; i++)
    {
        ct.ThrowIfCancellationRequested();
        await ProcessAsync(i, ct);
    }
}

// Parallel execution
var tasks = items.Select(async item => await ProcessAsync(item));
var results = await Task.WhenAll(tasks);
```

## Debugging Tips

1. **Attach to process**: `Debug > Attach to Process` (Ctrl+Alt+P)
2. **Breakpoints**: F9 toggle, conditional breakpoints supported
3. **Immediate window**: Evaluate expressions during debug
4. **Watch window**: Monitor variables/expressions
5. **Launch profiles**: Configure in `launchSettings.json`

## Common Issues & Solutions

| Issue | Solution |
|-------|----------|
| "Could not find dotnet SDK" | Check `global.json` version, verify PATH |
| "Package restore failed" | Run `dotnet restore` with `--verbosity detailed` |
| "Ambiguous reference" | Check for duplicate using statements or package conflicts |
| "Missing Microsoft.NET.Sdk" | Add `<Project Sdk="Microsoft.NET.Sdk">` to csproj |
| "Target framework not found" | Install the SDK/runtime for that framework |

## Resources

- [C# documentation](https://docs.microsoft.com/dotnet/csharp/)
- [.NET documentation](https://docs.microsoft.com/dotnet/)
- [NuGet package reference](https://docs.microsoft.com/nuget/)
