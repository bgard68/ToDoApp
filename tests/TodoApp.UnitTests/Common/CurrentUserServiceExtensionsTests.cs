using FluentAssertions;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Common;

/// <summary>
/// Thirteen handlers depend on this one line behaving the same way, so it is worth pinning
/// rather than trusting each caller to have copied it correctly.
/// </summary>
public class CurrentUserServiceExtensionsTests
{
    [Fact]
    public void RequireUserId_ReturnsTheId_WhenAuthenticated()
    {
        var currentUser = new FakeCurrentUserService { UserId = 42 };

        currentUser.RequireUserId().Should().Be(42);
    }

    [Fact]
    public void RequireUserId_Throws401_WhenThereIsNoUser()
    {
        var currentUser = new FakeCurrentUserService { UserId = null };

        var act = () => currentUser.RequireUserId();

        act.Should().Throw<UnauthorizedException>();
    }
}
