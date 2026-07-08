using System.ComponentModel;
using System.Windows;
using ClaudeUsageMonitor.App.ViewModels;

namespace ClaudeUsageMonitor.App.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
