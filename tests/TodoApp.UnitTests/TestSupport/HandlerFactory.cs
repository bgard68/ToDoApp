using TodoApp.Application.Auth.Commands.GoogleSignIn;
using TodoApp.Application.Auth.Commands.Login;
using TodoApp.Application.Auth.Commands.Register;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Common.Interfaces;

namespace TodoApp.UnitTests.TestSupport;

/// <summary>
/// Builds the three auth handlers with a token issuer bound to the same context the handler got.
/// </summary>
/// <remarks>
/// The issuer stages a refresh token on the context, so handler and issuer must share one
/// instance — and these tests routinely pass a freshly created context inline
/// (<c>db.NewContext()</c>). Constructing both here keeps that invariant in one place instead of
/// making every test hoist a local and remember to pass it twice.
/// </remarks>
internal static class HandlerFactory
{
    public static LoginCommandHandler Login(
        IApplicationDbContext db,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime)
        => new(db, hasher, jwt, dateTime, new TokenIssuer(jwt, db));

    public static RegisterCommandHandler Register(
        IApplicationDbContext db,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime,
        IBreachedPasswordChecker breachChecker)
        => new(db, hasher, jwt, dateTime, breachChecker, new TokenIssuer(jwt, db));

    public static GoogleSignInCommandHandler GoogleSignIn(
        IApplicationDbContext db,
        IGoogleTokenValidator google,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime)
        => new(db, google, jwt, dateTime, new TokenIssuer(jwt, db));
}
