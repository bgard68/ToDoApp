using System.Data;
using Dapper;

namespace TodoApp.Infrastructure.Persistence.Dapper;

/// <summary>
/// Stores every <see cref="DateTimeOffset"/> as a UTC-tick <see cref="long"/> and reconstructs
/// it with a zero offset. Mirrors the value produced by the previous EF Core value converter,
/// so SQLite can order/compare timestamps and both providers keep an identical on-disk shape.
/// Registering the handler for <see cref="DateTimeOffset"/> also covers the nullable variant.
/// </summary>
public sealed class DateTimeOffsetTicksHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.Int64;
        parameter.Value = value.UtcTicks;
    }

    public override DateTimeOffset Parse(object value)
        => new(Convert.ToInt64(value), TimeSpan.Zero);
}
