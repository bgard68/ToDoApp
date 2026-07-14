using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TodoApp.Infrastructure.Persistence.Converters;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as its UTC tick count (a <see cref="long"/>), so
/// SQLite (which has no native DateTimeOffset type) can ORDER BY and compare it correctly.
/// <para>
/// The built-in <c>DateTimeOffsetToBinaryConverter</c> is deliberately NOT used: it encodes
/// the *local* ticks plus the offset, so values with different offsets do not sort in
/// chronological order. Using <c>UtcTicks</c> guarantees correct ordering regardless of
/// offset. This app writes UTC everywhere (via IDateTimeProvider), so reconstructing with a
/// zero offset on read is lossless; ticks preserve full (100 ns) precision.
/// </para>
/// <para>
/// A parameterless constructor is required so EF Core can instantiate this converter when it
/// is registered via <c>HaveConversion&lt;T&gt;()</c> in ConfigureConventions.
/// </para>
/// </summary>
public sealed class DateTimeOffsetToUtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToUtcTicksConverter()
        : base(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero))
    {
    }
}
