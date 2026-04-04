namespace OpenClaw.LLM;

using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// OpenAI API client with streaming support.
/// </summary>
public sealed class OpenAIClient : IModelClient
{
    private readonly HttpClient _httpClient;
    private readonly ModelConfig _config;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    
    public OpenAIClient(ModelConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        _httpClient = new HttpClient(handler ?? new HttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }
    
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<ApiMessage> messages,
        IEnumerable<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildPayload(systemPrompt, messages, tools, stream: true);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        var request = new HttpRequestMessage(HttpMethod.Post, GetEndpoint())
        {
            Content = content
        };
        
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        
        var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        
        var toolUseBuilder = new Dictionary<string, (string Name, StringBuilder Json)>();
        var inputTokens = 0;
        var outputTokens = 0;
        
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            
            var choice = choices[0];
            var delta = choice.GetProperty("delta");
            
            // Content
            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var text = contentProp.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return new StreamEvent.TokenDelta(text);
            }
            
            // Tool calls
            if (delta.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var index = tc.GetProperty("index").GetInt32();
                    var id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() : $"tool_{index}";
                    var function = tc.GetProperty("function");
                    var name = function.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
                    var args = function.TryGetProperty("arguments", out var argsProp) ? argsProp.GetString() : "";
                    
                    if (!string.IsNullOrEmpty(name))
                    {
                        toolUseBuilder[id!] = (name, new StringBuilder());
                        yield return new StreamEvent.ToolUseStart(id!, name);
                    }
                    
                    if (!string.IsNullOrEmpty(args) && toolUseBuilder.TryGetValue(id!, out var builder))
                    {
                        builder.Json.Append(args);
                        yield return new StreamEvent.ToolUseDelta(id!, args);
                    }
                }
            }
            
            // Finish
            if (choice.TryGetProperty("finish_reason", out var finishProp) && finishProp.ValueKind == JsonValueKind.String)
            {
                var finishReason = finishProp.GetString();
                
                foreach (var (id, (name, jsonBuilder)) in toolUseBuilder)
                {
                    var fullJson = jsonBuilder.ToString();
                    JsonElement args;
                    var parseError = false;
                    try
                    {
                        args = JsonDocument.Parse(fullJson).RootElement;
                    }
                    catch (JsonException)
                    {
                        parseError = true;
                        args = default;
                    }
                    if (parseError)
                    {
                        // Skip invalid tool arguments
                        continue;
                    }
                    yield return new StreamEvent.ToolUseComplete(id, name, args.Clone());
                }
                
                if (root.TryGetProperty("usage", out var usage))
                {
                    inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                    outputTokens = usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
                }
                
                yield return new StreamEvent.MessageComplete(finishReason ?? "stop", new UsageStats(inputTokens, outputTokens));
            }
        }
    }
    
    public async Task<CompletionResponse> CompleteAsync(
        string? systemPrompt,
        IEnumerable<ApiMessage> messages,
        IEnumerable<ToolDefinition>? tools,
        CancellationToken ct = default)
    {
        var payload = BuildPayload(systemPrompt, messages, tools, stream: false);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        var response = await _httpClient.PostAsync(GetEndpoint(), content, ct);
        response.EnsureSuccessStatusCode();
        
        var responseStr = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseStr);
        var root = doc.RootElement;
        
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var textContent = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var stopReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() ?? "stop" : "stop";
        
        var toolUses = new List<ToolUseBlock>();
        if (message.TryGetProperty("tool_calls", out var toolCalls))
        {
            foreach (var tc in toolCalls.EnumerateArray())
            {
                var function = tc.GetProperty("function");
                toolUses.Add(new ToolUseBlock(
                    tc.GetProperty("id").GetString() ?? "",
                    function.GetProperty("name").GetString() ?? "",
                    JsonDocument.Parse(function.GetProperty("arguments").GetString() ?? "{}").RootElement
                ));
            }
        }
        
        var usage = root.TryGetProperty("usage", out var u)
            ? new UsageStats(u.GetProperty("prompt_tokens").GetInt32(), u.GetProperty("completion_tokens").GetInt32())
            : null;
        
        return new CompletionResponse
        {
            Content = textContent,
            StopReason = stopReason,
            ToolUses = toolUses.Count > 0 ? toolUses : null,
            Usage = usage
        };
    }
    
    private string GetEndpoint()
    {
        return $"{_config.BaseUrl ?? "https://api.openai.com"}/v1/chat/completions";
    }
    
    private object BuildPayload(string? systemPrompt, IEnumerable<ApiMessage> messages, IEnumerable<ToolDefinition>? tools, bool stream)
    {
        var formattedMessages = new List<object>();
        
        if (!string.IsNullOrEmpty(systemPrompt))
            formattedMessages.Add(new { role = "system", content = systemPrompt });
        
        foreach (var msg in messages)
        {
            formattedMessages.Add(new { role = msg.Role, content = msg.Content ?? "" });
        }
        
        var payload = new Dictionary<string, object>
        {
            ["model"] = _config.Model,
            ["messages"] = formattedMessages,
            ["temperature"] = _config.Temperature,
            ["max_tokens"] = _config.MaxTokens,
        };
        
        if (stream)
            payload["stream"] = true;
        
        if (tools is not null)
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            }).ToList();
        }
        
        return payload;
    }
}
