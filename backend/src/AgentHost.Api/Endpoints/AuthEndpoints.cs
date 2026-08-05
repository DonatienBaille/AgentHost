using System.Security.Claims;
using AgentHost.Api.Contracts;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var authApi = app.MapGroup("/api/auth").WithTags("Auth");

        authApi.MapPost("/register", Register).WithName("Register")
            .WithValidation<RegisterRequest>().AllowAnonymous();
        authApi.MapPost("/login", Login).WithName("Login")
            .WithValidation<LoginRequest>().AllowAnonymous();
        authApi.MapGet("/me", Me).WithName("Me").RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Register(RegisterRequest req, IAuthService authService, CancellationToken ct)
    {
        try
        {
            var result = await authService.RegisterAsync(req, ct);
            return Results.Created($"/api/users/{result.User.Id}", result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> Login(LoginRequest req, IAuthService authService, CancellationToken ct)
    {
        var result = await authService.LoginAsync(req, ct);
        return result is not null ? Results.Ok(result) : Results.Unauthorized();
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, IUserRepository userRepository, CancellationToken ct)
    {
        var userId = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var user = await userRepository.GetAsync(userId, ct);
        return user is not null ? Results.Ok(user) : Results.NotFound();
    }
}
