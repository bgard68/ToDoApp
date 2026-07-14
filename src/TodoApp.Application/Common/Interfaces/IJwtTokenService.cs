using TodoApp.Application.Common.Models;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Common.Interfaces;

public interface IJwtTokenService
{
    /// <summary>Creates a signed, short-lived access token carrying the user's security stamp.</summary>
    AccessToken CreateAccessToken(User user);

    /// <summary>Generates a new random refresh token (raw value + hash to persist).</summary>
    RefreshTokenResult CreateRefreshToken();

    /// <summary>Hashes a raw refresh token so an incoming value can be matched against the store.</summary>
    string HashToken(string rawToken);
}
