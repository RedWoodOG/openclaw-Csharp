namespace OpenClaw.Mcp;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

/// <summary>
/// MCP transport interface.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task<JsonElement> SendRequestAsync(string method, JsonElement? parameters = null, CancellationToken ct = default);
    IAsyncEnumerable<McpNotification> GetNotifications(CancellationToken ct = default);
    bool IsConnected { get; }
}

/// <summary>
/// MCP notification.
/// </summary>
public sealed record McpNotification(string Method, JsonElement? Params);

/// <summary>
/// MCP tool definition.
/// </summary>
public sealed record McpToolDefinition(string Name, string? Description, JsonElement? InputSchema);

/// <summary>
/// MCP server configuration.
/// </summary>
public abstract record McpServerConfig(string Name);

public sealed record McpStdioConfig(string Name, string Command, string[]? Args = null, Dictionary<string, string>? Env = null) : McpServerConfig(Name);
public sealed record McpHttpConfig(string Name, Uri Url, Dictionary<string, string>? Headers = null) : McpServerConfig(Name);

/// <summary>
/// MCP exception.
/// </summary>
public sealed class McpException : Exception
{
    public int Code { get; }
    public McpException(string message, int code = -1) : base(message) { Code = code; }
}

/// <summary>
/// Stdio MCP transport.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly McpStdioConfig _config;
    private readonly Process _process = new();
    private readonly Channel<McpNotification> _notifications = Channel.CreateUnbounded<McpNotification>();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private int _requestId = 0;
    
    public bool IsConnected => _process is { HasExited: false };
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    
    public StdioMcpTransport(McpStdioConfig config) => _config = config;
    
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        if (_config.Args is not null)
            psi.ArgumentList.Add(string.Join(" ", _config.Args));
        
        if (_config.Env is not null)
            foreach (var (k, v) in _config.Env)
                psi.Environment[k] = v;
        
        _process.StartInfo = psi;
        _process.Start();
        
        _ = Task.Run(() => ReadLoop(ct), ct);
        
        // Initialize
        var initParams = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { tools = true },
            clientInfo = new { name = "openclaw-cs", version = "1.0.0" }
        });
        
        await SendRequestAsync("initialize", initParams, ct);
    }
    
    public async Task<JsonElement> SendRequestAsync(string method, JsonElement? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _requestId).ToString();
        var tcs = new TaskCompletionSource<JsonElement>();
        lock (_pending) { _pending[id] = tcs; }
        
        var request = new { jsonrpc = "2.0", id, method, @params = parameters };
        var json = JsonSerializer.Serialize(request, JsonOptions);
        
        await _process.StandardInput.WriteLineAsync(json);
        await _process.StandardInput.FlushAsync(ct);
        
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        
        try { return await tcs.Task.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { lock (_pending) { _pending.Remove(id); } throw new TimeoutException(); }
    }
    
    public IAsyncEnumerable<McpNotification> GetNotifications(CancellationToken ct = default) =>
        _notifications.Reader.ReadAllAsync(ct);
    
    private async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_process.HasExited)
        {
            var line = await _process.StandardOutput.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetString();
                    if (id is not null)
                    {
                        TaskCompletionSource<JsonElement>? tcs;
                        lock (_pending) { _pending.TryGetValue(id, out tcs); _pending.Remove(id); }
                        
                        if (tcs is not null)
                        {
                            if (root.TryGetProperty("error", out var err))
                            {
                                var msg = err.GetProperty("message").GetString() ?? "Unknown error";
                                var code = err.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                                tcs.SetException(new McpException(msg, code));
                            }
                            else if (root.TryGetProperty("result", out var result))
                                tcs.SetResult(result.Clone());
                            else
                                tcs.SetResult(JsonSerializer.SerializeToElement(new { }));
                        }
                    }
                }
                else if (root.TryGetProperty("method", out var methodProp))
                {
                    var method = methodProp.GetString();
                    var @params = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
                    _notifications.Writer.TryWrite(new McpNotification(method ?? "", @params));
                }
            }
            catch { }
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        _notifications.Writer.TryComplete();
        try { _process.Kill(true); } catch { }
        _process.Dispose();
    }
}

/// <summary>
/// MCP server connection.
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private IMcpTransport? _transport;
    private readonly List<McpToolDefinition> _tools = new();
    
    public string Name => _config.Name;
    public IReadOnlyList<McpToolDefinition> Tools => _tools;
    public bool IsConnected => _transport?.IsConnected ?? false;
    
    public McpServerConnection(McpServerConfig config) => _config = config;
    
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _transport = _config switch
        {
            McpStdioConfig stdio => new StdioMcpTransport(stdio),
            McpHttpConfig http => throw new NotImplementedException("HTTP transport not yet implemented"),
            _ => throw new ArgumentException($"Unknown config type: {_config.GetType()}")
        };
        
        await _transport.ConnectAsync(ct);
        await DiscoverToolsAsync(ct);
    }
    
    private async Task DiscoverToolsAsync(CancellationToken ct)
    {
        if (_transport is null) return;
        
        try
        {
            var result = await _transport.SendRequestAsync("tools/list", null, ct);
            if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tools.EnumerateArray())
                {
                    _tools.Add(new McpToolDefinition(
                        t.GetProperty("name").GetString() ?? "",
                        t.TryGetProperty("description", out var d) ? d.GetString() : null,
                        t.TryGetProperty("inputSchema", out var s) ? s.Clone() : null
                    ));
                }
            }
        }
        catch (McpException ex) when (ex.Code == -32601) { /* Tools not supported */ }
    }
    
    public async Task<McpToolResult> CallToolAsync(string name, JsonElement? args = null, CancellationToken ct = default)
    {
        if (_transport is null) throw new InvalidOperationException("Not connected");
        
        var @params = JsonSerializer.SerializeToElement(new { name, arguments = args });
        var result = await _transport.SendRequestAsync("tools/call", @params, ct);
        
        return JsonSerializer.Deserialize<McpToolResult>(result) ?? new McpToolResult([]);
    }
    
    public string NormalizeToolName(string toolName) => $"mcp__{_config.Name}__{toolName}";
    
    public async ValueTask DisposeAsync()
    {
        if (_transport is not null) await _transport.DisposeAsync();
    }
}

public sealed record McpToolResult(IReadOnlyList<McpContentBlock> Content, bool IsError = false);

public abstract record McpContentBlock
{
    public sealed record Text(string Value) : McpContentBlock;
    public sealed record Image(string Data, string MimeType) : McpContentBlock;
    public sealed record Resource(string Uri, string? MimeType = null) : McpContentBlock;
}
