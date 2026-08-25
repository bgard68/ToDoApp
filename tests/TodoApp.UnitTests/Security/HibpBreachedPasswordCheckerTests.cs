using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TodoApp.Infrastructure.Authentication;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// The Pwned Passwords range lookup. No test touches the network: a stub handler stands in for
/// the API so both the match logic and the fail-open behavior are exercised deterministically.
/// </summary>
public class HibpBreachedPasswordCheckerTests
{
    private const string Password = "correct horse battery staple";

    private static (string prefix, string suffix) Sha1Of(string password)
    {
        var hex = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        return (hex[..5], hex[5..]);
    }

    private static HibpBreachedPasswordChecker Create(
        StubHandler handler, bool enabled = true, int minimumOccurrences = 1)
    {
        var options = Options.Create(new PasswordBreachCheckOptions
        {
            Enabled = enabled,
            TimeoutSeconds = 5,
            MinimumOccurrences = minimumOccurrences
        });

        return new HibpBreachedPasswordChecker(
            new StubHttpClientFactory(handler),
            options,
            NullLogger<HibpBreachedPasswordChecker>.Instance);
    }

    [Fact]
    public async Task Disabled_ChecksNothing()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));

        var breached = await Create(handler, enabled: false)
            .IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnEmptyPassword_ChecksNothing()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));

        var breached = await Create(handler).IsBreachedAsync("", CancellationToken.None);

        breached.Should().BeFalse();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SendsOnlyTheFirstFiveHexCharactersOfTheHash()
    {
        var (prefix, _) = Sha1Of(Password);
        string? requested = null;
        var handler = new StubHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return Respond(HttpStatusCode.OK, "0000000000000000000000000000000000000:1");
        });

        await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        // k-anonymity: the suffix must never leave the process.
        requested.Should().EndWith($"range/{prefix}");
        var (_, suffix) = Sha1Of(Password);
        requested.Should().NotContain(suffix);
    }

    [Fact]
    public async Task AMatchingSuffix_IsBreached()
    {
        var (_, suffix) = Sha1Of(Password);
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK,
            $"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:9\n{suffix}:42"));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeTrue();
    }

    [Fact]
    public async Task AMatchingSuffixIsCaseInsensitive()
    {
        var (_, suffix) = Sha1Of(Password);
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, $"{suffix.ToLowerInvariant()}:3"));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeTrue();
    }

    [Fact]
    public async Task NoMatchingSuffix_IsClean()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:9\nBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB:1"));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse();
    }

    [Fact]
    public async Task AnEmptyRangeResponse_IsClean()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, ""));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse();
    }

    [Theory]
    [InlineData("no-colon-at-all")]
    [InlineData(":leading-colon")]
    public async Task MalformedLinesAreSkipped(string line)
    {
        var (_, suffix) = Sha1Of(Password);
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, $"{line}\n{suffix}:5"));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeTrue(); // the good line after the junk still matches
    }

    [Fact]
    public async Task AnUnparseableCountIsTreatedAsASingleSighting()
    {
        var (_, suffix) = Sha1Of(Password);
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, $"{suffix}:not-a-number"));

        var breached = await Create(handler, minimumOccurrences: 2)
            .IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse(); // 1 sighting < the threshold of 2
    }

    [Fact]
    public async Task ACountBelowTheThresholdIsAllowed()
    {
        var (_, suffix) = Sha1Of(Password);
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, $"{suffix}:3"));

        var breached = await Create(handler, minimumOccurrences: 10)
            .IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse();
    }

    [Fact]
    public async Task ANonSuccessStatus_FailsOpen()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, ""));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse();
    }

    [Fact]
    public async Task ATransportFailure_FailsOpen()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("dns is having a day"));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse();
    }

    [Fact]
    public async Task ATimeout_FailsOpen()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));

        var breached = await Create(handler).IsBreachedAsync(Password, CancellationToken.None);

        breached.Should().BeFalse();
    }

    [Fact]
    public async Task CallerCancellation_FailsOpen()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(_ => throw new OperationCanceledException());

        var breached = await Create(handler).IsBreachedAsync(Password, cts.Token);

        breached.Should().BeFalse();
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;

        public StubHttpClientFactory(StubHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("https://api.pwnedpasswords.com/") };
    }
}
