using System.Data;
using Dapper;

namespace TodoApp.Infrastructure.Persistence.Dapper;

/// <summary>
/// Round-trips <see cref="Guid"/> values as text for cross-provider consistency. Microsoft.Data.Sqlite
/// stores the <c>ConcurrencyToken</c> column as TEXT and returns it as a string; SQL Server's
/// <c>uniqueidentifier</c> returns a native <see cref="Guid"/>. Parsing from either keeps the
/// optimistic-concurrency comparison exact on both backends.
/// </summary>
public sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString();
    }

    public override Guid Parse(object value)
        => value is Guid guid ? guid : Guid.Parse((string)value);
}
