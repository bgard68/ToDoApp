namespace TodoApp.Application.Common.Interfaces;

/// <summary>
/// Checks a candidate password against a public corpus of passwords exposed in known breaches
/// (review finding L9).
/// </summary>
/// <remarks>
/// Length and composition rules stop nothing that matters: "Password1" satisfies every rule the
/// register validator enforces and appears in breach corpora millions of times. Rejecting known-
/// breached passwords is the single highest-value password control there is.
/// </remarks>
public interface IBreachedPasswordChecker
{
    /// <summary>
    /// True when the password is known to appear in a breach corpus.
    /// Implementations MUST fail open — a checker that is down must not block registration.
    /// </summary>
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken);
}
