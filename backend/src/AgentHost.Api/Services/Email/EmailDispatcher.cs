using System.Threading.Channels;
using Serilog;

namespace AgentHost.Api.Services.Email;

/// <summary>
/// Remise d'un message « hors du chemin de réponse ».
///
/// Les appelants applicatifs passent par ici plutôt que d'attendre <see cref="IEmailSender"/>
/// directement. La raison est une propriété de sécurité, pas une optimisation : voir
/// <see cref="BackgroundEmailDispatcher"/>.
/// </summary>
public interface IEmailDispatcher
{
    /// <summary>Vrai quand le <see cref="IEmailSender"/> sous-jacent achemine réellement les messages.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Met un message en file. <b>Ne bloque pas, ne lève jamais, et n'attend pas l'acheminement.</b>
    /// Un appelant ne peut donc rien apprendre du sort du message, et un relais lent ou en panne ne
    /// peut ni ralentir ni faire échouer la requête en cours.
    /// </summary>
    void Enqueue(EmailMessage message);
}

/// <summary>
/// La file d'envoi : un canal en mémoire vidé par une tâche de fond.
///
/// <b>Pourquoi ne pas envoyer en ligne dans la requête.</b>
/// <c>POST /api/auth/password-reset/request</c> répond 202 que l'adresse ait un compte ou non, et
/// c'est délibéré : sans cela, l'endpoint — anonyme — devient un annuaire d'utilisateurs. Or un
/// envoi SMTP synchrone rétablirait exactement la distinction qu'on vient de retirer :
/// <list type="bullet">
/// <item><b>par le temps</b> — une connexion SMTP, sa poignée de main TLS et son aller-retour se
/// comptent en centaines de millisecondes ; un observateur chronomètre la réponse et lit
/// « compte existant / compte inexistant » dans la différence, sans même avoir besoin de
/// statistiques fines ;</item>
/// <item><b>par l'échec</b> — un relais qui refuse le destinataire, une boîte pleine, un
/// dépassement de délai : autant de manières de faire échouer une requête uniquement quand le
/// compte existe, ce qui est le même oracle sous une autre forme ;</item>
/// <item><b>par la charge</b> — un appelant qui inonde l'endpoint d'adresses inventées ne doit pas
/// pouvoir immobiliser les threads de traitement en attendant un tiers.</item>
/// </list>
/// Mettre en file coupe les trois : la mise en file est une écriture non bloquante dans un canal
/// en mémoire, et son coût est de plusieurs ordres de grandeur inférieur aux allers-retours vers
/// Postgres que la branche « compte existant » effectue de toute façon (recherche, révocation,
/// insertion du jeton, journal d'audit). Le temps de réponse reste dominé, comme avant ce lot, par
/// ces requêtes-là ; on n'ajoute aucune différence observable nouvelle.
///
/// <b>Ce que ce choix coûte.</b> Cette file est en mémoire et sans persistance : un arrêt brutal
/// du processus perd les messages non encore acheminés. C'est assumé pour un courriel
/// transactionnel dont l'utilisateur peut simplement redemander l'envoi — le jeton, lui, est déjà
/// en base. Une remise garantie demanderait une table d'attente (« outbox ») et sort du cadre de
/// ce lot ; le point d'extension est ici, sans changer un seul appelant.
/// </summary>
public sealed class BackgroundEmailDispatcher : BackgroundService, IEmailDispatcher
{
    /// <summary>
    /// Capacité de la file. Bornée pour qu'un flot de requêtes ne puisse pas faire enfler la
    /// mémoire du processus sans limite ; au-delà, les messages sont abandonnés avec une trace
    /// plutôt que de faire attendre l'appelant (qui est, rappelons-le, sur le chemin d'une réponse
    /// qui doit être insensible au contenu de la file).
    /// </summary>
    public const int Capacity = 512;

    private readonly Channel<EmailMessage> _channel;
    private readonly IEmailSender _sender;
    private readonly ILogger _logger;

    public BackgroundEmailDispatcher(IEmailSender sender, ILogger logger)
    {
        _sender = sender;
        _logger = logger;
        _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(Capacity)
        {
            // On abandonne l'écriture plutôt que de bloquer le producteur : Enqueue doit rester
            // non bloquant, c'est toute la raison d'être de cette file.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public bool IsConfigured => _sender.IsConfigured;

    /// <inheritdoc />
    public void Enqueue(EmailMessage message)
    {
        if (!_channel.Writer.TryWrite(message))
        {
            // File pleine (ou fermée, à l'arrêt du processus). On le dit, et la requête continue :
            // aucun appelant ne doit échouer parce qu'un courriel n'a pas pu être mis en file.
            _logger.Warning(
                "File d'envoi de courriels saturée ou fermée : message « {Kind} » abandonné pour {Recipient}",
                message.Kind, EmailAddressRedaction.Redact(message.To));
        }
    }

    /// <summary>
    /// Vide la file, un message à la fois. Chaque envoi est isolé : une exception ne fait pas
    /// tomber la boucle, sans quoi un seul destinataire invalide arrêterait tous les envois
    /// suivants du processus.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _sender.SendAsync(message, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Le corps n'est jamais journalisé : il contient le jeton brut.
                    _logger.Error(ex,
                        "Échec de l'envoi du courriel « {Kind} » vers {Recipient}",
                        message.Kind, EmailAddressRedaction.Redact(message.To));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal de l'hôte.
        }
    }
}
