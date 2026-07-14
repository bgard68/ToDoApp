using MediatR;
using TodoApp.Application.Categories.Commands.CreateCategory;
using TodoApp.Application.Categories.Commands.DeleteCategory;
using TodoApp.Application.Categories.Commands.UpdateCategory;
using TodoApp.Application.Categories.Dtos;
using TodoApp.Application.Categories.Queries.GetCategories;

namespace TodoApp.WebApi.Endpoints;

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/categories")
            .WithTags("Categories")
            .RequireAuthorization();

        group.MapGet("/", async (ISender sender) =>
        {
            var result = await sender.Send(new GetCategoriesQuery());
            return Results.Ok(result);
        })
        .WithName("GetCategories")
        .Produces<IReadOnlyList<CategoryDto>>();

        group.MapPost("/", async (CreateCategoryCommand command, ISender sender) =>
        {
            var created = await sender.Send(command);
            return Results.Created($"/api/categories/{created.Id}", created);
        })
        .WithName("CreateCategory")
        .Produces<CategoryDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        group.MapPut("/{id:int}", async (int id, CategoryRequest request, ISender sender) =>
        {
            var updated = await sender.Send(new UpdateCategoryCommand
            {
                Id = id,
                Name = request.Name,
                Color = request.Color
            });
            return Results.Ok(updated);
        })
        .WithName("UpdateCategory")
        .Produces<CategoryDto>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        group.MapDelete("/{id:int}", async (int id, ISender sender) =>
        {
            await sender.Send(new DeleteCategoryCommand(id));
            return Results.NoContent();
        })
        .WithName("DeleteCategory")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    public record CategoryRequest(string Name, string Color);
}
