using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;

namespace AgentHost.Api.Services;

/// <summary>
/// Mints the access+refresh pair that ends every successful authentication, whatever proved the
/// user's identity: a password (<see cref="AuthService"/>), an invitation token
/// (<see cref="InvitationService"/>), or an MFA challenge plus a TOTP/recovery code
/// (<see cref="MfaService"/>).
///
/// Kept as its own service so those three flows cannot drift apart — in particular so none of them
/// can accidentally skip persisting a revocable refresh token, or persist the raw token instead of
/// its hash.
/// </summary>
public interface IAuthTokenIssuer
{
    /// <summary>
    /// Issues a stateless access token plus a freshly persisted, revocable refresh token.
    /// Returns the response to hand back to the client and the id of the refresh-token row, which
    /// callers that are *rotating* a token need in order to record the replacement link.
    /// </summary>
    Task<(AuthResponse Response, string RefreshTokenId)> IssueAsync(User user, CancellationToken ct = default);
}

public class AuthTokenIssuer : IAuthTokenIssuer
{
    /// <summary>How long a refresh token stays usable if it is never rotated.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(14);

    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IJwtTokenService _tokenService;

    public AuthTokenIssuer(IRefreshTokenRepository refreshTokenRepository, IJwtTokenService tokenService)
    {
        _refreshTokenRepository = refreshTokenRepository;
        _tokenService = tokenService;
    }

    public async Task<(AuthResponse Response, string RefreshTokenId)> IssueAsync(User user, CancellationToken ct = default)
    {
        var rawRefreshToken = OpaqueToken.New();
        var now = DateTime.UtcNow;

        var record = new RefreshToken
        {
            Id = UlidGenerator.NewUlid(),
            UserId = user.Id,
            OrgId = user.OrgId,
            TokenHash = OpaqueToken.Hash(rawRefreshToken),
            ExpiresAt = now.Add(RefreshTokenLifetime),
            CreatedAt = now,
        };

        await _refreshTokenRepository.InsertAsync(record, ct);

        var response = new AuthResponse
        {
            Token = _tokenService.GenerateToken(user),
            RefreshToken = rawRefreshToken,
            ExpiresInSeconds = _tokenService.AccessTokenLifetimeMinutes * 60,
            User = user,
        };

        return (response, record.Id);
    }
}
