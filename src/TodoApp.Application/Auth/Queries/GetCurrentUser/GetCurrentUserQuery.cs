using MediatR;
using TodoApp.Application.Auth.Dtos;

namespace TodoApp.Application.Auth.Queries.GetCurrentUser;

public record GetCurrentUserQuery : IRequest<UserDto>;
