using MediatR;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Application.Auth.Queries.GetCurrentUser;

public class GetCurrentUserQueryHandler : IRequestHandler<GetCurrentUserQuery, UserDto>
{
    private readonly IUserRepository _users;
    private readonly ICurrentUserService _currentUser;

    public GetCurrentUserQueryHandler(IUserRepository users, ICurrentUserService currentUser)
    {
        _users = users;
        _currentUser = currentUser;
    }

    public async Task<UserDto> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var user = await _users.GetByIdAsync(userId, cancellationToken)
            ?? throw new UnauthorizedException();

        return UserDto.FromEntity(user);
    }
}
