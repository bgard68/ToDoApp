using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Common.Behaviors;

namespace TodoApp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));

        services.AddValidatorsFromAssembly(assembly);

        services.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // Scoped: it stages a refresh token on the request's database context, so it must share
        // that context's lifetime rather than outlive it.
        services.AddScoped<ITokenIssuer, TokenIssuer>();

        return services;
    }
}
