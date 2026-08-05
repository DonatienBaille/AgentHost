using System.Security.Cryptography;
using System.Text;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;

namespace AgentHost.Api.Services;

public interface IAuthService
{
    /// <summary>True when <c>Auth:AllowSelfRegistration</c> permits anonymous organization creation.</summary>
    bool SelfRegistrationAllowed { get; }

    Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);
    Task<AuthResponse?> LoginAsync(LoginRequest req, CancellationToken ct = default);

    /// <summary>
    /// Exchanges a valid refresh token for a brand-new access+refresh pair, revoking the token
    /// that was presented (rotation). Returns null when it is unknown, expired or already used.
    /// </summary>
    Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Revokes every active refresh token for a user. Returns how many were revoked.</summary>
    Task<int> LogoutAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Self-service signup (creates a new org + owner user), login, and the refresh-token lifecycle.
/// Members can also be added by invitation (see <see cref="InvitationService"/>) or directly via
/// POST /api/users by an owner/maintainer, always inside their own organization.
///
/// Token model: a short-lived stateless JWT access token plus a long-lived, persisted, revocable
/// refresh token. Only a SHA-256 hash of the refresh token is stored — the raw value is handed to
/// the client once and never again. Access tokens deliberately stay valid until they expire even
/// after logout (there is no server-side JWT deny-list); the short TTL is the mitigation, and the
/// refresh token is the half that can actually be revoked.
/// </summary>
public class AuthService : IAuthService
{
    private readonly IOrganizationRepository _orgRepository;
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IAuthTokenIssuer _tokenIssuer;
    private readonly bool _allowSelfRegistration;

    public AuthService(
        IOrganizationRepository orgRepository,
        IUserRepository userRepository,
        IRefreshTokenRepository refreshTokenRepository,
        IAuthTokenIssuer tokenIssuer,
        IConfiguration config)
    {
        _orgRepository = orgRepository;
        _userRepository = userRepository;
        _refreshTokenRepository = refreshTokenRepository;
        _tokenIssuer = tokenIssuer;

        // Absent key => allowed, preserving the historical behaviour for deployments that have not
        // added the setting yet. appsettings.json ships it explicitly so it is discoverable.
        _allowSelfRegistration = !bool.TryParse(config["Auth:AllowSelfRegistration"], out var allow) || allow;
    }

    public bool SelfRegistrationAllowed => _allowSelfRegistration;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        if (!_allowSelfRegistration)
            throw new UnauthorizedAccessException("Self-service registration is disabled on this instance");

        if (await _orgRepository.GetBySlugAsync(req.OrgSlug, ct) is not null)
            throw new InvalidOperationException($"Organization slug '{req.OrgSlug}' is already taken");
        if (await _userRepository.GetByEmailAsync(req.Email, ct) is not null)
            throw new InvalidOperationException($"Email '{req.Email}' is already registered");

        var now = DateTime.UtcNow;
        var org = new Organization
        {
            Id = UlidGenerator.NewUlid(),
            Name = req.OrgName,
            Slug = req.OrgSlug,
            Plan = "free",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _orgRepository.InsertAsync(org, ct);

        var user = new User
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = org.Id,
            Email = req.Email,
            DisplayName = req.DisplayName,
            Role = UserRole.Owner,
            PasswordHash = PasswordHasher.Hash(req.Password),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _userRepository.InsertAsync(user, ct);

        var (response, _) = await _tokenIssuer.IssueAsync(user, ct);
        return response;
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByEmailAsync(req.Email, ct);
        if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            return null;

        var (response, _) = await _tokenIssuer.IssueAsync(user, ct);
        return response;
    }

    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var stored = await _refreshTokenRepository.GetByHashAsync(OpaqueToken.Hash(refreshToken), ct);
        if (stored is null) return null;

        if (!stored.IsActive(DateTime.UtcNow))
        {
            // Presenting an already-revoked token is the classic signature of a stolen-token
            // replay: the legitimate client rotated and the thief replayed the old value, or vice
            // versa. Kill the whole family so neither party keeps a usable credential.
            if (stored.RevokedAt is not null)
                await _refreshTokenRepository.RevokeAllForUserAsync(stored.UserId, ct);
            return null;
        }

        // Unscoped by necessity: /api/auth/refresh is anonymous, and the refresh token itself is
        // the only credential presented. The user id comes from the stored record, not the request.
        var user = await _userRepository.GetAsync(stored.UserId, ct);
        if (user is null)
        {
            await _refreshTokenRepository.RevokeAsync(stored.Id, null, ct);
            return null;
        }

        var (response, newTokenId) = await _tokenIssuer.IssueAsync(user, ct);
        await _refreshTokenRepository.RevokeAsync(stored.Id, newTokenId, ct);
        return response;
    }

    public Task<int> LogoutAsync(string userId, CancellationToken ct = default) =>
        _refreshTokenRepository.RevokeAllForUserAsync(userId, ct);

}
