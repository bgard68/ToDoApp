using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Infrastructure.Authentication;

/// <summary>
/// Reads the authenticated principal from the current HTTP request. Inbound claim
/// mapping is disabled (see Program.cs), so raw JWT claim names are used ("sub", "role").
/// </summary>
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public int? UserId =>
        int.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public string? Email => Principal?.FindFirstValue("email");

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public bool IsInRole(string role) => Principal?.IsInRole(role) ?? false;
}
