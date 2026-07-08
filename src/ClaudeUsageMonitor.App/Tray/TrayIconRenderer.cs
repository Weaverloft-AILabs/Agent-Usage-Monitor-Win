using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor.App.Tray;

/// <summary>5시간 사용률(%)을 트레이 아이콘 비트맵으로 렌더링.</summary>
public static class TrayIconRenderer
{
    public static Icon Render(double fiveHourPct, bool warning, bool stale)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var ringColor = warning
            ? Color.FromArgb(255, 224, 80, 60)   // 경고: 적색
            : Color.FromArgb(255, 90, 170, 250); // 평상: 청색
        if (stale)
        {
            ringColor = Color.FromArgb(255, 140, 140, 140);
        }

        // 안쪽 다크 원판 — 밝은 트레이 배경에서도 텍스트 대비 확보
        using (var inner = new SolidBrush(Color.FromArgb(230, 28, 30, 40)))
        {
            g.FillEllipse(inner, 2, 2, size - 5, size - 5);
        }

        // 배경 링 + 사용률 아크
        using (var back = new Pen(Color.FromArgb(70, ringColor), 4))
        {
            g.DrawEllipse(back, 2, 2, size - 5, size - 5);
        }
        using (var arc = new Pen(ringColor, 4))
        {
            var sweep = (float)(Math.Clamp(fiveHourPct, 0, 100) * 3.6);
            if (sweep > 0)
            {
                g.DrawArc(arc, 2, 2, size - 5, size - 5, -90, sweep);
            }
        }

        // 중앙 % 텍스트 (100은 'F'로 축약)
        var text = fiveHourPct >= 99.5 ? "F" : Math.Round(fiveHourPct).ToString();
        using var font = new Font("Segoe UI", text.Length > 2 ? 9 : 11, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        var measured = g.MeasureString(text, font);
        g.DrawString(text, font, brush, (size - measured.Width) / 2f, (size - measured.Height) / 2f + 0.5f);

        var hIcon = bitmap.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone(); // 핸들 수명과 분리된 복사본
        }
        finally
        {
            NativeDestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroyIcon(IntPtr handle);
}
