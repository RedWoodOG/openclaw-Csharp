---
name: vscode-customization
description: 'VS Code customization: agent configuration, skills, hooks, prompts, workspace instructions, file patterns, and applyTo rules. Use when creating or editing .instructions.md, .prompt.md, .agent.md, SKILL.md, AGENTS.md, copilot-instructions.md, or configuring VS Code agent behavior. Triggers: customize copilot, agent instructions, slash commands, context patterns.'
user-invocable: true
---

# VS Code Agent Customization

## When to Use This Skill

- Creating or editing agent skills (SKILL.md)
- Setting up workspace instructions (copilot-instructions.md)
- Creating custom agents (.agent.md)
- Configuring file-specific instructions (*.instructions.md)
- Setting up hooks for automation
- Creating prompt templates (*.prompt.md)
- Configuring MCP servers
- Debugging why instructions aren't being applied

## Skill Locations

| Scope | Path |
|-------|------|
| Project skills | `.github/skills/<name>/` |
| Project agents | `.github/agents/<name>/` |
| User prompts | `{{VSCODE_USER_PROMPTS_FOLDER}}/` |

## File Types

### SKILL.md (Skills)

Bundled on-demand workflows with scripts and assets.

```yaml
---
name: skill-name
description: 'Clear description of when to use'
user-invocable: true        # Show as slash command
disable-model-invocation: false  # Auto-load when relevant
---
```

### .instructions.md (File Instructions)

Apply to specific files via `applyTo` patterns.

```yaml
---
name: file-instructions
applyTo:
  - "**/*.cs"
  - "**/*.json"
---
```

### .prompt.md (Prompts)

Parameterized templates for specific tasks.

### .agent.md (Custom Agents)

Subagents with specialized tool restrictions and prompts.

### copilot-instructions.md (Workspace Instructions)

Always-on instructions for the entire workspace.

## applyTo Patterns

Glob patterns that control when file instructions activate:

| Pattern | Matches |
|---------|---------|
| `**/*.cs` | All C# files |
| `**/*.csproj` | Project files only |
| `src/**/*.ts` | TS files in src folder |
| `**/test/**` | Anything in test folders |

## Creating Custom Agents

```yaml
---
name: code-reviewer
description: 'Specialized code review agent'
tools:
  allow:
    - read_file
    - glob
    - grep
  deny:
    - bash
    - write_file
---
```

## Hooks

Automation at agent lifecycle points (stored in `.github/hooks/`).

## Best Practices

1. **Progressive loading**: Keep SKILL.md under 500 lines
2. **Keyword-rich descriptions**: Enable discovery by the model
3. **Self-contained skills**: Include all procedural knowledge
4. **Consistent naming**: lowercase-with-hyphens

## Anti-Patterns

- Vague descriptions ("A helpful skill")
- Monolithic files instead of references
- Name mismatch between folder and SKILL.md
- Missing step-by-step procedures

## Resources

- [VS Code Agent Skills](https://code.visualstudio.com/docs/copilot/customization/agent-skills)
- [Workspace Instructions](https://code.visualstudio.com/docs/copilot/customization/workspace-instructions)
- [Custom Agents](https://code.visualstudio.com/docs/copilot/customization/agents)
