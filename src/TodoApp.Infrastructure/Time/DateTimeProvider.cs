using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Infrastructure.Time;

/// <summary>The production clock: returns the real current UTC time.</summary>
public sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
