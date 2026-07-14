namespace TodoApp.Application.Common.Models;

/// <summary>Verified identity extracted from a Google ID token.</summary>
public record GoogleUserInfo(string Subject, string Email, bool EmailVerified, string? Name);
