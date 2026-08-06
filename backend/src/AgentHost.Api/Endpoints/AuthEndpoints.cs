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

        // ---- MFA (TOTP, RFC 6238) ----
        // Enroll/confirm/disable act on the caller's own account. /verify is anonymous because its
        // credential is the challenge token from login, not a session - see MfaChallengeTokenService
        // for why that token cannot be used as an access token.
        authApi.MapPost("/mfa/enroll", EnrollMfa).WithName("EnrollMfa").RequireAuthorization();
        authApi.MapPost("/mfa/confirm", ConfirmMfa).WithName("ConfirmMfa")
            .WithValidation<MfaConfirmRequest>().RequireAuthorization();
        authApi.MapPost("/mfa/disable", DisableMfa).WithName("DisableMfa")
            .WithValidation<MfaDisableRequest>().RequireAuthorization();
        authApi.MapPost("/mfa/verify", VerifyMfa).WithName("VerifyMfa")
            .WithValidation<MfaVerifyRequest>().AllowAnonymous();

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

    /// <summary>
    /// Verifies email + password. For an account with MFA enabled the response carries
    /// <c>mfaRequired: true</c> and a short-lived <c>mfaToken</c> instead of a session - the
    /// password alone never yields a usable access or refresh token in that case. Exchange the
    /// challenge at POST /api/auth/mfa/verify.
    /// </summary>
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
    /// When the address does have an account, the reset link is <b>queued</b> for delivery to it
    /// (<c>Email:Provider</c> = smtp) rather than sent inline: an awaited SMTP round trip would be
    /// slower, and fallible, precisely and only in the branch where the account exists, which
    /// re-opens by timing and by error the enumeration oracle this flat 202 exists to close. See
    /// <see cref="Services.Email.BackgroundEmailDispatcher"/>.
    ///
    /// With <c>Email:Provider</c> = none (the default) nothing is delivered — the token is stored
    /// hashed and the raw value is discarded — and the flow stays inert, which is why the dev-only
    /// escape hatch still exists:
    /// <list type="bullet">
    /// <item><c>Auth:ReturnResetTokenInResponse</c> = false (the default, and the only defensible
    /// production setting): the raw token never leaves the server except by email;</item>
    /// <item><c>Auth:ReturnResetTokenInResponse</c> = true: the raw token comes back in this
    /// response so development and integration tests can drive the flow without a mail relay.
    /// Enabling this in production would let anybody who can name an email address take over that
    /// account, because the caller here is anonymous by design.</item>
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

    /// <summary>
    /// Generates a new TOTP secret for the caller and returns it base32-encoded plus an
    /// <c>otpauth://</c> URI for QR rendering. The enrollment is inert until
    /// POST /api/auth/mfa/confirm proves the authenticator was set up correctly - that two-step
    /// shape is what stops a mis-scanned code from locking the user out. Calling this again
    /// replaces any pending (or existing) enrollment.
    /// </summary>
    private static async Task<IResult> EnrollMfa(
        IMfaService mfaService, IUserRepository userRepository, ICallerContext caller, CancellationToken ct)
    {
        var user = await userRepository.GetAsync(caller.UserId, ct);
        if (user is null) return Results.NotFound();

        return Results.Ok(await mfaService.EnrollAsync(user, ct));
    }

    /// <summary>
    /// Confirms an enrollment with a live code, enabling MFA for the caller and returning their
    /// recovery codes <b>once</b> - only hashes are stored, so they cannot be shown again.
    /// </summary>
    private static async Task<IResult> ConfirmMfa(
        MfaConfirmRequest req, IMfaService mfaService, IUserRepository userRepository,
        ICallerContext caller, CancellationToken ct)
    {
        var user = await userRepository.GetAsync(caller.UserId, ct);
        if (user is null) return Results.NotFound();

        var result = await mfaService.ConfirmAsync(user, req.Code, ct);
        return result is not null
            ? Results.Ok(result)
            : Results.BadRequest(new { error = "Code is invalid or there is no pending MFA enrollment" });
    }

    /// <summary>
    /// Turns MFA off for the caller. Requires a current TOTP code or an unused recovery code: an
    /// access token alone must not be enough to strip the second factor, or a stolen token would
    /// undo the very protection it is supposed to be subject to.
    /// </summary>
    private static async Task<IResult> DisableMfa(
        MfaDisableRequest req, IMfaService mfaService, IUserRepository userRepository,
        ICallerContext caller, CancellationToken ct)
    {
        var user = await userRepository.GetAsync(caller.UserId, ct);
        if (user is null) return Results.NotFound();

        var disabled = await mfaService.DisableAsync(user, req.Code, ct);
        return disabled
            ? Results.NoContent()
            : Results.BadRequest(new { error = "Code is invalid or MFA is not enabled" });
    }

    /// <summary>
    /// Second step of an MFA login: exchanges the challenge token from /api/auth/login plus a TOTP
    /// code (or a recovery code) for the real access+refresh pair. Anonymous, because the caller
    /// has no session yet - the challenge token is the credential, and it is accepted here and
    /// nowhere else.
    ///
    /// Every failure is the same flat 401: bad token, expired token, wrong code, spent recovery
    /// code. A recovery code is consumed on use and cannot be replayed.
    /// </summary>
    private static async Task<IResult> VerifyMfa(MfaVerifyRequest req, IMfaService mfaService, CancellationToken ct)
    {
        var result = await mfaService.VerifyChallengeAsync(req.MfaToken, req.Code, ct);
        return result is not null ? Results.Ok(result) : Results.Unauthorized();
    }

    private static async Task<IResult> Me(ICallerContext caller, IUserRepository userRepository, CancellationToken ct)
    {
        if (!caller.IsAuthenticated) return Results.Unauthorized();

        // Unscoped by design: this resolves the caller's *own* id, taken from their token.
        var user = await userRepository.GetAsync(caller.UserId, ct);
        return user is not null ? Results.Ok(user) : Results.NotFound();
    }
}
