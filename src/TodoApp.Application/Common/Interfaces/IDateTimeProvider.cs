namespace TodoApp.Application.Common.Interfaces;

/// <summary>
/// Abstraction over the system clock so time-dependent logic is testable and deterministic.
/// The single implementation returns the real UTC time; tests substitute a fake.
/// </summary>
public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
