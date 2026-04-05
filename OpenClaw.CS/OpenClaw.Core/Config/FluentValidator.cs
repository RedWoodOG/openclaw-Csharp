namespace OpenClaw.Config;

using System.ComponentModel.DataAnnotations;
using FluentValidation;
using FluentValidation.Results;

#pragma warning disable CS8981

/// <summary>
/// FluentValidation-based config validator.
/// </summary>
public sealed class FluentConfigValidator<T> : ISchemaValidator
{
    private readonly IValidator<T>? _validator;
    private readonly Action<SchemaBuilder<T>>? _builder;
    private Schema? _builtSchema;
    
    public FluentConfigValidator(IValidator<T>? validator = null)
    {
        _validator = validator;
    }
    
    public FluentConfigValidator(Action<SchemaBuilder<T>> builder)
    {
        _builder = builder;
        var schemaBuilder = new SchemaBuilder<T>();
        _builder(schemaBuilder);
        _builtSchema = schemaBuilder.Build();
    }
    
    /// <inheritdoc />
    public ConfigValidationResult Validate(object? value)
    {
        if (_validator != null && value is T typedValue)
        {
            var result = _validator.Validate(typedValue);
            return ConvertResult(result);
        }
        
        if (_builtSchema != null)
        {
            return _builtSchema.Validate(value);
        }
        
        return ConfigValidationResult.Success();
    }
    
    /// <inheritdoc />
    public JsonElement? ToJsonSchema()
    {
        return _builtSchema?.ToJsonSchema();
    }
    
    private static ConfigValidationResult ConvertResult(ValidationResult result)
    {
        if (result.IsValid)
            return ConfigValidationResult.Success();
        
        var errors = result.Errors.Select(e => new ConfigError
        {
            Path = e.PropertyName,
            Message = e.ErrorMessage,
            Code = e.ErrorCode
        }).ToArray();
        
        return ConfigValidationResult.Failure(errors);
    }
}

/// <summary>
/// Agent configuration schema.
/// </summary>
public sealed class AgentConfigSchema : Schema
{
    public override ConfigValidationResult Validate(object? value)
    {
        if (value is not System.Collections.IDictionary dict)
            return ConfigValidationResult.Failure(new ConfigError 
            { 
                Path = "$", 
                Message = "Agent config must be an object" 
            });
        
        var errors = new List<ConfigError>();
        
        // Validate model if present
        if (dict["model"] is string model && string.IsNullOrWhiteSpace(model))
        {
            errors.Add(new ConfigError { Path = "model", Message = "Model cannot be empty" });
        }
        
        // Validate provider if present
        if (dict["provider"] is string provider && string.IsNullOrWhiteSpace(provider))
        {
            errors.Add(new ConfigError { Path = "provider", Message = "Provider cannot be empty" });
        }
        
        // Validate temperature
        if (dict["temperature"] is double temp && (temp < 0 || temp > 2))
        {
            errors.Add(new ConfigError { Path = "temperature", Message = "Temperature must be between 0 and 2" });
        }
        
        // Validate maxTokens
        if (dict["maxTokens"] is int maxTokens && maxTokens < 1)
        {
            errors.Add(new ConfigError { Path = "maxTokens", Message = "maxTokens must be at least 1" });
        }
        
        return errors.Count > 0 
            ? ConfigValidationResult.Failure(errors.ToArray())
            : ConfigValidationResult.Success();
    }
    
    public override JsonElement ToJsonSchema()
    {
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                model = new { type = "string", description = "Model to use (e.g., gpt-4o, claude-3-5-sonnet)" },
                provider = new { type = "string", description = "Provider to use (e.g., openai, anthropic)" },
                temperature = new { type = "number", minimum = 0, maximum = 2, default_value = 0.7 },
                maxTokens = new { type = "integer", minimum = 1, default_value = 4096 },
                timeout = new { type = "integer", description = "Timeout in seconds", default_value = 120 }
            }
        });
    }
}

/// <summary>
/// Gateway configuration schema.
/// </summary>
public sealed class GatewayConfigSchema : Schema
{
    public override ConfigValidationResult Validate(object? value)
    {
        if (value is not System.Collections.IDictionary dict)
            return ConfigValidationResult.Failure(new ConfigError 
            { 
                Path = "$", 
                Message = "Gateway config must be an object" 
            });
        
        var errors = new List<ConfigError>();
        
        // Validate port
        if (dict["port"] is int port && (port < 1 || port > 65535))
        {
            errors.Add(new ConfigError { Path = "port", Message = "Port must be between 1 and 65535" });
        }
        
        // Validate bind
        if (dict["bind"] is string bind)
        {
            if (bind != "loopback" && bind != "all" && bind != "lan")
            {
                errors.Add(new ConfigError { Path = "bind", Message = "Bind must be 'loopback', 'all', or 'lan'" });
            }
        }
        
        return errors.Count > 0 
            ? ConfigValidationResult.Failure(errors.ToArray())
            : ConfigValidationResult.Success();
    }
    
    public override JsonElement ToJsonSchema()
    {
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                port = new { type = "integer", minimum = 1, maximum = 65535, default_value = 18789 },
                bind = new { type = "string", enum = new[] { "loopback", "all", "lan" }, default_value = "loopback" },
                mode = new { type = "string", enum = new[] { "local", "cloud" }, default_value = "local" },
                force = new { type = "boolean", description = "Force restart even if already running", default_value = false }
            }
        });
    }
}

/// <summary>
/// Channel configuration schema.
/// </summary>
public sealed class ChannelConfigSchema : Schema
{
    private readonly string _channelId;
    
    public ChannelConfigSchema(string channelId)
    {
        _channelId = channelId;
    }
    
    public override ConfigValidationResult Validate(object? value)
    {
        if (value is not System.Collections.IDictionary dict)
            return ConfigValidationResult.Failure(new ConfigError 
            { 
                Path = "$", 
                Message = "Channel config must be an object" 
            });
        
        var errors = new List<ConfigError>();
        
        // Validate enabled flag
        if (dict["enabled"] != null && dict["enabled"] is not bool)
        {
            errors.Add(new ConfigError { Path = "enabled", Message = "Enabled must be a boolean" });
        }
        
        // Validate account ID
        if (dict["accountId"] is string accountId && string.IsNullOrWhiteSpace(accountId))
        {
            errors.Add(new ConfigError { Path = "accountId", Message = "Account ID cannot be empty" });
        }
        
        return errors.Count > 0 
            ? ConfigValidationResult.Failure(errors.ToArray())
            : ConfigValidationResult.Success();
    }
    
    public override JsonElement ToJsonSchema()
    {
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                enabled = new { type = "boolean", default_value = true },
                accountId = new { type = "string", description = $"Account ID for {_channelId}" }
            }
        });
    }
}

#pragma warning restore CS8981
