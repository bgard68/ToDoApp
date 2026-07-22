using System.Reflection;
using Dapper;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Creates the database schema by running an embedded, idempotent DDL script for the active
/// provider. Replaces EF Core's <c>EnsureCreated</c>; safe to run on every startup and from the
/// test harness. See the README for the DbUp-based migration path recommended for production.
/// </summary>
public interface ISchemaInitializer
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SchemaInitializer : ISchemaInitializer
{
    private readonly IDbConnectionContext _context;
    private readonly IDbConnectionFactory _factory;

    public SchemaInitializer(IDbConnectionContext context, IDbConnectionFactory factory)
    {
        _context = context;
        _factory = factory;
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        var script = LoadScript(_factory.Provider);
        var connection = await _context.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(script, cancellationToken: cancellationToken));
    }

    private static string LoadScript(DbProvider provider)
    {
        var fileName = provider == DbProvider.SqlServer ? "Schema.SqlServer.sql" : "Schema.Sqlite.sql";
        var resourceName = $"TodoApp.Infrastructure.Persistence.Schema.{fileName}";

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema script '{resourceName}' was not found. " +
                "Ensure the .sql files are included as EmbeddedResource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
