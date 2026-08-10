using System.Net;
using System.Net.Mail;
using System.Net.Security;
using AgentHost.Api.Services.Email;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// <c>SmtpEmailSender</c> confronté à un vrai dialogue SMTP (dette identifiée, hors lots).
///
/// <b>Ce que ces tests changent.</b> Jusqu'ici, la sélection du fournisseur et la construction du
/// client étaient testées ; le protocole, lui, reposait sur le contrat documenté du BCL et sur rien
/// d'autre. La feuille de route le disait explicitement : « n'a jamais parlé à un vrai serveur
/// SMTP ». C'est le genre de dette qui ne coûte rien jusqu'au jour du premier envoi en production.
///
/// Ils exercent maintenant le chemin complet contre <see cref="FakeSmtpServer"/> : EHLO, montée en
/// TLS par STARTTLS, authentification, enveloppe, corps. La montée en TLS est la raison d'être de
/// ce dispositif — c'est la seule partie qu'aucun double ne peut simuler, et la seule où un
/// <c>EnableSsl</c> mal câblé passerait inaperçu.
///
/// <b>Ce qu'ils ne prouvent pas.</b> Le certificat est auto-signé et sa validation est désactivée
/// pour la durée du test. La négociation est donc réellement exercée, la vérification de chaîne ne
/// l'est pas. Un relais public reste à confronter une fois — même statut que Podman, S3 et HIBP.
/// </summary>
public class SmtpDeliveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_message_reaches_the_relay_with_its_envelope_and_body_intact()
    {
        await using var server = new FakeSmtpServer();
        var sender = CreateSender(server.Port);

        await sender.SendAsync(new EmailMessage
        {
            To = "destinataire@example.com",
            Subject = "Réinitialisation de mot de passe",
            TextBody = "Suivez ce lien : https://agenthost.test/reset-password?token=abc",
            Kind = "password_reset",
        });

        var received = await server.WaitForMessageAsync(Timeout);

        // L'enveloppe SMTP, qui est ce que le relais route — distincte des en-têtes du message.
        Assert.Equal("agenthost@example.com", received.From);
        Assert.Equal("destinataire@example.com", Assert.Single(received.Recipients));

        // Le corps est arrivé entier, une fois décodé : un lien tronqué produirait un jeton
        // invalide, et une réinitialisation qui échoue sans rien dire. `MailMessage` encode le
        // corps dès qu'il contient un accent — ce que fait tout message en français — donc la
        // question ne se pose qu'après décodage.
        Assert.Contains("token=abc", received.DecodedBody);
        Assert.Contains("https://agenthost.test/reset-password", received.DecodedBody);
        Assert.True(received.HasHeader("To", "destinataire@example.com"));
    }

    [Fact]
    public async Task The_session_is_actually_upgraded_to_tls()
    {
        await using var server = new FakeSmtpServer();
        var sender = CreateSender(server.Port);

        await sender.SendAsync(Message());
        await server.WaitForMessageAsync(Timeout);

        // Le point qui justifie tout ce dispositif : le mode STARTTLS doit produire un vrai
        // STARTTLS. Un client qui l'ignorerait enverrait le jeton de réinitialisation en clair sur
        // le réseau, et rien dans les journaux ne le dirait.
        Assert.True(server.StartTlsNegotiated);
    }

    /// <summary>
    /// Le port 465, c'est-à-dire la dette que ce mode vient solder.
    ///
    /// Le TLS implicite chiffre dès l'ouverture de la socket : il n'y a aucun échange en clair, pas
    /// même la bannière. <c>System.Net.Mail.SmtpClient</c> en était structurellement incapable — il
    /// ne savait faire que du STARTTLS — et un relais qui n'écoute qu'en 465 refusait donc la
    /// connexion. Le contournement documenté était d'installer un relais local, c'est-à-dire de
    /// demander à chaque exploitant d'administrer un serveur de messagerie pour compenser un choix
    /// de dépendance.
    /// </summary>
    [Fact]
    public async Task A_relay_that_only_speaks_implicit_tls_is_reachable()
    {
        await using var server = new FakeSmtpServer(implicitTls: true);
        var sender = CreateSender(server.Port, security: SmtpSecurity.Ssl);

        await sender.SendAsync(Message());
        var received = await server.WaitForMessageAsync(Timeout);

        Assert.True(server.ImplicitTlsNegotiated);

        // Et pas seulement connecté : le message doit être arrivé entier. Une connexion qui aboutit
        // sans que rien ne soit remis serait une régression plus discrète qu'un refus.
        Assert.True(received.HasHeader("To", "destinataire@example.com"));

        // Le serveur n'a rien annoncé de tel, et le client n'avait rien à monter.
        Assert.False(server.StartTlsNegotiated);
    }

    /// <summary>
    /// Le pendant du test précédent : demander du TLS implicite à un relais qui n'en fait pas doit
    /// <b>échouer</b>, jamais retomber en clair.
    ///
    /// C'est ce qui distingue <c>SslOnConnect</c> des variantes « Auto » de MailKit, et le choix
    /// est délibéré : une négociation qui se dégrade silencieusement ferait partir les identifiants
    /// et le jeton de réinitialisation en clair, sans erreur et sans trace.
    /// </summary>
    [Fact]
    public async Task Implicit_tls_never_falls_back_to_a_cleartext_session()
    {
        // Un serveur qui parle en clair, alors que la configuration exige du TLS dès la connexion.
        await using var server = new FakeSmtpServer(offerStartTls: false, requireAuth: false);
        var sender = CreateSender(server.Port, security: SmtpSecurity.Ssl, timeoutSeconds: 5);

        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(Message()));

        Assert.False(server.ImplicitTlsNegotiated);
    }

    [Fact]
    public async Task The_configured_credentials_are_presented_to_the_relay()
    {
        await using var server = new FakeSmtpServer();
        var sender = CreateSender(server.Port, user: "apikey", password: "s3cr3t");

        await sender.SendAsync(Message());
        await server.WaitForMessageAsync(Timeout);

        // Des identifiants configurés mais jamais présentés donneraient un relais qui refuse, avec
        // un message d'erreur qui accuse le mot de passe.
        Assert.NotNull(server.Credentials);
        Assert.Equal("apikey", server.Credentials!.Value.User);
        Assert.Equal("s3cr3t", server.Credentials!.Value.Password);
    }

    [Fact]
    public async Task Without_credentials_no_authentication_is_attempted()
    {
        await using var server = new FakeSmtpServer(requireAuth: false);
        var sender = CreateSender(server.Port);

        await sender.SendAsync(Message());
        await server.WaitForMessageAsync(Timeout);

        // Un relais local ouvert n'annonce pas AUTH ; tenter de s'authentifier quand même ferait
        // échouer un envoi qui devait passer.
        Assert.Null(server.Credentials);
    }

    [Fact]
    public async Task A_relay_that_never_answers_fails_within_the_configured_timeout()
    {
        // Une socket qui accepte et se tait : le pire cas pour un envoi, parce qu'il ne produit
        // aucune erreur — seulement une attente. Sans délai d'expiration, il immobiliserait le fil
        // du répartiteur de courriels pour toujours.
        using var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        var port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;

        var sender = CreateSender(port, timeoutSeconds: 2);
        var started = DateTime.UtcNow;

        // Le `Task.WhenAny` est une ceinture de sécurité, pas la mesure : sans lui, un envoi qui
        // n'abandonne jamais bloquerait la suite entière au lieu de faire échouer ce test.
        var send = Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(Message()));
        var guard = Task.Delay(TimeSpan.FromSeconds(20));

        Assert.True(await Task.WhenAny(send, guard) == send,
            "L'envoi n'a pas abandonné au bout de 20 s alors que le délai configuré était de 2 s : " +
            "le délai d'expiration n'est pas appliqué.");

        var elapsed = DateTime.UtcNow - started;
        Assert.True(elapsed < TimeSpan.FromSeconds(15),
            $"L'envoi a mis {elapsed.TotalSeconds:0.0} s à abandonner ; le délai configuré était de 2 s.");
    }

    [Fact]
    public async Task A_refused_recipient_surfaces_as_an_error_rather_than_a_silent_drop()
    {
        // Rien n'écoute sur ce port : l'échec doit remonter à l'appelant. C'est le répartiteur qui
        // décide ensuite de ne pas le propager à la requête HTTP — mais il doit le savoir.
        var sender = CreateSender(FreePort());

        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(Message()));
    }

    // ---- helpers ----

    private static EmailMessage Message() => new()
    {
        To = "destinataire@example.com",
        Subject = "Sujet",
        TextBody = "Corps",
        Kind = "invitation",
    };

    private static SmtpEmailSender CreateSender(
        int port, string? user = null, string? password = null, int timeoutSeconds = 15,
        SmtpSecurity security = SmtpSecurity.StartTls)
    {
        var options = new EmailOptions
        {
            Provider = EmailOptions.SmtpProvider,
            FromAddress = "agenthost@example.com",
            FromName = "Agent Host",
            Smtp = new SmtpOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Security = security,
                UserName = user ?? string.Empty,
                Password = password ?? string.Empty,
                TimeoutSeconds = timeoutSeconds,
            },
        };

        return new SmtpEmailSender(options, new Serilog.LoggerConfiguration().CreateLogger())
        {
            // Le certificat du serveur de test est auto-signé. Le crochet est porté par CETTE
            // instance : il ne déborde pas sur les autres tests, contrairement au crochet global du
            // BCL qu'il remplace — c'est ce qui permet de laisser cette collection s'exécuter en
            // parallèle des autres.
            CertificateValidationOverride = (_, _, _, _) => true,
        };
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
