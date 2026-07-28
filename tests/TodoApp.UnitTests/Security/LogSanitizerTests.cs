using FluentAssertions;
using TodoApp.Application.Common.Logging;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// Review finding M1 — log forging (CWE-117). This fix previously existed on <c>main</c> only;
/// the sanitiser is now shared code with this test running on every branch, so the two cannot
/// drift apart again.
/// </summary>
public class LogSanitizerTests
{
    private const char Esc = (char)27;
    private const char Bel = (char)7;
    private const char Nul = (char)0;

    [Theory]
    [InlineData("/api/todos", "/api/todos")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Leaves_ordinary_values_untouched(string? input, string expected)
    {
        LogSanitizer.Sanitize(input).Should().Be(expected);
    }

    [Fact]
    public void Strips_carriage_returns_and_line_feeds()
    {
        // The classic forgery: make one request path look like two log lines.
        var forged = "/api/todos\r\nWARN: admin password reset by system";

        var result = LogSanitizer.Sanitize(forged);

        result.Should().NotContain("\r").And.NotContain("\n");
        result.Should().Be("/api/todosWARN: admin password reset by system");
    }

    [Fact]
    public void Strips_other_control_characters_including_terminal_escapes()
    {
        // ESC opens an ANSI sequence that can rewrite what an operator sees in a terminal;
        // BEL and NUL are equally unwelcome in a log line.
        var input = "/api/" + Esc + "[31mtodos" + Bel + Nul;

        var result = LogSanitizer.Sanitize(input);

        result.Should().Be("/api/[31mtodos");
        result.Should().NotContain(Esc.ToString()).And.NotContain(Bel.ToString());
    }

    [Fact]
    public void Strips_control_characters_from_the_middle_and_the_end()
    {
        LogSanitizer.Sanitize("a" + Nul + "b").Should().Be("ab");
        LogSanitizer.Sanitize("trailing" + Nul).Should().Be("trailing");
    }

    [Fact]
    public void Preserves_non_ascii_text()
    {
        LogSanitizer.Sanitize("/api/tâches/日本語").Should().Be("/api/tâches/日本語");
    }
}
