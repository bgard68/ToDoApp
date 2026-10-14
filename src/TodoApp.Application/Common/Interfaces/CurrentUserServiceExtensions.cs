using TodoApp.Application.Common.Exceptions;

namespace TodoApp.Application.Common.Interfaces;

/// <summary>
/// Convenience over <see cref="ICurrentUserService"/> for the common case: a handler that cannot
/// do anything useful without an authenticated user.
/// </summary>
public static class CurrentUserServiceExtensions
{
    /// <summary>
    /// The authenticated user's id, or <see cref="UnauthorizedException"/> (HTTP 401) if the
    /// request has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every handler that touches user-owned data opened with the same line —
    /// <c>_currentUser.RequireUserId()</c> — thirteen times over.
    /// Identical by convention rather than by construction, which is the kind of duplication that
    /// stays right only as long as nobody edits one copy: a handler that forgot the <c>throw</c>
    /// would read a nullable as if it were set, and one that threw something else would answer a
    /// missing identity with the wrong status code.
    /// </para>
    /// <para>
    /// Deliberately an extension rather than a member on the interface, so the rule lives in one
    /// place without every implementation — including the test fakes — having to restate it.
    /// </para>
    /// </remarks>
    public static int RequireUserId(this ICurrentUserService currentUser)
        => currentUser.UserId ?? throw new UnauthorizedException();
}
