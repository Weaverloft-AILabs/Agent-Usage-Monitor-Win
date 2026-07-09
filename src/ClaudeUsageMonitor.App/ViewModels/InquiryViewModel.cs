using System.Net.Http;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeUsageMonitor.App.ViewModels;

/// <summary>문의(제목+내용)를 Dooray incoming hook으로 전송.</summary>
public partial class InquiryViewModel : ObservableObject
{
    private const string HookUrl =
        "https://weaverloft.dooray.com/services/2873860291925818807/4372665566139854582/h1cEI4wkThW5ahZh88t0Vg";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>0=버그제보, 1=개선요청.</summary>
    [ObservableProperty]
    private int _categoryIndex;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private string _statusText = "";

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Content))
        {
            StatusText = "제목이나 내용을 입력해 주세요";
            return;
        }

        StatusText = "전송 중...";
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "?";
            var category = CategoryIndex == 0 ? "버그제보" : "개선요청";

            // Dooray incoming hook 포맷 (참고: https://ssonzm.tistory.com/143)
            var payload = JsonSerializer.Serialize(new
            {
                botName = "Agent Usage Monitor 문의",
                botIconImage = "https://static.dooray.com/static_images/dooray-bot.png",
                text = $"[{category}] {Title.Trim()}",
                attachments = new[]
                {
                    new
                    {
                        title = Title.Trim(),
                        text = $"{Content.Trim()}\n\n— v{version} · {Environment.OSVersion.VersionString}",
                        color = CategoryIndex == 0 ? "red" : "green",
                    },
                },
            });

            using var response = await Http.PostAsync(
                HookUrl, new StringContent(payload, Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                StatusText = "전송되었습니다. 감사합니다!";
                Title = "";
                Content = "";
            }
            else
            {
                StatusText = $"전송 실패 (HTTP {(int)response.StatusCode}) — 잠시 후 다시 시도해 주세요";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText = "전송 실패 — 네트워크 상태를 확인해 주세요";
        }
    }
}
