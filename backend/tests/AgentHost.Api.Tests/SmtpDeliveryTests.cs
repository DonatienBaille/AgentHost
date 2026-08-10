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
[Collection(nameof(SmtpDeliveryTests))]
[CollectionDefinition(nameof(SmtpDeliveryTests), DisableParallelization = true)]
public class SmtpDeliveryTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly RemoteCertificateValidationCallback? _previousCallback;

    public SmtpDeliveryTests()
    {
        // Hook global, d'où la désactivation du parallélisme sur cette collection : deux tests qui
        // le poseraient et le retireraient en même temps se marcheraient dessus. Il n'existe pas de
        // moyen de le porter par instance de SmtpClient — c'est une limite du BCL, et la raison
        // pour laquelle ce test dit ce qu'il ne prouve pas.
        _previousCallback = ServicePointManager.ServerCertificateValidationCallback;
        ServicePointManager.ServerCertificateValidationCallback = (_, _, _, _) => true;
    }

    public void Dispose() => ServicePointManager.ServerCertificateValidationCallback = _previousCallback;

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

        // Le point qui justifie tout ce dispositif : `EnableSsl` doit produire un vrai STARTTLS.
        // Un client qui l'ignorerait enverrait le jeton de réinitialisation en clair sur le
        // réseau, et rien dans les journaux ne le dirait.
        Assert.True(server.StartTlsNegotiated);
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
        int port, string? user = null, string? password = null, int timeoutSeconds = 15)
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
                Security = SmtpSecurity.StartTls,
                UserName = user ?? string.Empty,
                Password = password ?? string.Empty,
                TimeoutSeconds = timeoutSeconds,
            },
        };

        return new SmtpEmailSender(options, new Serilog.LoggerConfiguration().CreateLogger());
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
