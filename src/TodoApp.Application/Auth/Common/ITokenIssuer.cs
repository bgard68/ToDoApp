using TodoApp.Application.Auth.Dtos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Common;

/// <summary>
/// Issues an access token plus a new refresh token for a user, and stages the refresh token for
/// persistence. The caller owns the transaction boundary and must call SaveChanges.
/// </summary>
/// <remarks>
/// An injected service rather than the static helper this replaced. The static version took its
/// collaborators — the token service and the database context — as parameters on every call,
/// which made them invisible to the container, unsubstitutable in a test, and repeated at each of
/// the three call sites. Here they are constructor dependencies like everywhere else in the
/// layer, and a handler asks for the capability instead of threading the plumbing through it.
/// </remarks>
public interface ITokenIssuer
{
    /// <summary>
    /// Issues the access/refresh token pair for the user as of <paramref name="now"/> and
    /// stages the refresh token for persistence.
    /// </summary>
    /// <param name="user">The authenticated user to issue for.</param>
    /// <param name="now">
    /// The timestamp to record. Passed in rather than read from a clock here, so a caller that
    /// has already captured "now" for other writes in the same request stamps all of them
    /// identically.
    /// </param>
    AuthResponse Issue(User user, DateTimeOffset now);
}
