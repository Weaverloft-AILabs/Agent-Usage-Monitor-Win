using ClaudeUsageMonitor.Core.Validation;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class EmailValidatorTests
{
    [Theory]
    [InlineData("a@b.co")]
    [InlineData("user.name+tag@sub.domain.org")]
    [InlineData("noia1223@gmail.com")]
    [InlineData(" padded@example.io ")] // 앞뒤 공백은 Trim 후 판정
    public void Valid_Addresses(string email)
        => Assert.True(EmailValidator.IsValid(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@no-local.com")]
    [InlineData("no-domain@")]
    [InlineData("no-dot@domain")]
    [InlineData("trailing-dot@domain.")]
    [InlineData("dot-first@.com")]
    [InlineData("two@@ats.com")]
    [InlineData("inner space@x.co")]
    [InlineData("tab\tinside@x.co")]
    [InlineData("newline\ninside@x.co")]
    public void Invalid_Addresses(string? email)
        => Assert.False(EmailValidator.IsValid(email));
}
