using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Dtos;

public record UserDto(int Id, string Email, string Role)
{
    public static UserDto FromEntity(User user) => new(user.Id, user.Email, user.Role.ToString());
}
