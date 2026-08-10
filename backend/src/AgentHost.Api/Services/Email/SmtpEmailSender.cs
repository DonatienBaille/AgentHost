using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using Serilog;

namespace AgentHost.Api.Services.Email;

/// <summary>
/// Envoi SMTP réel, bâti sur MailKit.
///
/// <b>Pourquoi MailKit, alors que le BCL suffisait.</b> Cette classe reposait sur
/// <c>System.Net.Mail.SmtpClient</c>, et l'arbitrage était défendable : deux messages en texte
/// brut, sans pièce jointe ni multipart, n'ont besoin de rien de plus, et éviter trois dépendances
/// dans un projet qui en compte déjà beaucoup a de la valeur. Ce raisonnement énonçait cependant sa
/// propre condition de révision : <i>« un déploiement qui n'a que du 465 doit passer par un relais
/// local — ou justifier, à ce moment-là, l'ajout de MailKit »</i>.
///
/// C'est ce moment. <c>SmtpClient.EnableSsl</c> ne fait que du TLS <b>explicite</b> (connexion en
/// clair puis <c>STARTTLS</c>, ports 587 et 25). Le TLS <b>implicite</b> — session chiffrée dès
/// l'ouverture de la socket, port 465, appelé « SSL » ou SMTPS chez la plupart des hébergeurs — lui
/// est structurellement hors de portée. Ce n'était donc pas une lacune à combler par du code
/// applicatif : la bibliothèque ne sait pas ouvrir ce type de connexion. Le contournement (installer
/// un relais local qui parle 587 côté application et 465 côté sortie) revient à demander à chaque
/// exploitant d'administrer un serveur de messagerie pour compenser un choix de dépendance.
///
/// L'interface <see cref="IEmailSender"/> avait été conçue pour absorber exactement ce changement,
/// et elle l'a fait : aucun appelant ne bouge, aucune option ne change de nom, et
/// <see cref="SmtpSecurity"/> gagne une valeur qui manquait.
///
/// <b>Le chiffrement n'est jamais dégradé silencieusement.</b> <c>Email:Smtp:Security</c> chiffre
/// pour toute valeur qui n'est pas explicitement « none », et une configuration qui enverrait des
/// identifiants sur un transport en clair est signalée au démarrage.
///
/// <b>Le délai d'expiration reste porté par un jeton d'annulation.</b> C'était indispensable avec le
/// BCL, dont la propriété <c>Timeout</c> est ignorée par <c>SendMailAsync</c> — mesuré, pas déduit
/// (voir <c>SmtpDeliveryTests</c>). MailKit, lui, honore son propre <c>Timeout</c>, mais le jeton
/// est conservé : il couvre la connexion, la négociation TLS et l'authentification d'un seul tenant,
/// là où <c>Timeout</c> s'applique par opération de socket. Le répartiteur de courriels est un
/// consommateur unique — un seul envoi bloqué arrête toute la remise de l'installation.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly ILogger _logger;

    public SmtpEmailSender(EmailOptions options, ILogger logger)
    {
        _options = options.Smtp;
        _fromAddress = options.FromAddress;
        _fromName = options.FromName;
        _logger = logger;

        if (_options.Security == SmtpSecurity.None && _options.HasCredentials)
        {
            _logger.Warning(
                "Email:Smtp:Security vaut « none » alors que des identifiants sont configurés : " +
                "le mot de passe SMTP transitera en clair. À ne faire que vers un relais local de confiance.");
        }
    }

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <summary>
    /// Remplace la validation du certificat du relais. <b>Réservé aux tests</b>, et interne pour
    /// cette raison : aucune configuration ne permet de la désactiver en exploitation, parce qu'un
    /// interrupteur « accepter n'importe quel certificat » finit toujours par se retrouver activé
    /// en production « le temps de déboguer ».
    ///
    /// Elle existe parce que les tests confrontent l'envoi à un vrai serveur TLS muni d'un
    /// certificat auto-signé. La version précédente y parvenait par
    /// <c>ServicePointManager.ServerCertificateValidationCallback</c>, un crochet <i>global au
    /// processus</i> qui obligeait à désactiver le parallélisme de toute une collection de tests.
    /// MailKit porte le sien par instance : le crochet ne déborde plus sur rien.
    /// </summary>
    internal System.Net.Security.RemoteCertificateValidationCallback? CertificateValidationOverride { get; set; }

    /// <summary>
    /// La correspondance entre notre configuration et les options de connexion de MailKit.
    ///
    /// <c>SslOnConnect</c> et <c>StartTls</c> sont volontairement les variantes <b>exigeantes</b> :
    /// leurs équivalents « Auto » et <c>StartTlsWhenAvailable</c> retombent en clair si le serveur
    /// n'annonce pas TLS. Un relais mal configuré, ou une attaque par suppression de la capacité
    /// STARTTLS, ferait alors partir les identifiants et le jeton de réinitialisation en clair —
    /// sans erreur, et sans que rien ne le signale. Une configuration qui demande du chiffrement
    /// doit échouer plutôt que de se dégrader.
    /// </summary>
    private SecureSocketOptions SocketOptions => _options.Security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.Ssl => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTls,
    };

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var mail = new MimeMessage
        {
            Subject = message.Subject,
            Body = new TextPart(TextFormat.Plain) { Text = message.TextBody },
        };
        mail.From.Add(new MailboxAddress(_fromName ?? string.Empty, _fromAddress));
        mail.To.Add(MailboxAddress.Parse(message.To));

        // Un client par envoi. Le répartiteur n'envoie qu'un message à la fois : mutualiser une
        // connexion n'apporterait qu'un état partagé à protéger et une session à revalider.
        using var client = new SmtpClient();
        client.Timeout = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds;

        if (CertificateValidationOverride is not null)
            client.ServerCertificateValidationCallback = CertificateValidationOverride;

        // Le jeton de l'appelant ET le délai, liés : le premier qui parle gagne. Il couvre la
        // séquence entière — connexion, TLS, authentification, envoi — et non chaque opération
        // prise isolément.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, SocketOptions, deadline.Token);

            if (_options.HasCredentials)
                await client.AuthenticateAsync(_options.UserName, _options.Password, deadline.Token);

            await client.SendAsync(mail, deadline.Token);
            await client.DisconnectAsync(true, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Distinguer les deux causes : une annulation par l'appelant est un arrêt normal du
            // service, un dépassement de délai est une panne du relais. Les confondre rendrait
            // l'extinction du processus indiscernable d'un incident.
            throw new TimeoutException(
                $"Le relais SMTP {_options.Host}:{_options.Port} n'a pas répondu en " +
                $"{_options.TimeoutSeconds} s.");
        }

        // Ni le sujet ni le corps : le corps contient le jeton brut.
        _logger.Information("Courriel « {Kind} » remis au relais SMTP pour {Recipient}",
            message.Kind, EmailAddressRedaction.Redact(message.To));
    }
}
