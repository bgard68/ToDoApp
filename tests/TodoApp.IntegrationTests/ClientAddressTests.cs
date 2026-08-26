using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using TodoApp.WebApi;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Which caller a request is attributed to — the whole correctness of rate limiting. Get the
/// partition key wrong and the limiter either caps every user together or caps nobody at all.
///
/// None of this could be tested while the logic was a local function inside <c>Program.cs</c>, and
/// that is exactly how an IPv6 client came to be exempt from throttling: the endpoint test asserting
/// a 429 passed the whole time, because it drove the limiter over IPv4.
/// </summary>
public class ClientAddressTests
{
    private static HttpContext Request(string? remoteIp, params string[] forwardedFor)
    {
        var context = new DefaultHttpContext();
        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        if (forwardedFor.Length > 0)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }

    [Fact]
    public void The_connection_address_identifies_the_caller_by_default()
        => ClientAddress.Resolve(Request("203.0.113.7"), trustForwardedFor: false)
            .Should().Be("203.0.113.7");

    [Fact]
    public void A_forwarded_header_is_ignored_unless_a_proxy_is_trusted()
    {
        // Anyone can send this header. Believed with no proxy in front, a caller could vary it per
        // request and give itself an unlimited number of partitions.
        ClientAddress.Resolve(Request("203.0.113.7", "198.51.100.9"), trustForwardedFor: false)
            .Should().Be("203.0.113.7");
    }

    [Fact]
    public void The_last_hop_is_the_client_because_the_proxy_appended_it()
    {
        ClientAddress.Resolve(Request("10.0.0.1", "198.51.100.9"), trustForwardedFor: true)
            .Should().Be("198.51.100.9");
    }

    [Fact]
    public void A_caller_cannot_choose_its_own_partition_by_forging_the_header()
    {
        // The caller sends "9.9.9.9"; the proxy appends what it actually saw. Reading the left of
        // the chain would return the attacker's choice.
        ClientAddress.Resolve(Request("10.0.0.1", "9.9.9.9, 198.51.100.9"), trustForwardedFor: true)
            .Should().Be("198.51.100.9");
    }

    [Fact]
    public void Two_callers_forging_different_headers_still_share_one_partition()
    {
        var first = ClientAddress.Resolve(Request("10.0.0.1", "1.1.1.1, 198.51.100.9"), trustForwardedFor: true);
        var second = ClientAddress.Resolve(Request("10.0.0.1", "2.2.2.2, 198.51.100.9"), trustForwardedFor: true);

        first.Should().Be(second, "the key must depend on where a request came from, not what it claimed");
    }

    [Fact]
    public void The_port_App_Service_appends_is_not_part_of_an_IPv4_identity()
    {
        ClientAddress.Resolve(Request("10.0.0.1", "203.0.113.7:51514"), trustForwardedFor: true)
            .Should().Be("203.0.113.7");
    }

    [Fact]
    public void The_port_App_Service_appends_is_not_part_of_an_IPv6_identity()
    {
        // The regression. Trimming brackets and slicing on the last colon left "2001:db8::1]:51514"
        // as the key: a different partition on every connection, since the source port is ephemeral.
        // An IPv6 caller was therefore never rate limited at all.
        var first = ClientAddress.Resolve(Request("10.0.0.1", "[2001:db8::1]:51514"), trustForwardedFor: true);
        var second = ClientAddress.Resolve(Request("10.0.0.1", "[2001:db8::1]:60122"), trustForwardedFor: true);

        first.Should().Be("2001:db8::1");
        first.Should().Be(second, "the same client on two connections must land in one partition");
    }

    [Fact]
    public void A_bare_IPv6_address_survives_its_own_colons()
    {
        ClientAddress.Resolve(Request("10.0.0.1", "2001:db8::1"), trustForwardedFor: true)
            .Should().Be("2001:db8::1");
    }

    [Fact]
    public void An_IPv6_client_behind_a_forged_header_is_still_identified()
    {
        ClientAddress.Resolve(Request("10.0.0.1", "9.9.9.9, [2001:db8::1]:51514"), trustForwardedFor: true)
            .Should().Be("2001:db8::1");
    }

    [Fact]
    public void A_chain_split_across_repeated_headers_is_read_the_same_way()
    {
        ClientAddress.Resolve(Request("10.0.0.1", "9.9.9.9", "198.51.100.9"), trustForwardedFor: true)
            .Should().Be("198.51.100.9");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void An_empty_forwarded_header_falls_back_to_the_connection(string headerValue)
        => ClientAddress.Resolve(Request("203.0.113.7", headerValue), trustForwardedFor: true)
            .Should().Be("203.0.113.7");

    [Theory]
    [InlineData("unknown")]
    [InlineData("_hidden")]
    [InlineData("not-an-address")]
    public void An_entry_that_is_not_an_address_is_not_used_as_an_identity(string entry)
    {
        // Used as a key, every caller sending the same placeholder would share one budget under a
        // name that reads like a specific client.
        ClientAddress.Resolve(Request("203.0.113.7", entry), trustForwardedFor: true)
            .Should().Be("203.0.113.7");
    }

    [Fact]
    public void Callers_with_no_determinable_address_share_one_budget()
    {
        // Failing closed: unattributable traffic is throttled together rather than exempted.
        ClientAddress.Resolve(Request(null), trustForwardedFor: false).Should().Be(ClientAddress.Unknown);
        ClientAddress.Resolve(Request(null), trustForwardedFor: true).Should().Be(ClientAddress.Unknown);
    }

    [Fact]
    public void A_null_context_is_a_programming_error_not_a_silent_pass()
    {
        var resolve = () => ClientAddress.Resolve(null!, trustForwardedFor: false);

        resolve.Should().Throw<ArgumentNullException>();
    }
}
