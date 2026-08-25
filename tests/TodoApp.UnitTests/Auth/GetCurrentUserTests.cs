using FluentAssertions;
using TodoApp.Application.Auth.Queries.GetCurrentUser;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Auth;

public class GetCurrentUserTests
{
    private readonly FakeDateTimeProvider _clock = new();

    [Fact]
    public async Task Handle_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new GetCurrentUserQueryHandler(db.NewContext(), new FakeCurrentUserService());

        var act = () => handler.Handle(new GetCurrentUserQuery(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Handle_WhenTheUserRowIsGone_Throws()
    {
        using var db = new TestDatabase();
        // A token can outlive the row it names — deleting the account must not 500.
        var handler = new GetCurrentUserQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = 4242 });

        var act = () => handler.Handle(new GetCurrentUserQuery(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Handle_ReturnsTheCallersOwnProfile()
    {
        using var db = new TestDatabase();
        var user = new User("Me@Example.com", "hash", _clock.UtcNow, UserRole.Admin);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();

        var handler = new GetCurrentUserQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id });

        var dto = await handler.Handle(new GetCurrentUserQuery(), CancellationToken.None);

        dto.Id.Should().Be(user.Id);
        dto.Email.Should().Be("me@example.com");
        dto.Role.Should().Be(nameof(UserRole.Admin));
    }
}
