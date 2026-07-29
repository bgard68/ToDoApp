using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Infrastructure.Authentication;

/// <summary>
/// Have I Been Pwned "Pwned Passwords" checker, using the k-anonymity range API
/// (review finding L9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The password never leaves this process.</strong> Only the first five hex characters of
/// its SHA-1 are sent; the API returns every suffix sharing that prefix (~800 hashes) and the
/// match is done locally. The service cannot tell which password — or even which of its own
/// entries — was being asked about.
/// </para>
/// <para>
/// SHA-1 here is not a security choice: it is the corpus's index format. It is never stored.
/// </para>
/// <para>
/// <strong>Fails open by design.</strong> If the API is slow, rate-limited, or unreachable, this
/// returns <c>false</c> and registration proceeds. An outage at a third party must not become an
/// outage here — the check is a meaningful improvement on average, not a control worth trading
/// availability for. The endpoint is free and needs no API key, which is what makes it viable on
/// the Free tier.
/// </para>
/// </remarks>
public class HibpBreachedPasswordChecker : IBreachedPasswordChecker
{
    public const string HttpClientName = "hibp";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HibpBreachedPasswordChecker> _logger;
    private readonly PasswordBreachCheckOptions _options;

    public HibpBreachedPasswordChecker(
        IHttpClientFactory httpClientFactory,
        IOptions<PasswordBreachCheckOptions> options,
        ILogger<HibpBreachedPasswordChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var prefix = sha1[..5];
        var suffix = sha1[5..];

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync($"range/{prefix}", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Pwned Passwords lookup returned {Status}; allowing the password.",
                    (int)response.StatusCode);
                return false;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                // Each line is "SUFFIX:COUNT".
                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                if (!suffix.Equals(line[..separator], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var seen = int.TryParse(line[(separator + 1)..].Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 1;

                return seen >= _options.MinimumOccurrences;
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Fail open — see the remarks above.
            _logger.LogWarning(ex, "Pwned Passwords lookup failed; allowing the password.");
            return false;
        }
    }
}

/// <summary>Settings for <see cref="HibpBreachedPasswordChecker"/> (config section "PasswordBreachCheck").</summary>
public class PasswordBreachCheckOptions
{
    public const string SectionName = "PasswordBreachCheck";

    /// <summary>Off in tests and local development; on in deployed environments.</summary>
    public bool Enabled { get; set; }

    /// <summary>Give up (and allow) after this long, so registration never hangs on a third party.</summary>
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// How many breach appearances count as "breached". 1 is the strict reading; a small threshold
    /// avoids rejecting a strong passphrase that happens to appear once in a scraped corpus.
    /// </summary>
    public int MinimumOccurrences { get; set; } = 1;
}
