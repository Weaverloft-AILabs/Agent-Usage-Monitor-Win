using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeUsageMonitor.Core.Validation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeUsageMonitor.App.ViewModels;

/// <summary>문의(분류+이메일+제목+내용)를 Dooray incoming hook으로 전송.</summary>
public partial class InquiryViewModel : ObservableObject
{
    private const string HookUrl =
        "https://weaverloft.dooray.com/services/2873860291925818807/4372665566139854582/h1cEI4wkThW5ahZh88t0Vg";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>0=버그제보, 1=개선요청.</summary>
    [ObservableProperty]
    private int _categoryIndex;

    /// <summary>답변 회신용 이메일 (필수).</summary>
    [ObservableProperty]
    private string _email = "";

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isEmailInvalid;

    [ObservableProperty]
    private string _emailErrorText = "";

    /// <summary>전송 시 자동 첨부되는 정보 안내 (창 하단 각주).</summary>
    public string MetaText { get; } = $"v{AppVersion()} 및 OS 정보가 함께 전송됩니다";

    public bool IsBugCategory
    {
        get => CategoryIndex == 0;
        set
        {
            if (value)
            {
                CategoryIndex = 0;
            }
        }
    }

    public bool IsImprovementCategory
    {
        get => CategoryIndex == 1;
        set
        {
            if (value)
            {
                CategoryIndex = 1;
            }
        }
    }

    partial void OnCategoryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsBugCategory));
        OnPropertyChanged(nameof(IsImprovementCategory));
    }

    partial void OnEmailChanged(string value)
    {
        // 오류 표시 중 사용자가 고쳐서 유효해지면 즉시 해제
        if (IsEmailInvalid && EmailValidator.IsValid(value))
        {
            IsEmailInvalid = false;
            EmailErrorText = "";
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var email = Email.Trim();
        if (!EmailValidator.IsValid(email))
        {
            EmailErrorText = string.IsNullOrWhiteSpace(email)
                ? "답변을 받을 이메일 주소를 입력해 주세요"
                : "이메일 주소 형식이 올바르지 않습니다";
            IsEmailInvalid = true;
            StatusText = "";
            return;
        }

        IsEmailInvalid = false;
        EmailErrorText = "";

        if (string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Content))
        {
            IsStatusError = true;
            StatusText = "제목이나 내용을 입력해 주세요";
            return;
        }

        IsStatusError = false;
        StatusText = "전송 중...";
        try
        {
            var version = AppVersion();
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
                        text = $"보낸 사람: {email}\n\n{Content.Trim()}\n\n— v{version} · {Environment.OSVersion.VersionString}",
                        color = CategoryIndex == 0 ? "red" : "green",
                    },
                },
            });

            using var response = await Http.PostAsync(
                HookUrl, new StringContent(payload, Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                IsStatusError = false;
                StatusText = "전송되었습니다. 감사합니다!";
                Title = "";
                Content = "";
                // 이메일은 다음 문의를 위해 유지
            }
            else
            {
                IsStatusError = true;
                StatusText = $"전송 실패 (HTTP {(int)response.StatusCode}) — 잠시 후 다시 시도해 주세요";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsStatusError = true;
            StatusText = "전송 실패 — 네트워크 상태를 확인해 주세요";
        }
    }

    private static string AppVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
}
