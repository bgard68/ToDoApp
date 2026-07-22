namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// The relational backend the app is running against. Chosen from configuration
/// (<c>Database:Provider</c>) and used to pick provider-specific SQL where the two dialects
/// diverge (identity retrieval, DDL types, unique-violation error codes).
/// </summary>
public enum DbProvider
{
    Sqlite,
    SqlServer
}
