using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Services.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Les deux flux qui envoient un courriel, exercés de bout en bout contre un expéditeur qui
/// capture les messages en mémoire. Aucun test ne parle à un vrai serveur SMTP — un test qui
/// dépendrait d'un relais joignable serait un test instable, et ce n'est pas la conformité SMTP
/// du BCL qui est en jeu ici mais le câblage de ce dépôt.
///
/// Ce que ces tests protègent en priorité : l'absence d'oracle d'énumération de comptes sur
/// POST /api/auth/password-reset/request. Un envoi de courriel est la manière la plus facile de
/// rouvrir cet oracle sans s'en apercevoir — par le temps de réponse, par un échec visible, par
/// un code d'état différent — donc chaque propriété est vérifiée avec un mailer branché, y compris
/// un mailer en panne.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class EmailDeliveryTests : IAsyncLifetime
{
    /// <summary>Racine choisie pour être reconnaissable dans les liens capturés.</summary>
    private const string FrontEndBaseUrl = "https://agenthost.test";

    /// <summary>
    /// Hôte dont l'IEmailSender est remplacé par un faux. L'injection passe par ConfigureServices
    /// et non par la configuration : elle s'applique après les enregistrements de Program.cs, donc
    /// elle gagne, sans dépendre du moment où Program lit la section Email.
    /// </summary>
    private sealed class CapturingMailerFactory : AgentHostApiFactory
    {
        public CapturingEmailSender Sender { get; }

        public CapturingMailerFactory(bool failing = false) =>
            Sender = new CapturingEmailSender { ThrowOnSend = failing };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IEmailSender>(Sender);
                services.AddSingleton(new EmailOptions
                {
                    Provider = EmailOptions.SmtpProvider,
                    FromAddress = "no-reply@agenthost.test",
                    AppBaseUrl = FrontEndBaseUrl,
                });
            });
        }
    }

    private CapturingMailerFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CapturingMailerFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>Extrait la valeur de <c>?token=</c> du premier lien trouvé dans un corps de message.</summary>
    private static string TokenFromLink(string body, string path)
    {
        var marker = $"{FrontEndBaseUrl}{path}?token=";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Aucun lien vers {path} dans le corps du message :\n{body}");

        var value = body[(start + marker.Length)..];
        var end = value.IndexOfAny(new[] { ' ', '\r', '\n' });
        return Uri.UnescapeDataString(end < 0 ? value : value[..end]);
    }

    // ---- Réinitialisation de mot de passe ----

    [Fact]
    public async Task RequestReset_EmailsTheLink_AndItsTokenActuallyResetsThePassword()
    {
        var registered = await TestData.RegisterAsync(_client);
        const string newPassword = "un-tout-nouveau-mot-de-passe-42";

        var request = await _client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = registered.User.Email });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        // Auth:ReturnResetTokenInResponse est off ici : le courriel est le SEUL moyen d'obtenir le
        // jeton, ce qui est précisément ce qui rend le flux utilisable en production.
        var body = await request.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        Assert.Null(body!.Token);

        var message = await _factory.Sender.WaitForAsync(registered.User.Email);
        Assert.NotNull(message);
        Assert.Equal(EmailTemplates.PasswordResetKind, message!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));

        // Le lien porte bien le jeton brut : la preuve est qu'il change réellement le mot de passe.
        var token = TokenFromLink(message.TextBody, "/reset-password");
        var confirm = await _client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = token, NewPassword = newPassword });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        var login = await _client.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = registered.User.Email, Password = newPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task RequestReset_StillAnswers202_AndSendsNothing_ForAnUnknownEmail()
    {
        var unknown = $"personne-{TestData.Suffix()}@example.com";
        var registered = await TestData.RegisterAsync(_client);

        var unknownResponse = await _client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = unknown });
        var knownResponse = await _client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = registered.User.Email });

        Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode);

        // Corps identiques : rien dans la réponse ne distingue les deux adresses.
        var unknownBody = await unknownResponse.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        var knownBody = await knownResponse.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        Assert.Equal(unknownBody!.Message, knownBody!.Message);
        Assert.Null(unknownBody.Token);
        Assert.Null(knownBody.Token);

        // La file est vidée dans l'ordre : quand le message de l'adresse connue est arrivé, celui
        // de l'adresse inconnue serait déjà là s'il existait. On n'attend donc aucun délai
        // arbitraire pour conclure qu'il n'a jamais été mis en file.
        Assert.NotNull(await _factory.Sender.WaitForAsync(registered.User.Email));
        Assert.False(_factory.Sender.HasMessageFor(unknown));
    }

    [Fact]
    public async Task RequestReset_IsUnaffected_WhenEveryDeliveryFails()
    {
        // Un relais en panne ne doit ni casser la requête, ni la ralentir différemment, ni changer
        // quoi que ce soit d'observable entre une adresse connue et une adresse inconnue.
        await using var factory = new CapturingMailerFactory(failing: true);
        var client = factory.CreateClient();

        var registered = await TestData.RegisterAsync(client);
        var unknown = $"personne-{TestData.Suffix()}@example.com";

        var known = await client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = registered.User.Email });
        var missing = await client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = unknown });

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, missing.StatusCode);

        var knownBody = await known.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        var missingBody = await missing.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        Assert.Equal(knownBody!.Message, missingBody!.Message);
        Assert.Null(knownBody.Token);
        Assert.Null(missingBody.Token);

        // L'envoi a bien été tenté et a bien échoué, hors du chemin de réponse : la requête
        // ci-dessus n'en a rien su.
        Assert.NotNull(await factory.Sender.WaitForAsync(registered.User.Email));
        Assert.False(factory.Sender.HasMessageFor(unknown));

        // Et l'hôte reste sain : une requête suivante fonctionne toujours.
        var again = await client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = registered.User.Email });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
    }

    // ---- Invitations ----

    [Fact]
    public async Task CreateInvitation_EmailsTheLink_AndWithholdsTheTokenFromTheInviter()
    {
        var owner = await TestData.RegisterAsync(_client);
        var authed = TestData.AuthedClient(_factory, owner.Token);
        var invitee = $"invite-{TestData.Suffix()}@example.com";

        var created = await authed.PostJsonAsync("/api/invitations",
            new CreateInvitationRequest { Email = invitee, Role = UserRole.Developer });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Le jeton est parti à l'invité : le renvoyer à celui qui invite n'ajouterait qu'une
        // copie d'un porteur capable de créer un compte à la place de l'invité.
        var response = await created.Content.ReadFromJsonAsync<CreateInvitationResponse>(TestJson.Options);
        Assert.Null(response!.Token);

        var message = await _factory.Sender.WaitForAsync(invitee);
        Assert.NotNull(message);
        Assert.Equal(EmailTemplates.InvitationKind, message!.Kind);

        // Le lien porte le jeton brut : il permet réellement d'accepter l'invitation.
        var token = TokenFromLink(message.TextBody, "/accept-invitation");
        var accepted = await _factory.CreateClient().PostJsonAsync("/api/invitations/accept",
            new AcceptInvitationRequest
            {
                Token = token,
                Password = TestData.DefaultPassword,
                DisplayName = "Invité",
            });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task CreateInvitation_StillReturnsTheToken_WhenNoMailerIsConfigured()
    {
        // Le chemin par défaut du dépôt, et le seul qui fonctionne aujourd'hui : sans mailer, le
        // jeton doit continuer d'être rendu à celui qui invite, sans quoi plus rien ne le remet.
        await using var factory = new AgentHostApiFactory();
        var client = factory.CreateClient();
        var owner = await TestData.RegisterAsync(client);
        var authed = TestData.AuthedClient(factory, owner.Token);

        var created = await authed.PostJsonAsync("/api/invitations",
            new CreateInvitationRequest { Email = $"invite-{TestData.Suffix()}@example.com", Role = UserRole.Developer });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var response = await created.Content.ReadFromJsonAsync<CreateInvitationResponse>(TestJson.Options);
        Assert.False(string.IsNullOrWhiteSpace(response!.Token));
    }
}
