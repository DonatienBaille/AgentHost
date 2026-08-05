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

        // ---- Password reset / change ----
        // The two reset endpoints are anonymous by necessity: someone who cannot log in is exactly
        // who needs them. The change endpoint is authenticated and additionally proves possession
        // of the current password.
        authApi.MapPost("/password-reset/request", RequestPasswordReset).WithName("RequestPasswordReset")
            .WithValidation<PasswordResetRequestRequest>().AllowAnonymous();
        authApi.MapPost("/password-reset/confirm", ConfirmPasswordReset).WithName("ConfirmPasswordReset")
            .WithValidation<PasswordResetConfirmRequest>().AllowAnonymous();
        authApi.MapPost("/password", ChangePassword).WithName("ChangePassword")
            .WithValidation<ChangePasswordRequest>().RequireAuthorization();

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

    /// <summary>
    /// Starts a password reset. <b>Always answers 202</b>, with the same body, whether or not the
    /// address has an account — an endpoint that distinguishes them is a free account-enumeration
    /// oracle, and this one is anonymous and unauthenticated.
    ///
    /// <b>NO EMAIL IS SENT.</b> This deployment has no mailer, so what happens to the minted token
    /// depends on configuration:
    /// <list type="bullet">
    /// <item><c>Auth:ReturnResetTokenInResponse</c> = false (the default, and the only defensible
    /// production setting): the token is stored hashed and the raw value is discarded. Nothing
    /// reaches the user, so the reset flow is a no-op placeholder until a mailer exists. That gap
    /// is deliberate and known — it is not a working reset flow;</item>
    /// <item><c>Auth:ReturnResetTokenInResponse</c> = true: the raw token comes back in this
    /// response so development and integration tests can drive the flow. Enabling this in
    /// production would let anybody who can name an email address take over that account, because
    /// the caller here is anonymous by design.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult> RequestPasswordReset(
        PasswordResetRequestRequest req, IPasswordService passwordService, CancellationToken ct)
    {
        var token = await passwordService.RequestResetAsync(req.Email, ct);

        // 202 either way, and the body only differs by the dev-only token field — never by
        // anything that reveals whether the account exists.
        return Results.Accepted(value: new PasswordResetRequestResponse { Token = token });
    }

    /// <summary>
    /// Completes a password reset: sets the new password, consumes the token, and revokes every
    /// refresh token the user holds so existing sessions end with the old password.
    ///
    /// Unknown, expired, revoked and already-used tokens all produce the same flat 400 — telling
    /// them apart would make this a token-probing oracle.
    /// </summary>
    private static async Task<IResult> ConfirmPasswordReset(
        PasswordResetConfirmRequest req, IPasswordService passwordService, CancellationToken ct)
    {
        var ok = await passwordService.ConfirmResetAsync(req, ct);
        return ok
            ? Results.NoContent()
            : Results.BadRequest(new { error = "Reset token is invalid, expired or already used" });
    }

    /// <summary>
    /// Changes the caller's own password. Requires the current password (an access token alone is
    /// not enough: a stolen token must not be usable to lock the real owner out), and revokes the
    /// caller's other sessions. A fresh access+refresh pair is returned so the session that made
    /// the change keeps working while every other one is logged out.
    /// </summary>
    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest req, IPasswordService passwordService, ICallerContext caller, CancellationToken ct)
    {
        var result = await passwordService.ChangePasswordAsync(caller.UserId, req, ct);
        return result is not null
            ? Results.Ok(result)
            : Results.BadRequest(new { error = "Current password is incorrect" });
    }

    private static async Task<IResult> Me(ICallerContext caller, IUserRepository userRepository, CancellationToken ct)
    {
        if (!caller.IsAuthenticated) return Results.Unauthorized();

        // Unscoped by design: this resolves the caller's *own* id, taken from their token.
        var user = await userRepository.GetAsync(caller.UserId, ct);
        return user is not null ? Results.Ok(user) : Results.NotFound();
    }
}
