using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.ViewModels;

namespace ClaudeUsageMonitor.App.Widget;

public partial class WidgetWindow : Window
{
    private readonly WidgetViewModel _viewModel;
    private readonly DispatcherTimer _tickTimer;

    /// <summary>Floating 모드에서 드래그 이동이 끝났을 때(위치 저장용).</summary>
    public event Action<double, double>? Moved;

    public bool AllowDrag { get; set; }

    public WidgetWindow(WidgetViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _tickTimer.Tick += (_, _) => _viewModel.Tick(DateTimeOffset.UtcNow);
        _tickTimer.Start();

        SourceInitialized += (_, _) =>
            WindowStyling.MakeToolWindowNoActivate(new WindowInteropHelper(this).Handle);

        MouseLeftButtonDown += OnDragStart;

        // WS_EX_NOACTIVATE 창에서도 우클릭 메뉴가 확실히 열리도록 명시 처리
        MouseRightButtonUp += (_, e) =>
        {
            if (ContextMenu is not null)
            {
                ContextMenu.PlacementTarget = this;
                ContextMenu.IsOpen = true;
                e.Handled = true;
            }
        };
    }

    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>컨텍스트 메뉴가 열려 있는 동안 topmost 재주장을 멈추기 위한 플래그.</summary>
    public bool IsContextMenuOpen => ContextMenu is { IsOpen: true };

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!AllowDrag)
        {
            return;
        }
        try
        {
            DragMove();
            Moved?.Invoke(Left, Top);
        }
        catch (InvalidOperationException)
        {
            // 마우스 버튼이 이미 떨어진 경우 등 — 무시
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _tickTimer.Stop();
        base.OnClosed(e);
    }
}
