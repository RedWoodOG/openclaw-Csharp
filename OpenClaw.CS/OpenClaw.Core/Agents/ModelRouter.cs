namespace OpenClaw.Agents;

using OpenClaw.Config;
using OpenClaw.LLM;
using OpenClaw.Providers;

#pragma warning disable CS8981

/// <summary>
/// Model routing configuration.
/// </summary>
public sealed class ModelRouterConfig
{
    public string? DefaultProvider { get; init; }
    public string? DefaultModel { get; init; }
    public bool AllowProviderFallback { get; init; } = true;
    public bool AllowModelFallback { get; init; } = true;
    public int MaxContextWindow { get; init; } = 200000;
}

/// <summary>
/// Model router - resolves the appropriate model for a request.
/// </summary>
public sealed class ModelRouter
{
    private readonly ModelRouterConfig _config;
    private readonly IProviderCatalog _catalog;
    private readonly IProviderApiKeyResolver? _apiKeyResolver;
    private readonly IConfigProvider? _configProvider;
    
    public ModelRouter(
        ModelRouterConfig? config = null,
        IProviderCatalog? catalog = null,
        IProviderApiKeyResolver? apiKeyResolver = null,
        IConfigProvider? configProvider = null)
    {
        _config = config ?? new ModelRouterConfig();
        _catalog = catalog ?? new ProviderCatalog();
        _apiKeyResolver = apiKeyResolver;
        _configProvider = configProvider;
    }
    
    /// <summary>
    /// Resolve the best model for a request.
    /// </summary>
    public Task<ModelSelection> ResolveAsync(string? modelHint = null, CancellationToken ct = default)
    {
        // 1. Check for explicit override (hook/agent override)
        if (!string.IsNullOrEmpty(modelHint))
        {
            var resolved = ResolveModelHint(modelHint);
            if (resolved != null)
            {
                return Task.FromResult(resolved);
            }
        }
        
        // 2. Check config for default
        var configSelection = ResolveFromConfig();
        if (configSelection != null)
        {
            return Task.FromResult(configSelection);
        }
        
        // 3. Fall back to built-in defaults
        return Task.FromResult(new ModelSelection
        {
            ProviderId = _config.DefaultProvider ?? "openai",
            ModelId = _config.DefaultModel ?? "gpt-4o",
            ResolvedModel = $"{_config.DefaultProvider ?? "openai"}/{_config.DefaultModel ?? "gpt-4o"}",
            IsOverride = false
        });
    }
    
    /// <summary>
    /// Resolve a model hint (could be "provider/model", just "model", or "latest" alias).
    /// </summary>
    private ModelSelection? ResolveModelHint(string hint)
    {
        // Handle "provider/model" format
        if (hint.Contains('/'))
        {
            var parts = hint.Split('/', 2);
            var providerId = parts[0];
            var modelId = parts[1];
            
            if (_catalog.IsEnabled(providerId) || _config.AllowProviderFallback)
            {
                return new ModelSelection
                {
                    ProviderId = providerId,
                    ModelId = modelId,
                    ResolvedModel = hint,
                    IsOverride = true
                };
            }
        }
        
        // Try to find model across providers
        if (_config.AllowProviderFallback)
        {
            foreach (var entry in _catalog.GetAllProviders())
            {
                if (!entry.IsEnabled) continue;
                
                var model = entry.Provider.Models.FirstOrDefault(m => 
                    string.Equals(m.Id, hint, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Name, hint, StringComparison.OrdinalIgnoreCase));
                
                if (model != null)
                {
                    return new ModelSelection
                    {
                        ProviderId = entry.Provider.Id,
                        ModelId = model.Id,
                        ResolvedModel = $"{entry.Provider.Id}/{model.Id}",
                        IsOverride = true
                    };
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Resolve from config settings.
    /// </summary>
    private ModelSelection? ResolveFromConfig()
    {
        // Try to get from config provider
        var agentConfig = _configProvider?.Get("agent");
        if (agentConfig == null) return null;
        
        var model = agentConfig.GetValue<string?>("model");
        var provider = agentConfig.GetValue<string?>("provider");
        
        if (!string.IsNullOrEmpty(model) || !string.IsNullOrEmpty(provider))
        {
            return new ModelSelection
            {
                ProviderId = provider ?? _config.DefaultProvider ?? "openai",
                ModelId = model ?? _config.DefaultModel ?? "gpt-4o",
                ResolvedModel = $"{provider ?? _config.DefaultProvider ?? "openai"}/{model ?? _config.DefaultModel ?? "gpt-4o"}",
                IsOverride = false
            };
        }
        
        return null;
    }
    
    /// <summary>
    /// Get the effective model configuration for API calls.
    /// </summary>
    public ModelConfig GetModelConfig(ModelSelection selection)
    {
        var entry = _catalog.GetProvider(selection.ProviderId);
        var model = entry?.Provider.Models.FirstOrDefault(m => m.Id == selection.ModelId);
        
        // Resolve API key
        var apiKeyResult = _apiKeyResolver?.Resolve(selection.ProviderId) 
            ?? new ProviderApiKeyResult(null, null, false);
        
        return new ModelConfig
        {
            Provider = selection.ProviderId,
            Model = selection.ModelId,
            ApiKey = apiKeyResult.ApiKey,
            BaseUrl = apiKeyResult.BaseUrl ?? entry?.BaseUrl,
            MaxTokens = model?.MaxOutputTokens ?? 4096,
            Success = true
        };
    }
    
    /// <summary>
    /// Check if a model's context window can handle the requested size.
    /// </summary>
    public bool CanHandleContextWindow(ModelSelection selection, int requiredTokens)
    {
        var entry = _catalog.GetProvider(selection.ProviderId);
        var model = entry?.Provider.Models.FirstOrDefault(m => m.Id == selection.ModelId);
        
        if (model?.ContextWindow == null) return true; // Unknown = assume OK
        return requiredTokens <= model.ContextWindow;
    }
    
    /// <summary>
    /// Get available models from catalog.
    /// </summary>
    public IReadOnlyList<ProviderModel> GetAvailableModels(string? providerFilter = null)
    {
        if (!string.IsNullOrEmpty(providerFilter))
        {
            return _catalog.GetModels(providerFilter);
        }
        
        var all = new List<ProviderModel>();
        foreach (var entry in _catalog.GetAllProviders())
        {
            if (entry.IsEnabled)
            {
                all.AddRange(entry.Provider.Models);
            }
        }
        return all;
    }
}

#pragma warning restore CS8981
