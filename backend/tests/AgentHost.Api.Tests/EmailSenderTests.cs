using AgentHost.Api.Services.Email;
using Microsoft.Extensions.Configuration;
using Serilog;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// L'expéditeur no-op, la file d'envoi et la construction des messages, testés sans aucun réseau.
///
/// Le contrat central que ces tests figent : <b>rien de ce qui touche au courriel ne peut faire
/// échouer un appelant</b>. Le no-op ne lève jamais, la mise en file ne lève jamais, et un
/// expéditeur qui explose est absorbé par la tâche de fond.
/// </summary>
public class EmailSenderTests
{
    private static ILogger Logger => new LoggerConfiguration().CreateLogger();

    private static EmailMessage Message(string to = "someone@example.com") => new()
    {
        To = to,
        Subject = "Sujet",
        TextBody = "Corps",
        Kind = "test",
    };

    private static EmailOptions Options(Dictionary<string, string?> settings) =>
        EmailOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    // ---- NoOpEmailSender ----

    [Fact]
    public async Task NoOpSender_SendsNothing_AndNeverThrows()
    {
        var sender = new NoOpEmailSender(Logger, "aucun fournisseur configuré");

        Assert.False(sender.IsConfigured);

        // Le contrat est l'absence d'effet : pas d'exception, pas de réseau, rien à observer.
        await sender.SendAsync(Message());
        await sender.SendAsync(Message("someone-else@example.com"));
    }

    [Fact]
    public async Task NoOpSender_DoesNotThrow_OnAnAlreadyCancelledToken()
    {
        var sender = new NoOpEmailSender(Logger, "aucun fournisseur configuré");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await sender.SendAsync(Message(), cts.Token);
    }

    [Fact]
    public void NoMailerConfigured_ResolvesToTheNoOpSender()
    {
        var sender = EmailSenderFactory.Create(Options(new Dictionary<string, string?>()), Logger);

        Assert.IsType<NoOpEmailSender>(sender);
        Assert.False(sender.IsConfigured);
    }

    [Theory]
    // Provider smtp mais rien derrière : on retombe sur le no-op plutôt que d'échouer au démarrage.
    [InlineData("smtp", "", "expediteur@example.com")]
    [InlineData("smtp", "relais.example.com", "")]
    [InlineData("smtp", "relais.example.com", "pas-une-adresse")]
    // Fournisseur inconnu : jamais d'envoi par accident.
    [InlineData("sendgrid", "relais.example.com", "expediteur@example.com")]
    public void IncompleteConfiguration_FallsBackToTheNoOpSender(string provider, string host, string from)
    {
        var sender = EmailSenderFactory.Create(Options(new Dictionary<string, string?>
        {
            ["Email:Provider"] = provider,
            ["Email:Smtp:Host"] = host,
            ["Email:FromAddress"] = from,
        }), Logger);

        Assert.IsType<NoOpEmailSender>(sender);
    }

    [Fact]
    public void CompleteSmtpConfiguration_ResolvesToTheSmtpSender()
    {
        var sender = EmailSenderFactory.Create(Options(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "smtp",
            ["Email:Smtp:Host"] = "relais.example.com",
            ["Email:FromAddress"] = "expediteur@example.com",
        }), Logger);

        Assert.IsType<SmtpEmailSender>(sender);
        Assert.True(sender.IsConfigured);
    }

    // ---- BackgroundEmailDispatcher ----

    [Fact]
    public async Task Dispatcher_DeliversQueuedMessagesToTheSender()
    {
        var sender = new CapturingEmailSender();
        var dispatcher = TestDispatcher.Create(sender, new InMemoryOutbox(), Logger);
        await dispatcher.StartAsync(CancellationToken.None);

        dispatcher.Enqueue(Message("first@example.com"));
        dispatcher.Enqueue(Message("second@example.com"));

        Assert.NotNull(await sender.WaitForAsync("first@example.com"));
        Assert.NotNull(await sender.WaitForAsync("second@example.com"));

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatcher_SurvivesASenderThatThrows_AndKeepsDeliveringAfterwards()
    {
        // Un seul destinataire refusé ne doit pas arrêter tous les envois suivants du processus.
        var sender = new CapturingEmailSender { ThrowOnSend = true };
        var dispatcher = TestDispatcher.Create(sender, new InMemoryOutbox(), Logger);
        await dispatcher.StartAsync(CancellationToken.None);

        dispatcher.Enqueue(Message("boom@example.com"));
        dispatcher.Enqueue(Message("after-the-boom@example.com"));

        Assert.NotNull(await sender.WaitForAsync("boom@example.com"));
        Assert.NotNull(await sender.WaitForAsync("after-the-boom@example.com"));

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Dispatcher_EnqueueNeverThrows_EvenBeforeItIsStarted()
    {
        // Le point important pour les appelants : mettre en file est une opération qui ne peut pas
        // échouer, quel que soit l'état de la file ou de l'expéditeur.
        var dispatcher = TestDispatcher.Create(new CapturingEmailSender { ThrowOnSend = true }, new InMemoryOutbox(), Logger);

        for (var i = 0; i < BackgroundEmailDispatcher.Capacity + 50; i++)
            dispatcher.Enqueue(Message($"flood-{i}@example.com"));
    }

    [Fact]
    public void Dispatcher_ReportsWhetherTheUnderlyingSenderIsConfigured()
    {
        var withMailer = TestDispatcher.Create(new CapturingEmailSender(), new InMemoryOutbox(), Logger);
        var withoutMailer = TestDispatcher.Create(
            new NoOpEmailSender(Logger, "aucun fournisseur configuré"), new InMemoryOutbox(), Logger);

        Assert.True(withMailer.IsConfigured);
        Assert.False(withoutMailer.IsConfigured);
    }

    // ---- EmailOptions ----

    [Fact]
    public void Links_CarryTheRawToken_AndUseTheConfiguredFrontEndBaseUrl()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["Email:AppBaseUrl"] = "https://agenthost.example.com/",
            ["Email:ResetPasswordPath"] = "/nouveau-mot-de-passe",
            ["Email:AcceptInvitationPath"] = "/rejoindre",
        });

        Assert.Equal(
            "https://agenthost.example.com/nouveau-mot-de-passe?token=jeton-brut",
            options.PasswordResetLink("jeton-brut"));
        Assert.Equal(
            "https://agenthost.example.com/rejoindre?token=jeton-brut",
            options.InvitationLink("jeton-brut"));
    }

    [Fact]
    public void Links_EscapeTheToken()
    {
        var options = Options(new Dictionary<string, string?>());

        Assert.Contains("token=a%2Bb%3Dc", options.PasswordResetLink("a+b=c"));
    }

    [Theory]
    [InlineData(null, EmailLanguage.French)]
    [InlineData("", EmailLanguage.French)]
    [InlineData("fr", EmailLanguage.French)]
    [InlineData("klingon", EmailLanguage.French)]
    [InlineData("en", EmailLanguage.English)]
    [InlineData("EN-GB", EmailLanguage.English)]
    public void Language_DefaultsToFrench_ForAnythingItDoesNotRecognise(string? configured, EmailLanguage expected)
    {
        var options = Options(new Dictionary<string, string?> { ["Email:Language"] = configured });

        Assert.Equal(expected, options.Language);
    }

    [Theory]
    [InlineData(null, SmtpSecurity.StartTls)]
    [InlineData("starttls", SmtpSecurity.StartTls)]
    // Une faute de frappe doit chiffrer, jamais dégrader le transport en clair.
    [InlineData("startls", SmtpSecurity.StartTls)]
    // « ssl » et « smtps » demandent le TLS implicite, ce qu'ils ont toujours voulu dire dans les
    // interfaces d'hébergeurs. Ils étaient auparavant ramenés à STARTTLS faute de savoir faire
    // autrement : le transport restait chiffré, mais un relais qui n'écoute qu'en 465 refusait la
    // connexion sans que le message d'erreur en dise la raison.
    [InlineData("ssl", SmtpSecurity.Ssl)]
    [InlineData("SMTPS", SmtpSecurity.Ssl)]
    [InlineData("none", SmtpSecurity.None)]
    public void SmtpSecurity_OnlyFallsBackToPlaintextWhenAskedExplicitly(string? configured, SmtpSecurity expected)
    {
        Assert.Equal(expected, SmtpOptions.ParseSecurity(configured));
    }

    [Fact]
    public void SmtpOptions_FallBackToUsableDefaults_WhenValuesAreUnparseable()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["Email:Smtp:Port"] = "not-a-port",
            ["Email:Smtp:TimeoutSeconds"] = "-3",
        });

        Assert.Equal(SmtpOptions.DefaultPort, options.Smtp.Port);
        Assert.Equal(SmtpOptions.DefaultTimeoutSeconds, options.Smtp.TimeoutSeconds);
    }

    // ---- EmailTemplates ----

    [Theory]
    [InlineData(EmailLanguage.French)]
    [InlineData(EmailLanguage.English)]
    public void PasswordResetMessage_CarriesTheLinkAndAValidityNotice(EmailLanguage language)
    {
        var message = EmailTemplates.PasswordReset(
            "user@example.com", "https://agenthost.example.com/reset-password?token=abc",
            TimeSpan.FromMinutes(30), language);

        Assert.Equal("user@example.com", message.To);
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));
        Assert.Contains("https://agenthost.example.com/reset-password?token=abc", message.TextBody);
        Assert.Contains("30 minutes", message.TextBody);
        Assert.Equal(EmailTemplates.PasswordResetKind, message.Kind);
    }

    [Theory]
    [InlineData(EmailLanguage.French, "7 jours")]
    [InlineData(EmailLanguage.English, "7 days")]
    public void InvitationMessage_CarriesTheLinkAndAValidityNotice(EmailLanguage language, string validity)
    {
        var message = EmailTemplates.Invitation(
            "invitee@example.com", "https://agenthost.example.com/accept-invitation?token=xyz",
            TimeSpan.FromDays(7), language);

        Assert.Equal("invitee@example.com", message.To);
        Assert.False(string.IsNullOrWhiteSpace(message.Subject));
        Assert.Contains("https://agenthost.example.com/accept-invitation?token=xyz", message.TextBody);
        Assert.Contains(validity, message.TextBody);
        Assert.Equal(EmailTemplates.InvitationKind, message.Kind);
    }

    [Fact]
    public void Messages_AreWrittenInTheChosenLanguage()
    {
        var french = EmailTemplates.PasswordReset("u@example.com", "https://x/y", TimeSpan.FromMinutes(30), EmailLanguage.French);
        var english = EmailTemplates.PasswordReset("u@example.com", "https://x/y", TimeSpan.FromMinutes(30), EmailLanguage.English);

        Assert.NotEqual(french.Subject, english.Subject);
        Assert.Contains("Bonjour", french.TextBody);
        Assert.Contains("Hello", english.TextBody);
    }

    // ---- Journalisation ----

    [Fact]
    public void RedactedAddresses_KeepTheDomainAndDropTheRest()
    {
        // Les journaux du flux de réinitialisation ne doivent pas devenir la liste des comptes que
        // l'endpoint refuse d'énumérer.
        Assert.Equal("a***@example.com", EmailAddressRedaction.Redact("alice@example.com"));
        Assert.Equal("***", EmailAddressRedaction.Redact("@example.com"));
        Assert.Equal("(vide)", EmailAddressRedaction.Redact(" "));
    }
}
