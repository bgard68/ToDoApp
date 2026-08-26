using FluentAssertions;
using TodoApp.WebApi;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// The guard on the rate limiter's one unstated assumption: that a single instance serves traffic.
///
/// Worth testing rather than trusting, because the condition it watches for produces no symptom.
/// A second instance does not error, retry, or log — it enforces the same budget again,
/// independently, and the configured numbers quietly stop being the numbers in effect.
/// </summary>
public class ScaleOutCheckTests
{
    private static (string? Warning, int Count) Inspect(string? sku)
    {
        string? captured = null;
        var count = 0;
        ScaleOutCheck.Inspect(sku, message => { captured = message; count++; });
        return (captured, count);
    }

    [Theory]
    [InlineData("Free")]
    [InlineData("Shared")]
    public void A_single_instance_tier_is_what_the_limiter_assumes_and_says_nothing(string sku)
    {
        var (warning, count) = Inspect(sku);

        warning.Should().BeNull();
        count.Should().Be(0);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("FREE")]
    [InlineData("  Free  ")]
    public void The_tier_is_matched_regardless_of_how_the_platform_cases_it(string sku)
        => Inspect(sku).Warning.Should().BeNull();

    [Theory]
    [InlineData("Basic")]
    [InlineData("Standard")]
    [InlineData("Premium")]
    [InlineData("PremiumV3")]
    [InlineData("Isolated")]
    public void A_tier_that_can_scale_out_is_warned_about(string sku)
    {
        var (warning, count) = Inspect(sku);

        count.Should().Be(1);
        warning.Should().Contain(sku);
        // A warning that does not say what to do about it is just noise in a log nobody reads.
        warning.Should().Contain("multiplied by the instance count");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Off_App_Service_there_is_no_platform_claim_to_check(string? sku)
    {
        // Local runs, Docker, and the systemd/nginx deployment do not set WEBSITE_SKU. Warning
        // there would train an operator to ignore the message, which costs more than it is worth.
        var (warning, count) = Inspect(sku);

        warning.Should().BeNull();
        count.Should().Be(0);
    }

    [Fact]
    public void An_unrecognised_tier_warns_rather_than_assuming_it_is_safe()
    {
        // A tier this code has never heard of is likelier to be new and scalable than new and
        // single-instance, and the wrong guess is the silent one.
        Inspect("SomeTierInventedNextYear").Warning.Should().NotBeNull();
    }

    [Fact]
    public void A_hostile_tier_value_cannot_forge_a_log_entry()
    {
        // WEBSITE_SKU comes from the environment, and an environment variable is not automatically
        // trustworthy text — the same log-forging reasoning LogSanitizer already exists for.
        var warning = Inspect("Standard\r\nWARN: fake entry").Warning;

        warning.Should().NotBeNull();
        warning.Should().NotContain("\n").And.NotContain("\r");
    }

    [Fact]
    public void A_missing_sink_is_a_programming_error_not_a_silent_pass()
    {
        var inspect = () => ScaleOutCheck.Inspect("Standard", null!);

        inspect.Should().Throw<ArgumentNullException>();
    }
}
