using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TodoApp.Infrastructure.Persistence;

namespace TodoApp.UnitTests.TestSupport;

/// <summary>
/// A real EF Core context backed by an in-memory SQLite database (kept alive by an open
/// connection). Exercises actual query translation, unlike the EF in-memory provider.
/// Use <see cref="NewContext"/> to read with a fresh context and avoid stale tracking.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Context = NewContext();
        Context.Database.EnsureCreated();
    }

    public ApplicationDbContext Context { get; }

    public ApplicationDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new ApplicationDbContext(options);
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
