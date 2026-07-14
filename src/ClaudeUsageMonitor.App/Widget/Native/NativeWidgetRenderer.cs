using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor.App.Widget.Native;

/// <summary>
/// 임베드 위젯의 GDI+ 렌더러 — WidgetWindow.xaml/GaugeBar.xaml의 레이아웃(96dpi DIP 기준)을
/// 물리 픽셀로 재현한다. UpdateLayeredWindow용 32bpp ARGB 비트맵을 생성.
/// </summary>
internal static class NativeWidgetRenderer
{
    // GaugeBar.xaml / WidgetWindow.xaml의 레이아웃 상수 (DIP)
    private const float BarWidth = 56f;
    private const float BarHeight = 7f;
    private const float RowSideMargin = 6f;
    private const float RowVerticalMargin = 1.5f;
    private const float RowHeight = 15f;
    private const float LabelMinWidth = 18f;
    private const float PercentMinWidth = 26f;
    private const float ResetMinWidth = 34f;
    private const float CornerRadius = 6f;

    /// <summary>스냅샷을 렌더한 비트맵 반환 (호출자가 Dispose). scale = dpi / 96.</summary>
    public static Bitmap Render(NativeWidgetSnapshot s, double scale)
    {
        // 1) 측정 패스로 크기 산출 → 2) 본 렌더
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            var size = Measure(g, s, scale);
            var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
            try
            {
                using var canvas = Graphics.FromImage(bitmap);
                Draw(canvas, s, scale, size);
                return bitmap;
            }
            catch
            {
                // Draw 중 GDI+ 예외 시 반환 못 한 비트맵을 해제 (호출자 using이 못 받음)
                bitmap.Dispose();
                throw;
            }
        }
    }

    private static Font RowFont(string text, float diPixels, double scale, FontStyle style)
    {
        // Segoe UI에는 한글이 없음 — 비ASCII 텍스트는 한국어 UI 폰트로
        var family = text.Any(c => c > 0x2000) ? "Malgun Gothic" : "Segoe UI";
        return new Font(family, (float)(diPixels * scale), style, GraphicsUnit.Pixel);
    }

    private static SizeF Text(Graphics g, string text, float px, double scale, FontStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return SizeF.Empty;
        }
        using var font = RowFont(text, px, scale, style);
        return g.MeasureString(text, font);
    }

    private static Size Measure(Graphics g, NativeWidgetSnapshot s, double scale)
    {
        float S(double v) => (float)(v * scale);
        float contentWidth;
        float contentHeight;

        if (s.CliMissing)
        {
            var warn = Text(g, CliMissingText, 12f, scale, FontStyle.Bold);
            contentWidth = S(RowSideMargin) * 2 + warn.Width;
            contentHeight = S(RowHeight * 2);
        }
        else if (s.IsLoading)
        {
            var load = Text(g, LoadingText, 12f, scale, FontStyle.Regular);
            contentWidth = S(RowSideMargin) * 2 + load.Width;
            contentHeight = S(RowHeight * 2);
        }
        else
        {
            // 두 행 중 넓은 쪽 (5H 행에만 소진 예측이 붙음)
            var reset5 = Math.Max(S(ResetMinWidth), Text(g, s.FiveHourResetText, 9f, scale, FontStyle.Regular).Width);
            var reset7 = Math.Max(S(ResetMinWidth), Text(g, s.SevenDayResetText, 9f, scale, FontStyle.Regular).Width);
            var prediction = string.IsNullOrEmpty(s.ExhaustionText)
                ? 0f
                : S(4) + Text(g, s.ExhaustionText, 9f, scale, FontStyle.Bold).Width;

            var rowCommon = S(LabelMinWidth) + S(5) + S(BarWidth) + S(5) + S(PercentMinWidth) + S(3);
            var row5 = rowCommon + reset5 + prediction;
            var row7 = rowCommon + reset7;
            contentWidth = S(RowSideMargin) * 2 + Math.Max(row5, row7);
            contentHeight = S(RowHeight) * 2;
        }

        if (s.UpdateAvailable)
        {
            contentWidth += S(2) + Text(g, UpdateGlyph, 13f, scale, FontStyle.Bold).Width + S(4);
        }

        // Border 1px + Padding(2,1)
        var width = (int)Math.Ceiling(contentWidth + S(2) * 2 + 2);
        var height = (int)Math.Ceiling(contentHeight + S(1) * 2 + 2);
        return new Size(width, height);
    }

    private const string CliMissingText = "⚠ Claude Code 미감지";
    private const string LoadingText = "로딩중…";
    private const string UpdateGlyph = "⬆";
    private static readonly Color CliMissingColor = Color.FromArgb(0xFF, 0xF0, 0xA0, 0x30);

    private static void Draw(Graphics g, NativeWidgetSnapshot s, double scale, Size size)
    {
        float S(double v) => (float)(v * scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias; // ClearType은 투명 배경에서 알파 파손
        g.Clear(Color.Transparent);

        // 배경 + 1px 보더 (CornerRadius 6)
        var radius = S(CornerRadius);
        using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f), radius))
        {
            using var back = new SolidBrush(s.Palette.Background);
            g.FillPath(back, path);
            using var border = new Pen(s.Palette.Border, 1f);
            g.DrawPath(border, path);
        }

        var left = 1f + S(2) + S(RowSideMargin);
        var contentRight = DrawBody(g, s, scale, size, left);

        if (s.UpdateAvailable)
        {
            using var font = new Font("Segoe UI Symbol", (float)(13 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(s.Palette.Prediction);
            var text = g.MeasureString(UpdateGlyph, font);
            g.DrawString(UpdateGlyph, font, brush,
                contentRight + S(2), (size.Height - text.Height) / 2f);
        }
    }

    /// <summary>본문(게이지 2행 또는 CLI 미감지 경고)을 그리고 오른쪽 끝 X를 반환.</summary>
    private static float DrawBody(Graphics g, NativeWidgetSnapshot s, double scale, Size size, float left)
    {
        float S(double v) => (float)(v * scale);

        if (s.CliMissing)
        {
            using var font = RowFont(CliMissingText, 12f, scale, FontStyle.Bold);
            using var brush = new SolidBrush(CliMissingColor);
            var text = g.MeasureString(CliMissingText, font);
            g.DrawString(CliMissingText, font, brush, left, (size.Height - text.Height) / 2f);
            return left + text.Width;
        }

        if (s.IsLoading)
        {
            using var font = RowFont(LoadingText, 12f, scale, FontStyle.Regular);
            using var brush = new SolidBrush(s.Palette.Text);
            var text = g.MeasureString(LoadingText, font);
            g.DrawString(LoadingText, font, brush, left, (size.Height - text.Height) / 2f);
            return left + text.Width;
        }

        var rowHeight = S(RowHeight);
        var top = (size.Height - rowHeight * 2) / 2f;
        var right5 = DrawGaugeRow(g, s, scale, "5H", s.FiveHourPct, s.FiveHourBar,
            s.FiveHourResetText, s.ExhaustionText, left, top, rowHeight);
        var right7 = DrawGaugeRow(g, s, scale, "7D", s.SevenDayPct, s.SevenDayBar,
            s.SevenDayResetText, "", left, top + rowHeight, rowHeight);
        return Math.Max(right5, right7);
    }

    private static float DrawGaugeRow(
        Graphics g, NativeWidgetSnapshot s, double scale,
        string label, double percent, Color barColor, string resetText, string predictionText,
        float x, float rowTop, float rowHeight)
    {
        float S(double v) => (float)(v * scale);
        float CenterY(float h) => rowTop + (rowHeight - h) / 2f;

        // 라벨 (10px Bold, minwidth 18)
        using (var font = RowFont(label, 10f, scale, FontStyle.Bold))
        using (var brush = new SolidBrush(s.Palette.Text))
        {
            var text = g.MeasureString(label, font);
            g.DrawString(label, font, brush, x, CenterY(text.Height));
        }
        x += S(LabelMinWidth) + S(5);

        // 게이지 바 56×7 (트랙 + 채움, 라운드 3.5)
        var barTop = CenterY(S(BarHeight));
        var track = new RectangleF(x, barTop, S(BarWidth), S(BarHeight));
        using (var path = RoundedRect(track, S(BarHeight) / 2f))
        using (var trackBrush = new SolidBrush(s.Palette.GaugeTrack))
        {
            g.FillPath(trackBrush, path);
        }
        var pct = Math.Clamp(percent, 0, 100);
        var fillWidth = (float)(S(BarWidth) * pct / 100.0);
        if (fillWidth >= 1f)
        {
            var fill = new RectangleF(x, barTop, fillWidth, S(BarHeight));
            using var path = RoundedRect(fill, Math.Min(S(BarHeight) / 2f, fillWidth / 2f));
            using var fillBrush = new SolidBrush(barColor);
            g.FillPath(fillBrush, path);
        }
        x += S(BarWidth) + S(5);

        // 퍼센트 (10px SemiBold→Bold 근사, minwidth 26)
        var pctText = $"{pct:0}%";
        using (var font = RowFont(pctText, 10f, scale, FontStyle.Bold))
        using (var brush = new SolidBrush(s.Palette.TextStrong))
        {
            var text = g.MeasureString(pctText, font);
            g.DrawString(pctText, font, brush, x, CenterY(text.Height));
            x += Math.Max(S(PercentMinWidth), text.Width) + S(3);
        }

        // 리셋 카운트다운 (9px, dim, minwidth 34)
        using (var font = RowFont(resetText, 9f, scale, FontStyle.Regular))
        using (var brush = new SolidBrush(s.Palette.TextDim))
        {
            var text = g.MeasureString(resetText, font);
            g.DrawString(resetText, font, brush, x, CenterY(text.Height));
            x += Math.Max(S(ResetMinWidth), text.Width);
        }

        // 소진 예측 (9px Bold, 코랄 — 5H 행 전용)
        if (!string.IsNullOrEmpty(predictionText))
        {
            x += S(4);
            using var font = RowFont(predictionText, 9f, scale, FontStyle.Bold);
            using var brush = new SolidBrush(s.Palette.Prediction);
            var text = g.MeasureString(predictionText, font);
            g.DrawString(predictionText, font, brush, x, CenterY(text.Height));
            x += text.Width;
        }

        return x;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        if (radius < 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }
        var d = radius * 2f;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
