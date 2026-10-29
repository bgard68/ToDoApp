using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using TodoApp.Application.Auth.Common;
using TodoApp.Domain.Entities;
using TodoApp.Infrastructure.Persistence;
using Xunit;

namespace TodoApp.UnitTests.Architecture;

/// <summary>
/// Dependency-direction rules that project references cannot express. A project reference
/// stops one project depending on another, but nothing stops a stray package install from
/// pulling EF Core into Application or ASP.NET types into Domain — these tests do, and they
/// name the offending types when they fail.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Category).Assembly;
    private static readonly Assembly Application = typeof(ITokenIssuer).Assembly;
    private static readonly Assembly Infrastructure = typeof(ApplicationDbContext).Assembly;

    // Persistence and web concerns that must never leak inward of Infrastructure/WebApi.
    private static readonly string[] InfrastructureConcerns =
    [
        "Microsoft.EntityFrameworkCore",
        "Dapper",
        "Microsoft.Data",
        "System.Data",
        "Microsoft.AspNetCore",
    ];

    // Application is allowed exactly one of those on this branch: EF Core, because
    // IApplicationDbContext deliberately exposes DbSet<T> to the handlers — that is the
    // EF-flavoured design of this lane. Everything else still may not leak inward.
    private static readonly string[] ApplicationForbidden =
    [
        "Dapper",
        "Microsoft.Data",
        "System.Data",
        "Microsoft.AspNetCore",
    ];

    [Fact]
    public void Domain_DependsOnNoOtherLayer() =>
        MustNotDependOn(Domain,
            ["TodoApp.Application", "TodoApp.Infrastructure", "TodoApp.WebApi"]);

    [Fact]
    public void Domain_StaysFreeOfInfrastructureConcerns() =>
        MustNotDependOn(Domain, InfrastructureConcerns);

    [Fact]
    public void Application_DependsOnlyInward() =>
        MustNotDependOn(Application, ["TodoApp.Infrastructure", "TodoApp.WebApi"]);

    [Fact]
    public void Application_StaysFreeOfInfrastructureConcerns() =>
        MustNotDependOn(Application, ApplicationForbidden);

    [Fact]
    public void Infrastructure_NeverReachesTheWebLayer() =>
        MustNotDependOn(Infrastructure, ["TodoApp.WebApi"]);

    private static void MustNotDependOn(Assembly assembly, string[] forbidden)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        string offenders = string.Join(", ",
            (result.FailingTypes ?? []).Select(t => t.FullName));

        result.IsSuccessful.Should().BeTrue(
            $"no type in {assembly.GetName().Name} may depend on " +
            $"{string.Join(", ", forbidden)} (offenders: {offenders})");
    }
}
