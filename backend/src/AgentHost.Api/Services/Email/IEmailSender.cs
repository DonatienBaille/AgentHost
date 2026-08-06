using Serilog;

namespace AgentHost.Api.Services.Email;

/// <summary>
/// Un message sortant. Texte brut uniquement : les deux courriels transactionnels du produit
/// (réinitialisation, invitation) tiennent en quelques lignes et un lien, et une version HTML
/// n'apporterait ici que des soucis de rendu et de filtrage anti-spam.
/// </summary>
public sealed record EmailMessage
{
    /// <summary>Destinataire unique. Aucun flux du produit n'a besoin de plusieurs destinataires.</summary>
    public required string To { get; init; }

    /// <summary>Sujet.</summary>
    public required string Subject { get; init; }

    /// <summary>Corps en texte brut. Contient le lien, donc le jeton brut : ne jamais journaliser.</summary>
    public required string TextBody { get; init; }

    /// <summary>
    /// Étiquette de nature du message (« password-reset », « invitation »), destinée aux journaux.
    /// Elle ne contient ni destinataire ni jeton, ce qui permet de tracer un envoi sans écrire de
    /// secret ni d'adresse dans les logs.
    /// </summary>
    public required string Kind { get; init; }
}

/// <summary>
/// L'abstraction d'envoi de courriel. Une seule opération, volontairement.
///
/// <see cref="IsConfigured"/> existe parce que deux appelants doivent adapter leur comportement
/// selon qu'un mailer est branché ou non — notamment <see cref="InvitationService"/>, qui continue
/// de rendre le jeton brut à l'appelant quand rien n'est configuré, sous peine de casser le seul
/// chemin d'invitation qui fonctionne aujourd'hui.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Vrai quand cette implémentation achemine réellement les messages. Faux pour
    /// <see cref="NoOpEmailSender"/>.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Achemine un message. Peut lever : l'appelant normal est
    /// <see cref="BackgroundEmailDispatcher"/>, qui isole ces échecs du chemin de requête.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// L'implémentation par défaut : elle n'envoie rien et ne lève jamais.
///
/// C'est le comportement historique du dépôt rendu explicite. Avant ce lot, un déploiement sans
/// mailer perdait silencieusement le jeton de réinitialisation ; désormais il produit une trace de
/// niveau Warning qui dit ce qui n'est <b>pas</b> parti et pourquoi. Aucun comportement observable
/// d'un appelant ne change : c'est ce qui garantit qu'un déploiement non configuré fonctionne
/// exactement comme avant.
/// </summary>
public sealed class NoOpEmailSender : IEmailSender
{
    private readonly ILogger _logger;
    private readonly string _reason;

    /// <param name="logger">Journal.</param>
    /// <param name="reason">
    /// Pourquoi rien n'est envoyé — « aucun fournisseur configuré », « Email:Smtp:Host est vide »,
    /// etc. Cette phrase se retrouve telle quelle dans le Warning, pour qu'un exploitant sache
    /// quoi corriger sans lire le code.
    /// </param>
    public NoOpEmailSender(ILogger logger, string reason)
    {
        _logger = logger;
        _reason = reason;
    }

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        // Ni le destinataire complet ni le corps ne sont journalisés : le corps contient le jeton
        // brut, et une adresse en clair dans les journaux du flux de réinitialisation dirait
        // exactement ce que l'endpoint refuse de dire (quels comptes existent).
        _logger.Warning(
            "Courriel « {Kind} » NON envoyé (destinataire {Recipient}) : aucun mailer n'achemine les messages ({Reason}). " +
            "Configurez la section Email pour activer l'envoi.",
            message.Kind, EmailAddressRedaction.Redact(message.To), _reason);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Réduction d'une adresse à ce qui est utile en exploitation sans être révélateur.
/// </summary>
public static class EmailAddressRedaction
{
    /// <summary>
    /// Ne garde que la première lettre de la partie locale et le domaine :
    /// <c>alice@example.com</c> devient <c>a***@example.com</c>. Assez pour diagnostiquer un
    /// domaine qui rejette tout, pas assez pour reconstituer un annuaire d'utilisateurs à partir
    /// des journaux.
    /// </summary>
    public static string Redact(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "(vide)";

        var at = address.IndexOf('@');
        if (at <= 0) return "***";

        return $"{address[0]}***{address[at..]}";
    }
}
