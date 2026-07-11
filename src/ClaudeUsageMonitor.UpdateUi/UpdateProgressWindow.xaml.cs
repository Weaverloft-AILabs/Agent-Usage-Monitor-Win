using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace ClaudeUsageMonitor.UpdateUi;

/// <summary>공용 브랜드 진행 창 — 인스톨러 MainWindow에서 이동. DataContext는 UpdateFlowViewModel(파생 포함).</summary>
public partial class UpdateProgressWindow : Window
{
    private readonly UpdateFlowViewModel _viewModel;

    public UpdateProgressWindow(UpdateFlowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
    }

    /// <summary>모든 닫기 경로(✕/Esc뿐 아니라 Alt+F4·작업표시줄 닫기 포함)의 단일 관문 —
    /// 진행 중 창이 닫히면 백엔드 프로세스만 남는 유령 설치가 된다.
    /// 예외: PendingRestart(SuppressCloseGuard) 이후에는 앱 Shutdown이 정당한 닫기.
    /// 참고: WPF Application.Shutdown()은 Closing의 e.Cancel을 무시하므로 이 가드의 실효 범위는
    /// 사용자 ✕/Esc/Alt+F4에 한정된다 (트레이 '종료'는 다운로드 중에도 막지 못함 — 마커 미기록이라 무해).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.State == UpdateFlowState.Progress && !_viewModel.SuppressCloseGuard)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
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
        if (_viewModel.State != UpdateFlowState.Progress || _viewModel.SuppressCloseGuard)
        {
            Close();
        }
    }
}
