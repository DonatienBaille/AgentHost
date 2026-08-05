using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;

namespace AgentHost.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);
    Task<AuthResponse?> LoginAsync(LoginRequest req, CancellationToken ct = default);
}

/// <summary>
/// Self-service signup (creates a new org + owner user) and login. There is no invite/reset
/// flow yet — additional users are added via POST /api/users by an owner/maintainer.
/// </summary>
public class AuthService : IAuthService
{
    private readonly IOrganizationRepository _orgRepository;
    private readonly IUserRepository _userRepository;
    private readonly IJwtTokenService _tokenService;

    public AuthService(IOrganizationRepository orgRepository, IUserRepository userRepository, IJwtTokenService tokenService)
    {
        _orgRepository = orgRepository;
        _userRepository = userRepository;
        _tokenService = tokenService;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
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

        return new AuthResponse { Token = _tokenService.GenerateToken(user), User = user };
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByEmailAsync(req.Email, ct);
        if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            return null;

        return new AuthResponse { Token = _tokenService.GenerateToken(user), User = user };
    }
}
