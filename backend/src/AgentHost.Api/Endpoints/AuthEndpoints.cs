using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Authentication. Login and refresh both return a short-lived access token plus a revocable
/// refresh token; see <see cref="AuthService"/> for the rotation/revocation semantics.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var authApi = app.MapGroup("/api/auth").WithTags("Auth");

        authApi.MapPost("/register", Register).WithName("Register")
            .WithValidation<RegisterRequest>().AllowAnonymous();
        authApi.MapPost("/login", Login).WithName("Login")
            .WithValidation<LoginRequest>().AllowAnonymous();
        authApi.MapPost("/refresh", Refresh).WithName("Refresh")
            .WithValidation<RefreshRequest>().AllowAnonymous();
        authApi.MapPost("/logout", Logout).WithName("Logout").RequireAuthorization();
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
        catch (UnauthorizedAccessException ex)
        {
            // Auth:AllowSelfRegistration is off: this instance provisions organizations out of
            // band, so anonymous signup is refused rather than silently creating a tenant.
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
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

    /// <summary>
    /// Rotates a refresh token: the presented token is revoked and a fresh access+refresh pair is
    /// issued. An unknown, expired or already-used token is a flat 401 — no detail about which.
    /// </summary>
    private static async Task<IResult> Refresh(RefreshRequest req, IAuthService authService, CancellationToken ct)
    {
        var result = await authService.RefreshAsync(req.RefreshToken, ct);
        return result is not null ? Results.Ok(result) : Results.Unauthorized();
    }

    /// <summary>
    /// Revokes all of the caller's refresh tokens. Their *access* token stays valid until it
    /// expires — JWTs are stateless and this deployment keeps no deny-list; the short access-token
    /// TTL (Jwt:ExpiryMinutes, 15 minutes) is the mitigation for that window, by design.
    /// </summary>
    private static async Task<IResult> Logout(IAuthService authService, ICallerContext caller, CancellationToken ct)
    {
        var revoked = await authService.LogoutAsync(caller.UserId, ct);
        return Results.Ok(new { revoked });
    }

    private static async Task<IResult> Me(ICallerContext caller, IUserRepository userRepository, CancellationToken ct)
    {
        if (!caller.IsAuthenticated) return Results.Unauthorized();

        // Unscoped by design: this resolves the caller's *own* id, taken from their token.
        var user = await userRepository.GetAsync(caller.UserId, ct);
        return user is not null ? Results.Ok(user) : Results.NotFound();
    }
}
