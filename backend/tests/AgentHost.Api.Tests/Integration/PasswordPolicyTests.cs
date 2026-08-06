using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// The password policy at <b>every</b> entry point that sets a password: registration, admin user
/// creation, admin user update, invitation acceptance, reset confirmation and self-service change.
/// A policy enforced at five of six doors is not a policy.
///
/// The breached-password half of the policy is exercised with a stubbed
/// <see cref="IBreachedPasswordChecker"/> — <b>no test here reaches the network</b>. The real HIBP
/// client's k-anonymity and fail-open behaviour is covered by <c>BreachedPasswordCheckerTests</c>.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PasswordPolicyTests
{
    /// <summary>Any password containing this marker is "breached" as far as the stub is concerned.</summary>
    private const string BreachedMarker = "pwned-corpus-marker";

    private static readonly string BreachedPassword = $"{BreachedMarker}-but-otherwise-strong";

    private readonly AgentHostApiFactory _factory;

    public PasswordPolicyTests(AgentHostApiFactory factory) => _factory = factory;

    private sealed class StubBreachedPasswordChecker : IBreachedPasswordChecker
    {
        public bool Enabled => true;

        public Task<bool> IsBreachedAsync(string password, CancellationToken ct = default) =>
            Task.FromResult(password.Contains(BreachedMarker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Host with the breach check answered locally by <see cref="StubBreachedPasswordChecker"/>,
    /// and reset tokens returned so the reset entry point can be driven end to end.
    /// </summary>
    private sealed class BreachCheckedFactory : AgentHostApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Auth:ReturnResetTokenInResponse"] = "true" }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IBreachedPasswordChecker, StubBreachedPasswordChecker>());
        }
    }

    /// <summary>Weak passwords that the local (network-free) rules reject on their own.</summary>
    public static TheoryData<string> WeakPasswords => new() { "short11", "elevenchars", "aaaaaaaaaaaaaaaa", "password1234" };

    [Theory]
    [MemberData(nameof(WeakPasswords))]
    public async Task Register_RejectsWeakPasswords(string weak) =>
        await AssertRejectedAtEveryEntryPointAsync(_factory, weak);

    /// <summary>
    /// Same six doors, this time with a password the *breach corpus* rejects rather than the local
    /// rules — proving the opt-in check is wired into all of them, not just registration.
    /// </summary>
    [Fact]
    public async Task BreachedPassword_IsRejectedAtEveryEntryPoint()
    {
        await using var factory = new BreachCheckedFactory();
        await AssertRejectedAtEveryEntryPointAsync(factory, BreachedPassword);
    }

    /// <summary>
    /// ...and a password the stub does not know stays acceptable at those same doors, so the check
    /// is not simply refusing everything.
    /// </summary>
    [Fact]
    public async Task UnbreachedPassword_IsStillAcceptedWhenTheCheckIsOn()
    {
        await using var factory = new BreachCheckedFactory();
        var suffix = TestData.Suffix();
        var client = factory.CreateClient();

        var owner = await TestData.RegisterAsync(client, suffix);
        var authed = TestData.AuthedClient(factory, owner.Token);

        var created = await authed.PostJsonAsync("/api/users", new CreateUserRequest
        {
            Email = $"clean-{suffix}@example.com",
            Password = TestData.DefaultPassword,
            Role = UserRole.Viewer,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var changed = await authed.PostJsonAsync("/api/auth/password", new ChangePasswordRequest
        {
            CurrentPassword = TestData.DefaultPassword,
            NewPassword = "a-perfectly-fine-new-passphrase",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    /// <summary>
    /// Drives all six set-password entry points with <paramref name="password"/> and asserts each
    /// answers 400 without setting anything.
    /// </summary>
    private static async Task AssertRejectedAtEveryEntryPointAsync(AgentHostApiFactory factory, string password)
    {
        var suffix = TestData.Suffix();
        var anonymous = factory.CreateClient();

        // 1. POST /api/auth/register
        var register = await anonymous.PostJsonAsync("/api/auth/register", new RegisterRequest
        {
            OrgName = $"Policy {suffix}",
            OrgSlug = $"policy-{suffix}",
            Email = $"policy-{suffix}@example.com",
            Password = password,
        });
        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);

        // A real org to drive the authenticated entry points from.
        var owner = await TestData.RegisterAsync(anonymous, suffix);
        var authed = TestData.AuthedClient(factory, owner.Token);

        // 2. POST /api/users
        var createUser = await authed.PostJsonAsync("/api/users", new CreateUserRequest
        {
            Email = $"member-{suffix}@example.com",
            Password = password,
            Role = UserRole.Viewer,
        });
        Assert.Equal(HttpStatusCode.BadRequest, createUser.StatusCode);

        // 3. PUT /api/users/{id}
        var updateUser = await authed.PutJsonAsync($"/api/users/{owner.User.Id}",
            new UpdateUserRequest { Password = password });
        Assert.Equal(HttpStatusCode.BadRequest, updateUser.StatusCode);

        // 4. POST /api/invitations/accept
        var inviteResponse = await authed.PostJsonAsync("/api/invitations", new CreateInvitationRequest
        {
            Email = $"invitee-{suffix}@example.com",
            Role = UserRole.Developer,
        });
        inviteResponse.EnsureSuccessStatusCode();
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreateInvitationResponse>(TestJson.Options);

        var accept = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = invite!.Token!,
            Password = password,
        });
        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);

        // 5. POST /api/auth/password-reset/confirm — only reachable where the host returns the
        // token (dev/test flag); elsewhere the earlier doors are the coverage.
        var resetResponse = await anonymous.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = owner.User.Email });
        resetResponse.EnsureSuccessStatusCode();
        var reset = await resetResponse.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        if (!string.IsNullOrEmpty(reset!.Token))
        {
            var confirm = await anonymous.PostJsonAsync("/api/auth/password-reset/confirm",
                new PasswordResetConfirmRequest { Token = reset.Token, NewPassword = password });
            Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        }

        // 6. POST /api/auth/password
        var change = await authed.PostJsonAsync("/api/auth/password", new ChangePasswordRequest
        {
            CurrentPassword = TestData.DefaultPassword,
            NewPassword = password,
        });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);

        // Nothing was actually set: the original password still logs in.
        var login = await anonymous.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = owner.User.Email, Password = TestData.DefaultPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
