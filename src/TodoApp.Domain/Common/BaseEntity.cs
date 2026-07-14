namespace TodoApp.Domain.Common;

/// <summary>
/// Base type for all persistent entities. Identity and audit fields live here.
/// Timestamps are supplied by the caller (via IDateTimeProvider) rather than read from
/// the ambient system clock, keeping the domain deterministic and testable.
/// </summary>
public abstract class BaseEntity
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
