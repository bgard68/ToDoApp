using System.Net;

namespace TodoApp.WebApi;

/// <summary>
/// Works out which caller a request belongs to, so rate limiting partitions per client rather than
/// per process.
///
/// This lives in its own class rather than as a local function in <c>Program.cs</c> because it has
/// rules worth testing. While it was a closure there nothing could reach it, and an IPv6 client
/// silently escaped the limiter for months — the string handling below is the whole correctness of
/// throttling, and it was the one part with no test.
/// </summary>
public static class ClientAddress
{
    /// <summary>Partition used when no address can be determined, so those callers share a budget.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Resolves the partition key for <paramref name="http"/>.
    ///
    /// <c>X-Forwarded-For</c> is only read when <paramref name="trustForwardedFor"/> says a proxy we
    /// control is in front, and only its <b>last</b> entry is believed. A proxy appends the address
    /// it observed rather than replacing what arrived, so everything to the left of that entry is
    /// whatever the caller chose to send: reading it would let a client vary the header per request
    /// and mint a fresh partition each time, opting out of throttling entirely.
    /// </summary>
    public static string Resolve(HttpContext http, bool trustForwardedFor)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (trustForwardedFor)
        {
            var forwarded = LastForwardedHop(http.Request.Headers["X-Forwarded-For"]);
            if (forwarded is not null)
            {
                return forwarded;
            }
        }

        return http.Connection.RemoteIpAddress?.ToString() ?? Unknown;
    }

    /// <summary>
    /// The final entry of the chain, reduced to a bare address. Returns null when nothing usable is
    /// present so the caller can fall back to the connection address.
    /// </summary>
    private static string? LastForwardedHop(IEnumerable<string?> headerValues)
    {
        string? last = null;
        foreach (var value in headerValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // A chain may arrive as one comma-separated header or as several repeated headers; the
            // two are equivalent, so they are read as a single sequence.
            var hops = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hops.Length > 0)
            {
                last = hops[^1];
            }
        }

        return last is null ? null : Normalize(last);
    }

    /// <summary>
    /// Reduces one entry to a bare address, dropping the <c>:port</c> App Service appends.
    ///
    /// The port is ephemeral — a new one per connection — so leaving it in would partition per
    /// connection instead of per caller and defeat the limiter just as thoroughly as reading a
    /// forged entry would. Parsing rather than slicing on the last colon is what makes this correct
    /// for IPv6: <c>[2001:db8::1]:51514</c> has colons throughout, and hand-rolled trimming left the
    /// port attached.
    /// </summary>
    private static string? Normalize(string candidate)
    {
        // Tried first so a bare IPv6 address is not mistaken for a host:port pair on account of its
        // own colons.
        if (IPAddress.TryParse(candidate, out var address))
        {
            return address.ToString();
        }

        // Covers "203.0.113.7:51514" and the bracketed "[2001:db8::1]:51514" App Service writes.
        // Anything that parses as neither is not an identity — "unknown" and the obfuscated forms
        // the standard allows would otherwise become a partition key shared by every caller sending
        // them, under a name that reads like a specific client.
        return IPEndPoint.TryParse(candidate, out var endpoint) ? endpoint.Address.ToString() : null;
    }
}
