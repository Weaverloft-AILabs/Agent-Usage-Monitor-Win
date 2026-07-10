using System.Windows;
using System.Windows.Input;

namespace ClaudeUsageMonitor.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerViewModel _viewModel;

    public MainWindow(InstallerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }

    private void OnDragStrip(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseIfAllowed();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseIfAllowed();
            e.Handled = true;
        }
    }

    /// <summary>✕/Esc 닫기는 준비·완료·오류 상태에서만 — 진행 중 닫기 = 반쯤 설치 위험 (취소 링크 사용).</summary>
    private void CloseIfAllowed()
    {
        if (_viewModel.State != InstallerState.Progress)
        {
            Close();
        }
    }
}
