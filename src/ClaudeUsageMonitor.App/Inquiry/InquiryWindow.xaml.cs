using System.ComponentModel;
using System.Windows;
using ClaudeUsageMonitor.App.ViewModels;

namespace ClaudeUsageMonitor.App.Inquiry;

public partial class InquiryWindow : Window
{
    public InquiryWindow(InquiryViewModel viewModel)
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
