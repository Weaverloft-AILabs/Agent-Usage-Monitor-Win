using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeUsageMonitor.App.Widget;

public partial class GaugeBar : UserControl
{
    private const double BarWidth = 56;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(GaugeBar), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(GaugeBar), new PropertyMetadata(0d, OnChanged));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(GaugeBar), new PropertyMetadata(Brushes.SteelBlue, OnChanged));

    public static readonly DependencyProperty ResetTextProperty = DependencyProperty.Register(
        nameof(ResetText), typeof(string), typeof(GaugeBar), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty PredictionTextProperty = DependencyProperty.Register(
        nameof(PredictionText), typeof(string), typeof(GaugeBar), new PropertyMetadata("", OnChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public string ResetText
    {
        get => (string)GetValue(ResetTextProperty);
        set => SetValue(ResetTextProperty, value);
    }

    /// <summary>소진 예측 텍스트 (빈 문자열이면 숨김).</summary>
    public string PredictionText
    {
        get => (string)GetValue(PredictionTextProperty);
        set => SetValue(PredictionTextProperty, value);
    }

    public GaugeBar()
    {
        InitializeComponent();
        Refresh();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((GaugeBar)d).Refresh();

    private void Refresh()
    {
        LabelText.Text = Label;
        var pct = Math.Clamp(Percent, 0, 100);
        Fill.Width = BarWidth * pct / 100.0;
        Fill.Background = BarBrush;
        PercentText.Text = $"{pct:0}%";
        ResetTextBlock.Text = ResetText;
        PredictionTextBlock.Text = PredictionText;
        PredictionTextBlock.Visibility = string.IsNullOrEmpty(PredictionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
