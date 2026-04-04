using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Core;
using OpenClaw.LLM;
using OpenClaw.Permissions;
using OpenClaw.Tools;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Desktop.Views;

/// <summary>
/// Chat page.
/// </summary>
public sealed partial class ChatPage : Page
{
    private readonly IModelClient _modelClient;
    private readonly OpenClawAgent _agent;
    private readonly Core.Session _session;
    private readonly ToolRegistry _toolRegistry;
    private bool _isBusy;
    
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    
    public ChatPage()
    {
        InitializeComponent();
        
        // Initialize services
        var config = new ModelConfig
        {
            Provider = "anthropic",
            Model = "claude-sonnet-4-20250514",
            ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? ""
        };
        
        _modelClient = new AnthropicClient(config);
        _toolRegistry = new ToolRegistry();
        _toolRegistry.RegisterDefaultTools();
        
        var permissionManager = new PermissionManager();
        
        _agent = new OpenClawAgent(_modelClient, _toolRegistry, permissionManager);
        _session = new Core.Session();
        
        // Add welcome message
        Messages.Add(new ChatMessageViewModel("OpenClaw", "Hello! I'm OpenClaw, your AI coding assistant. How can I help you today?"));
        
        MessagesList.ItemsSource = Messages;
        UpdateSessionInfo();
    }
    
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }
    
    private void PermissionMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_session is null) return;
        _session.Metadata.PermissionMode = PermissionModeBox.SelectedIndex switch
        {
            1 => PermissionMode.Plan,
            2 => PermissionMode.AcceptEdits,
            3 => PermissionMode.Auto,
            4 => PermissionMode.BypassPermissions,
            _ => PermissionMode.Default
        };
    }
    
    private async Task SendMessageAsync()
    {
        if (_isBusy) return;
        
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;
        
        PromptTextBox.Text = "";
        
        // Add user message
        Messages.Add(new ChatMessageViewModel("You", prompt));
        
        _session.AddMessage(new Message { Role = "user", Content = prompt });
        
        SetBusy(true);
        StatusText.Text = "Thinking...";
        
        try
        {
            var responseBuilder = new System.Text.StringBuilder();
            ChatMessageViewModel? assistantMessage = null;
            
            await foreach (var evt in _agent.RunAsync(_session, CancellationToken.None))
            {
                switch (evt)
                {
                    case AgentEvent.MessageDelta msg:
                        responseBuilder.Append(msg.Message.Content);
                        if (assistantMessage is null)
                        {
                            assistantMessage = new ChatMessageViewModel("OpenClaw", responseBuilder.ToString());
                            Messages.Add(assistantMessage);
                        }
                        else
                        {
                            assistantMessage.Content = responseBuilder.ToString();
                        }
                        break;
                        
                    case AgentEvent.ToolCall toolCall:
                        Messages.Add(new ChatMessageViewModel("Tool", $"Calling {toolCall.Name}..."));
                        break;
                        
                    case AgentEvent.Complete complete:
                        StatusText.Text = $"Completed ({complete.StopReason})";
                        break;
                        
                    case AgentEvent.Error error:
                        StatusText.Text = $"Error: {error.Ex.Message}";
                        break;
                }
            }
            
            if (responseBuilder.Length > 0)
            {
                _session.AddMessage(new Message { Role = "assistant", Content = responseBuilder.ToString() });
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
            UpdateSessionInfo();
        }
    }
    
    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        SendButton.IsEnabled = !busy;
        PromptTextBox.IsEnabled = !busy;
    }
    
    private void UpdateSessionInfo()
    {
        SessionIdText.Text = $"ID: {_session.Id.Substring(0, 8)}...";
        TokenCountText.Text = $"{_session.Metadata.TotalTokens} tokens";
        ToolsText.Text = $"{_toolRegistry.GetAllTools().Count} tools loaded";
    }
}

/// <summary>
/// Simple chat message view model.
/// </summary>
public sealed class ChatMessageViewModel
{
    public string Author { get; }
    public string Content { get; set; }
    
    public ChatMessageViewModel(string author, string content)
    {
        Author = author;
        Content = content;
    }
}
