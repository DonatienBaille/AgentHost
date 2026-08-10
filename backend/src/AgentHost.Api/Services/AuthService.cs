using System.Security.Cryptography;
using System.Text;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Npgsql;

namespace AgentHost.Api.Services;

public interface IAuthService
{
    /// <summary>True when <c>Auth:AllowSelfRegistration</c> permits anonymous organization creation.</summary>
    bool SelfRegistrationAllowed { get; }

    Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);

    /// <summary>
    /// Verifies an email/password pair. For an account without MFA this returns a full
    /// access+refresh pair; for one with MFA enabled it returns only a short-lived challenge token
    /// (<c>mfaRequired: true</c>), which must be exchanged at POST /api/auth/mfa/verify. Null when
    /// the credentials are wrong.
    /// </summary>
    Task<LoginResponse?> LoginAsync(LoginRequest req, CancellationToken ct = default);

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
    private readonly IUserMfaRepository _mfaRepository;
    private readonly IAuthTokenIssuer _tokenIssuer;
    private readonly IMfaChallengeTokenService _challengeTokens;
    private readonly bool _allowSelfRegistration;

    public AuthService(
        IOrganizationRepository orgRepository,
        IUserRepository userRepository,
        IRefreshTokenRepository refreshTokenRepository,
        IUserMfaRepository mfaRepository,
        IAuthTokenIssuer tokenIssuer,
        IMfaChallengeTokenService challengeTokens,
        IConfiguration config)
    {
        _orgRepository = orgRepository;
        _userRepository = userRepository;
        _refreshTokenRepository = refreshTokenRepository;
        _mfaRepository = mfaRepository;
        _tokenIssuer = tokenIssuer;
        _challengeTokens = challengeTokens;

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
        // Les deux vérifications ci-dessus sont des lire-puis-décider : deux inscriptions
        // simultanées avec la même adresse les passent toutes les deux. Ce qui tient réellement,
        // ce sont les contraintes d'unicité de `organizations.slug` et `users.email` — et il faut
        // donc traduire leur violation, sans quoi la seconde inscription reçoit une erreur 500
        // là où la version séquentielle rend un 409 parfaitement clair.
        try
        {
            await _orgRepository.InsertAsync(org, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"Organization slug '{req.OrgSlug}' is already taken");
        }

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

        try
        {
            await _userRepository.InsertAsync(user, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // L'organisation vient d'être créée et personne n'en connaît l'identifiant : elle ne
            // peut être référencée par rien. La laisser serait une fuite silencieuse — une
            // organisation vide de plus à chaque collision d'adresse.
            await _orgRepository.DeleteIfUninhabitedAsync(org.Id, ct);
            throw new InvalidOperationException($"Email '{req.Email}' is already registered");
        }

        var (response, _) = await _tokenIssuer.IssueAsync(user, ct);
        return response;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByEmailAsync(req.Email, ct);
        if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            return null;

        // MFA step-up. A correct password is not, on its own, enough to get a session on an account
        // that has enabled a second factor: no access token and no refresh token are issued here.
        // The challenge token proves only "this password was correct just now", and the sole thing
        // it can be exchanged for is a real pair at POST /api/auth/mfa/verify.
        var mfa = await _mfaRepository.GetAsync(user.Id, ct);
        if (mfa is { Enabled: true })
            return LoginResponse.Challenge(_challengeTokens.Issue(user), _challengeTokens.LifetimeSeconds);

        var (response, _) = await _tokenIssuer.IssueAsync(user, ct);
        return LoginResponse.Authenticated(response);
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

        // La révocation AVANT l'émission, et c'est tout l'objet du correctif. Dans l'autre ordre —
        // émettre puis révoquer — deux rafraîchissements simultanés présentant le même jeton
        // lisaient tous deux un jeton actif, émettaient tous deux une paire, et se révoquaient un
        // jeton déjà révoqué sans jamais s'en apercevoir : deux familles vivantes issues d'une
        // seule, donc une rotation qui n'en est pas une.
        //
        // Ici, la révocation est le point d'exclusion : `UPDATE ... WHERE revoked_at IS NULL` n'a
        // qu'un gagnant. Le perdant n'a rien émis et repart en refus, ce que l'intercepteur du
        // frontend traite déjà comme une session à reprendre.
        if (!await _refreshTokenRepository.RevokeAsync(stored.Id, null, ct))
            return null;

        var (response, newTokenId) = await _tokenIssuer.IssueAsync(user, ct);

        // Le chaînage est renseigné après coup : le successeur n'existait pas encore au moment où
        // il fallait trancher. C'est de la traçabilité, pas une garantie — la perdre laisserait le
        // jeton révoqué sans successeur nommé, jamais deux jetons vivants.
        await _refreshTokenRepository.SetReplacedByAsync(stored.Id, newTokenId, ct);
        return response;
    }

    public Task<int> LogoutAsync(string userId, CancellationToken ct = default) =>
        _refreshTokenRepository.RevokeAllForUserAsync(userId, ct);

}
