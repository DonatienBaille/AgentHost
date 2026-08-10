using System.Net;
using System.Net.Mail;
using Serilog;

namespace AgentHost.Api.Services.Email;

/// <summary>
/// Envoi SMTP réel, bâti sur <see cref="System.Net.Mail.SmtpClient"/>.
///
/// <b>Pourquoi le BCL plutôt que MailKit.</b> MailKit est la bibliothèque de référence en .NET et
/// Microsoft renvoie vers elle dans la documentation de <c>SmtpClient</c>. Elle n'est pourtant pas
/// retenue ici, et c'est un arbitrage assumé :
/// <list type="bullet">
/// <item>elle tire MimeKit et BouncyCastle, soit trois dépendances de plus dans un projet qui en
/// compte déjà beaucoup — pour une fonctionnalité que la majorité des déploiements laissera en
/// mode no-op ;</item>
/// <item>ce que le produit envoie est deux messages en texte brut, sans pièce jointe, sans
/// multipart, sans DKIM signé côté application : la surface de MimeKit ne sert à rien ici ;</item>
/// <item>le besoin réel (hôte, port, STARTTLS, authentification, expéditeur, délai d'expiration)
/// est intégralement couvert par le BCL, et <see cref="IEmailSender"/> isole le choix : passer à
/// MailKit plus tard, c'est ajouter une classe et une valeur de <c>Email:Provider</c>, sans
/// toucher à un seul appelant.</item>
/// </list>
///
/// <b>La limite qu'il faut connaître.</b> <c>SmtpClient.EnableSsl</c> fait du TLS <i>explicite</i>
/// (connexion en clair puis <c>STARTTLS</c>, ports 587 et 25) ; il ne sait pas faire de TLS
/// <i>implicite</i>, où la session est chiffrée dès l'ouverture de la socket (port 465, parfois
/// appelé SMTPS ou « SSL » dans les interfaces d'hébergeurs). C'est pourquoi
/// <see cref="SmtpSecurity"/> ne propose que <c>None</c> et <c>StartTls</c> : offrir une valeur
/// « Ssl » qui ne chiffrerait pas comme annoncé serait pire que de ne pas l'offrir. En pratique,
/// tous les relais courants (Amazon SES, SendGrid, Mailgun, Postmark, Gmail, la plupart des
/// serveurs d'entreprise) exposent 587/STARTTLS. Un déploiement qui n'a que du 465 doit
/// aujourd'hui passer par un relais local — ou justifier, à ce moment-là, l'ajout de MailKit.
///
/// <b>Le chiffrement n'est jamais dégradé silencieusement.</b> <c>Email:Smtp:Security</c> vaut
/// STARTTLS pour toute valeur qui n'est pas explicitement « none », et une configuration qui
/// envoie des identifiants sur un transport en clair est signalée au démarrage.
///
/// <b>Le délai d'expiration est appliqué ici, et non par <c>SmtpClient.Timeout</c>.</b> Cette
/// propriété est <i>ignorée</i> par <c>SendMailAsync</c> : elle ne gouverne que les surcharges
/// synchrones. Mesuré, pas déduit — un relais qui accepte la connexion puis se tait laissait
/// l'envoi pendre indéfiniment (voir <c>SmtpDeliveryTests</c>). Or le répartiteur de courriels est
/// un consommateur unique : un seul envoi bloqué arrêtait toute la remise de l'installation, sans
/// erreur et sans trace. Le délai est donc porté par un jeton d'annulation, que
/// <c>SendMailAsync</c>, lui, respecte.
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

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        // Un client par envoi. SmtpClient n'est ni réentrant ni sûr en concurrence, et le
        // répartiteur n'envoie qu'un message à la fois : mutualiser une instance n'apporterait
        // qu'un état partagé à protéger.
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.Security == SmtpSecurity.StartTls,
            Timeout = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            // Explicite : sans cela, SmtpClient peut tenter d'utiliser les identifiants du compte
            // de service qui exécute le processus, ce qui n'est jamais ce qu'on veut ici.
            UseDefaultCredentials = false,
            Credentials = _options.HasCredentials
                ? new NetworkCredential(_options.UserName, _options.Password)
                : null,
        };

        using var mail = new MailMessage
        {
            From = string.IsNullOrWhiteSpace(_fromName)
                ? new MailAddress(_fromAddress)
                : new MailAddress(_fromAddress, _fromName),
            Subject = message.Subject,
            Body = message.TextBody,
            IsBodyHtml = false,
        };
        mail.To.Add(new MailAddress(message.To));

        // Le jeton de l'appelant ET le délai, liés : le premier qui parle gagne.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await client.SendMailAsync(mail, deadline.Token);
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
