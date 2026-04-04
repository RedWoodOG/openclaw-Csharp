namespace OpenClaw.LLM;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// Anthropic Claude API client with streaming support.
/// </summary>
public sealed class AnthropicClient : IModelClient
{
    private readonly HttpClient _httpClient;
    private readonly ModelConfig _config;
    
    private const string ApiVersion = "2023-06-01";
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    
    public AnthropicClient(ModelConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        _httpClient = new HttpClient(handler ?? new HttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        }
        
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
        
        HttpResponseMessage response;
        Exception? connectError = null;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            connectError = ex;
            response = null!;
        }
        
        if (connectError is not null)
        {
            yield return new StreamEvent.StreamError(connectError);
            yield break;
        }
        
        var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        
        var toolUseBuilder = new Dictionary<string, (string Name, StringBuilder Json)>();
        var inputTokens = 0;
        var outputTokens = 0;
        
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            
            var data = line.Substring(6);
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            
            switch (type)
            {
                case "content_block_start":
                {
                    var block = root.GetProperty("content_block");
                    var blockType = block.GetProperty("type").GetString();
                    var index = root.GetProperty("index").GetInt32();
                    
                    if (blockType == "tool_use")
                    {
                        var id = block.GetProperty("id").GetString() ?? $"tool_{index}";
                        var name = block.GetProperty("name").GetString() ?? "";
                        toolUseBuilder[id] = (name, new StringBuilder());
                        yield return new StreamEvent.ToolUseStart(id, name);
                    }
                    break;
                }
                
                case "content_block_delta":
                {
                    var delta = root.GetProperty("delta");
                    var deltaType = delta.GetProperty("type").GetString();
                    
                    if (deltaType == "text_delta")
                    {
                        var text = delta.GetProperty("text").GetString() ?? "";
                        if (!string.IsNullOrEmpty(text))
                            yield return new StreamEvent.TokenDelta(text);
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var partialJson = delta.GetProperty("partial_json").GetString() ?? "";
                        var index = root.GetProperty("index").GetInt32();
                        var toolEntry = toolUseBuilder.ElementAtOrDefault(index);
                        if (!string.IsNullOrEmpty(toolEntry.Key))
                        {
                            toolEntry.Value.Json.Append(partialJson);
                            yield return new StreamEvent.ToolUseDelta(toolEntry.Key, partialJson);
                        }
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        var thinking = delta.GetProperty("thinking").GetString() ?? "";
                        if (!string.IsNullOrEmpty(thinking))
                            yield return new StreamEvent.ThinkingDelta(thinking);
                    }
                    break;
                }
                
                case "content_block_stop":
                {
                    var index = root.GetProperty("index").GetInt32();
                    var toolEntry = toolUseBuilder.ElementAtOrDefault(index);
                    if (!string.IsNullOrEmpty(toolEntry.Key))
                    {
                        var fullJson = toolEntry.Value.Json.ToString();
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
                            break;
                        }
                        yield return new StreamEvent.ToolUseComplete(toolEntry.Key, toolEntry.Value.Name, args.Clone());
                    }
                    break;
                }
                
                case "message_delta":
                {
                    var delta = root.GetProperty("delta");
                    var stopReason = delta.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                    var usage = root.GetProperty("usage");
                    outputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                    
                    if (!string.IsNullOrEmpty(stopReason))
                    {
                        yield return new StreamEvent.MessageComplete(stopReason, 
                            new UsageStats(inputTokens, outputTokens));
                    }
                    break;
                }
                
                case "message_start":
                {
                    var message = root.GetProperty("message");
                    if (message.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                    }
                    break;
                }
                
                case "error":
                {
                    var error = root.GetProperty("error");
                    var message = error.GetProperty("message").GetString() ?? "Unknown error";
                    yield return new StreamEvent.StreamError(new Exception(message));
                    break;
                }
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
        
        var contentBlocks = root.GetProperty("content");
        var textContent = new StringBuilder();
        var toolUses = new List<ToolUseBlock>();
        
        foreach (var block in contentBlocks.EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            if (type == "text")
            {
                textContent.Append(block.GetProperty("text").GetString());
            }
            else if (type == "tool_use")
            {
                toolUses.Add(new ToolUseBlock(
                    block.GetProperty("id").GetString() ?? "",
                    block.GetProperty("name").GetString() ?? "",
                    block.GetProperty("input").Clone()
                ));
            }
        }
        
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";
        var usage = root.TryGetProperty("usage", out var u) 
            ? new UsageStats(
                u.GetProperty("input_tokens").GetInt32(),
                u.GetProperty("output_tokens").GetInt32())
            : null;
        
        return new CompletionResponse
        {
            Content = textContent.ToString(),
            StopReason = stopReason,
            ToolUses = toolUses.Count > 0 ? toolUses : null,
            Usage = usage
        };
    }
    
    private string GetEndpoint()
    {
        return $"{_config.BaseUrl ?? "https://api.anthropic.com"}/v1/messages";
    }
    
    private object BuildPayload(string? systemPrompt, IEnumerable<ApiMessage> messages, IEnumerable<ToolDefinition>? tools, bool stream)
    {
        var formattedMessages = messages.Select(m => new
        {
            role = m.Role,
            content = m.ContentBlocks ?? (object)(m.Content ?? "")
        }).ToList();
        
        var payload = new Dictionary<string, object>
        {
            ["model"] = _config.Model,
            ["messages"] = formattedMessages,
            ["max_tokens"] = _config.MaxTokens,
        };
        
        if (!string.IsNullOrEmpty(systemPrompt))
            payload["system"] = systemPrompt;
        
        if (stream)
            payload["stream"] = true;
        
        if (tools is not null)
        {
            payload["tools"] = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.InputSchema
            }).ToList();
        }
        
        return payload;
    }
}
