using MediatR;
using TodoApp.Application.Todos.Commands.ChangeStatus;
using TodoApp.Application.Todos.Commands.CreateTodo;
using TodoApp.Application.Todos.Commands.DeleteTodo;
using TodoApp.Application.Todos.Commands.UpdateTodo;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Application.Todos.Queries.GetTodoById;
using TodoApp.Application.Todos.Queries.GetTodos;
using TodoApp.Domain.Enums;

namespace TodoApp.WebApi.Endpoints;

public static class TodoEndpoints
{
    public static IEndpointRouteBuilder MapTodoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/todos")
            .WithTags("Todos")
            .RequireAuthorization();

        group.MapGet("/", async (
            ISender sender,
            TodoFilter filter = TodoFilter.All,
            string? search = null) =>
        {
            var result = await sender.Send(new GetTodosQuery { Filter = filter, Search = search });
            return Results.Ok(result);
        })
        .WithName("GetTodos")
        .Produces<IReadOnlyList<TodoItemDto>>();

        group.MapGet("/{id:int}", async (int id, ISender sender) =>
        {
            var result = await sender.Send(new GetTodoByIdQuery(id));
            return Results.Ok(result);
        })
        .WithName("GetTodoById")
        .Produces<TodoItemDto>()
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (CreateTodoCommand command, ISender sender) =>
        {
            var created = await sender.Send(command);
            return Results.CreatedAtRoute("GetTodoById", new { id = created.Id }, created);
        })
        .WithName("CreateTodo")
        .Produces<TodoItemDto>(StatusCodes.Status201Created)
        .ProducesValidationProblem();

        group.MapPut("/{id:int}", async (int id, UpdateTodoRequest request, ISender sender) =>
        {
            var command = new UpdateTodoCommand
            {
                Id = id,
                Title = request.Title,
                Description = request.Description,
                Priority = request.Priority,
                CategoryId = request.CategoryId,
                DueDate = request.DueDate,
                ConcurrencyToken = request.ConcurrencyToken
            };

            var updated = await sender.Send(command);
            return Results.Ok(updated);
        })
        .WithName("UpdateTodo")
        .Produces<TodoItemDto>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        // Move a card to another lane (drag-and-drop).
        group.MapPatch("/{id:int}/status", async (int id, StatusRequest request, ISender sender) =>
        {
            var updated = await sender.Send(new ChangeTodoStatusCommand
            {
                Id = id,
                Status = request.Status,
                ConcurrencyToken = request.ConcurrencyToken
            });

            return Results.Ok(updated);
        })
        .WithName("ChangeTodoStatus")
        .Produces<TodoItemDto>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        group.MapDelete("/{id:int}", async (int id, ISender sender) =>
        {
            await sender.Send(new DeleteTodoCommand(id));
            return Results.NoContent();
        })
        .WithName("DeleteTodo")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    public record UpdateTodoRequest
    {
        public string Title { get; init; } = string.Empty;
        public string? Description { get; init; }
        public Priority Priority { get; init; } = Priority.Medium;
        public int? CategoryId { get; init; }
        public DateTimeOffset? DueDate { get; init; }
        public Guid? ConcurrencyToken { get; init; }
    }

    public record StatusRequest(TodoStatus Status, Guid? ConcurrencyToken = null);
}
