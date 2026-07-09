namespace ClaudeUsageMonitor.Core.Validation;

/// <summary>문의 회신용 이메일 주소의 형식 검증 (RFC 전체가 아닌 실용 수준).</summary>
public static class EmailValidator
{
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var s = email.Trim();
        if (s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@') || at == s.Length - 1)
        {
            return false;
        }

        var domain = s[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }
}
