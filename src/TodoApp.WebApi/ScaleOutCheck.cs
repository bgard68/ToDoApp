using TodoApp.Application.Common.Logging;

namespace TodoApp.WebApi;

/// <summary>
/// Says so when the deployment outgrows the assumption the rate limiter is built on.
///
/// Both limiters — the <c>auth</c> policy and the global backstop — keep their counters in this
/// process's memory. That is correct while one instance serves traffic, and wrong the moment a
/// second one does: each keeps its own counters and enforces the configured budget separately, so
/// two instances allow twice the limit and four allow four times.
///
/// Nothing about that surfaces on its own. No error, no rejected request, no log line — the numbers
/// in <c>appsettings.json</c> just stop being the numbers in effect.
/// </summary>
public static class ScaleOutCheck
{
    /// <summary>
    /// App Service tiers that cannot run more than one instance, so in-memory counters are the whole
    /// picture. Every other tier can scale out, whether or not it currently has.
    /// </summary>
    private static readonly string[] SingleInstanceTiers = ["Free", "Shared"];

    /// <summary>
    /// Warns when <paramref name="websiteSku"/> names a tier able to run several instances.
    ///
    /// The tier is the only available signal: an instance cannot see how many siblings it has, so
    /// this reports "this deployment can scale out" rather than "it has". That is the more useful
    /// warning regardless — on an autoscaling plan the second instance can arrive at any moment, and
    /// the limits are wrong from that point on with nothing to mark the transition.
    /// </summary>
    /// <param name="websiteSku">
    /// The <c>WEBSITE_SKU</c> environment variable. Absent off App Service — local runs, Docker,
    /// the systemd/nginx deployment — where there is no platform claim to check and this stays quiet.
    /// </param>
    /// <returns>The warning that was logged, or null when the tier is single-instance.</returns>
    public static string? Inspect(string? websiteSku, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);

        if (string.IsNullOrWhiteSpace(websiteSku) ||
            SingleInstanceTiers.Contains(websiteSku.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var message =
            $"Rate limiting keeps its counters in this instance's memory, but WEBSITE_SKU is " +
            $"'{LogSanitizer.Sanitize(websiteSku)}', a tier that can run more than one instance. Each " +
            "instance would enforce the configured budgets separately, so every limit is multiplied " +
            "by the instance count. Move to a shared counter store before scaling out, or divide the " +
            "budgets by the instance count and accept the imprecision.";

        warn(message);
        return message;
    }
}
