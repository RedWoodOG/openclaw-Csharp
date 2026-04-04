using Microsoft.UI.Xaml;
using OpenClaw.Desktop.Views;

namespace OpenClaw.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentFrame.Navigate(typeof(ChatPage));
    }
}
