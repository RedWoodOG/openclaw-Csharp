namespace OpenClaw.Channels;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CS8981

/// <summary>
/// Telegram channel plugin with full implementation.
/// </summary>
public sealed class TelegramChannelPlugin : ChannelPluginBase
{
    private readonly TelegramOptions _options;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, TelegramAccount> _accounts = new();
    private readonly JsonSerializerOptions _jsonOptions;
    
    public TelegramChannelPlugin(TelegramOptions? options = null)
        : base(new ChannelId("telegram"), "Telegram", new ChannelCapabilities(
            ChannelCapability.DirectMessages,
            ChannelCapability.GroupMessages,
            ChannelCapability.Media,
            ChannelCapability.Polls,
            ChannelCapability.Threads,
            ChannelCapability.Reactions,
            ChannelCapability.Mentions,
            ChannelCapability.Markdown))
    {
        _options = options ?? new TelegramOptions();
        _httpClient = new HttpClient { BaseAddress = new Uri("https://api.telegram.org/") };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        
        InitializeAccounts();
    }
    
    private void InitializeAccounts()
    {
        if (_options.BotToken != null)
        {
            _accounts["default"] = new TelegramAccount
            {
                AccountId = "default",
                BotToken = _options.BotToken,
                AllowedUsers = _options.AllowedUsers?.ToList() ?? new List<string>(),
                IsEnabled = true
            };
        }
    }
    
    public override IReadOnlyList<string> GetConfiguredAccounts() => _accounts.Keys.ToList();
    
    public override bool IsEnabled(string accountId)
    {
        return _accounts.TryGetValue(accountId ?? "default", out var account) 
            && account.IsEnabled;
    }
    
    public override ChannelAccountSnapshot GetStatus(string accountId)
    {
        var account = _accounts.GetValueOrDefault(accountId ?? "default");
        return new ChannelAccountSnapshot
        {
            AccountId = accountId,
            ChannelId = Id,
            Status = account?.IsEnabled == true ? ChannelStatus.Connected : ChannelStatus.Disconnected,
            ConnectedAt = DateTimeOffset.UtcNow
        };
    }
    
    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct = default)
    {
        // Verify the bot token is valid
        foreach (var (accountId, account) in _accounts)
        {
            try
            {
                var response = await _httpClient.GetAsync(
                    $"bot{account.BotToken}/getMe", ct);
                
                if (response.IsSuccessStatusCode)
                {
                    account.IsEnabled = true;
                    SetStatus(ChannelStatus.Connected);
                }
                else
                {
                    account.IsEnabled = false;
                }
            }
            catch
            {
                account.IsEnabled = false;
            }
        }
        
        await base.StartAsync(ct);
    }
    
    /// <inheritdoc />
    public override async Task<OutboundDeliveryResult> SendAsync(
        ChannelOutboundContext context, 
        CancellationToken ct = default)
    {
        var account = _accounts.GetValueOrDefault(context.AccountId ?? "default");
        if (account == null || !account.IsEnabled)
        {
            return new OutboundDeliveryResult
            {
                Success = false,
                Error = "Account not configured or disabled"
            };
        }
        
        try
        {
            var payload = new TelegramSendMessageRequest
            {
            ChatId = context.To,
            Text = context.Content ?? "",
                    ? int.Parse(context.ReplyToMessageId) 
                    : null,
                AllowSendingWithoutReply = true
            };
            
            var response = await _httpClient.PostAsJsonAsync(
                $"bot{account.BotToken}/sendMessage",
                payload,
                _jsonOptions,
                ct);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TelegramMessageResponse>(ct);
                return new OutboundDeliveryResult
                {
                    Success = true,
                    MessageId = result?.Result?.MessageId.ToString(),
                    DeliveredAt = DateTimeOffset.UtcNow
                };
            }
            
            return new OutboundDeliveryResult
            {
                Success = false,
                Error = $"Telegram API error: {response.StatusCode}"
            };
        }
        catch (Exception ex)
        {
            return new OutboundDeliveryResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }
    
    /// <summary>
    /// Process an incoming Telegram update from webhook or long polling.
    /// </summary>
    public async Task ProcessUpdateAsync(TelegramUpdate update, CancellationToken ct = default)
    {
        if (update.Message == null) return;
        
        var account = _accounts.GetValueOrDefault("default");
        if (account == null) return;
        
        // Check allowed users if configured
        if (account.AllowedUsers.Count > 0)
        {
            var userId = update.Message.From?.Id.ToString();
            if (userId == null || !account.AllowedUsers.Contains(userId))
            {
                return; // User not allowed
            }
        }
        
        var context = new ChannelInboundContext
        {
            ChannelId = Id,
            AccountId = "default",
            SenderId = update.Message.From?.Id.ToString() ?? "",
            MessageId = update.Message.MessageId.ToString(),
            Content = update.Message.Text ?? "",
            ReplyToMessageId = update.Message.ReplyToMessage?.MessageId.ToString(),
            ThreadId = update.Message.MessageThreadId?.ToString(),
            Sender = new ChannelAccount
            {
                AccountId = update.Message.From?.Id.ToString() ?? "",
                ChannelId = Id,
                DisplayName = update.Message.From?.FirstName,
                Username = update.Message.From?.Username,
                IsBot = update.Message.From?.IsBot ?? false
            },
            Metadata =
            {
                ["chat_id"] = update.Message.Chat.Id.ToString(),
                ["chat_type"] = update.Message.Chat.Type.ToString().ToLowerInvariant(),
                ["chat_title"] = update.Message.Chat.Title ?? ""
            },
            RawPayload = JsonSerializer.Serialize(update, _jsonOptions)
        };
        
        await HandleInboundMessageAsync(context, ct);
    }
    
    private static ChannelChatType MapChatType(string type) => type.ToLowerInvariant() switch
    {
        "private" => ChannelChatType.Direct,
        "group" => ChannelChatType.Group,
        "supergroup" => ChannelChatType.Group,
        "channel" => ChannelChatType.Channel,
        _ => ChannelChatType.Unknown
    };
    
    /// <summary>
    /// Set the webhook URL for receiving updates.
    /// </summary>
    public async Task<bool> SetWebhookAsync(string url, CancellationToken ct = default)
    {
        var account = _accounts.GetValueOrDefault("default");
        if (account == null) return false;
        
        var response = await _httpClient.PostAsJsonAsync(
            $"bot{account.BotToken}/setWebhook",
            new { url },
            _jsonOptions,
            ct);
        
        return response.IsSuccessStatusCode;
    }
}

/// <summary>
/// Telegram options for configuration.
/// </summary>
public sealed class TelegramOptions
{
    public string? BotToken { get; set; }
    public IReadOnlyList<string>? AllowedUsers { get; set; }
    public bool EnableMarkdown { get; set; } = true;
    public int MessageTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Telegram account state.
/// </summary>
internal sealed class TelegramAccount
{
    public required string AccountId { get; init; }
    public required string BotToken { get; init; }
    public List<string> AllowedUsers { get; init; } = new();
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Telegram API types.
/// </summary>
internal enum TelegramChatType
{
    Private,
    Group,
    Supergroup,
    Channel
}

#pragma warning restore CS8981

// Telegram API response types
internal sealed class TelegramUpdate
{
    public int UpdateId { get; init; }
    public TelegramMessage? Message { get; init; }
    public TelegramMessage? EditedMessage { get; init; }
    public TelegramMessage? ChannelPost { get; init; }
}

internal sealed class TelegramMessage
{
    public int MessageId { get; init; }
    public TelegramUser? From { get; init; }
    public TelegramChat Chat { get; init; } = null!;
    public int? MessageThreadId { get; init; }
    public string? Text { get; init; }
    public TelegramMessage? ReplyToMessage { get; init; }
    public DateTimeOffset Date { get; init; }
}

internal sealed class TelegramUser
{
    public int Id { get; init; }
    public bool IsBot { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Username { get; init; }
}

internal sealed class TelegramChat
{
    public int Id { get; init; }
    public string Type { get; init; } = "private";
    public string? Title { get; init; }
    public string? Username { get; init; }
}

internal sealed class TelegramSendMessageRequest
{
    public string ChatId { get; init; } = "";
    public string Text { get; init; } = "";
    public string? ParseMode { get; init; }
    public int? ReplyToMessageId { get; init; }
    public bool AllowSendingWithoutReply { get; init; } = true;
}

internal sealed class TelegramMessageResponse
{
    public bool Ok { get; init; }
    public TelegramMessageResult? Result { get; init; }
}

internal sealed class TelegramMessageResult
{
    public int MessageId { get; init; }
}
