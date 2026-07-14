namespace TodoApp.Infrastructure.Authentication;

public class GoogleAuthSettings
{
    public const string SectionName = "Authentication:Google";

    /// <summary>OAuth 2.0 Web client ID from the Google Cloud console. Also the token audience.</summary>
    public string ClientId { get; set; } = string.Empty;
}
