using System.Threading.Channels;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
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
/// <b>La file est désormais durable, mais pas au même endroit qu'on pourrait croire.</b> La mise en
/// file reste une écriture en mémoire — c'est elle qui est sur le chemin de la réponse, et la rendre
/// synchrone vers Postgres rouvrirait l'oracle par le temps ET par l'échec, exactement ce que les
/// paragraphes ci-dessus interdisent. La persistance a lieu <b>côté consommateur</b> : dès qu'il
/// sort un message du canal, il l'écrit dans <c>email_outbox</c> AVANT de tenter la remise.
///
/// Ce que cela donne, précisément :
/// <list type="bullet">
/// <item>un échec de remise est <b>réessayé</b>, avec un recul exponentiel qui survit au
/// redémarrage — c'est le gain principal, et l'ancienne file n'en offrait rien ;</item>
/// <item>un arrêt brutal ne perd plus que les messages encore dans le canal, soit la poignée de
/// millisecondes entre la mise en file et leur écriture ; l'ancienne fenêtre couvrait toute la
/// durée de vie du message ;</item>
/// <item>un message qu'on renonce à envoyer est <b>visible</b> en base avec sa dernière erreur, au
/// lieu de disparaître dans une ligne de journal.</item>
/// </list>
/// La fenêtre résiduelle est assumée et énoncée telle quelle : la supprimer demanderait d'écrire en
/// base dans la requête, ce qui coûterait la propriété de sécurité que cette file existe pour tenir.
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

    /// <summary>
    /// Reculs successifs entre deux tentatives. Le nombre d'entrées <b>est</b> le nombre de
    /// tentatives supplémentaires : au-delà, on renonce.
    ///
    /// La progression est volontairement lente à la fin. Un relais tombe rarement pour trente
    /// secondes ; il tombe pour une maintenance, et une dernière tentative une heure plus tard
    /// rattrape ce cas-là, qui est le seul que des tentatives rapprochées ne rattrapent pas.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// Durée pendant laquelle un message réclamé n'est pas repris par une autre réplique.
    ///
    /// Assez long pour couvrir une remise lente, assez court pour qu'un processus mort ne gèle pas
    /// le message plus que nécessaire.
    /// </summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cadence de relecture de la table, pour les messages en attente de réessai.
    ///
    /// C'est un filet, pas le chemin normal : un message qui vient d'être mis en file réveille la
    /// boucle immédiatement (voir <c>_wakeup</c>). Sans ce réveil, une réinitialisation de mot de
    /// passe partirait jusqu'à trente secondes après la demande — un délai que l'utilisateur
    /// attribuerait à une panne.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>Réveille la boucle de remise dès qu'un message est persisté.</summary>
    private readonly SemaphoreSlim _wakeup = new(0);

    private readonly Channel<EmailMessage> _channel;
    private readonly IEmailSender _sender;
    private readonly IEmailOutboxRepository _outbox;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// <see cref="ISecretsBroker"/> est enregistré <c>Scoped</c> ; ce répartiteur est un singleton.
    /// Le capturer au constructeur figerait une instance de portée pour la vie du processus — ce
    /// que la validation de portées de .NET refuse, et à raison. On ouvre donc une portée par
    /// usage, comme le fait déjà l'exécuteur de runs.
    /// </summary>
    private T Resolve<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    public BackgroundEmailDispatcher(
        IEmailSender sender, IEmailOutboxRepository outbox, IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _sender = sender;
        _outbox = outbox;
        _scopeFactory = scopeFactory;
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
    /// Deux boucles concurrentes, et elles ne font pas la même chose.
    ///
    /// La première vide le canal en mémoire et <b>persiste</b> ce qu'elle en sort : c'est le
    /// chemin rapide, celui du message qui vient d'être demandé. La seconde balaie la table pour
    /// les messages dus — réessais, et tout ce qu'un redémarrage a laissé derrière lui. Les fondre
    /// en une seule ferait attendre un message neuf jusqu'au prochain balayage.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            PersistIncomingAsync(stoppingToken),
            DeliverDueAsync(stoppingToken));
    }

    /// <summary>
    /// Sort les messages du canal et les écrit en base. N'envoie rien : la remise est le travail de
    /// l'autre boucle, qui la réessaiera si elle échoue.
    /// </summary>
    private async Task PersistIncomingAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var secrets = Resolve<ISecretsBroker>(scope);

                    await _outbox.InsertAsync(new OutboxEntry
                    {
                        Id = UlidGenerator.NewUlid(),
                        ToAddress = message.To,
                        Subject = message.Subject,
                        // Chiffré : le corps porte le jeton en clair, et le dépôt ne laisse jamais
                        // un jeton lisible au repos. Voir la migration 0013.
                        BodyEncrypted = secrets.Encrypt(message.TextBody),
                        Kind = message.Kind,
                        Attempts = 0,
                        NextAttemptAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                    }, stoppingToken);

                    // Persisté : la remise peut partir maintenant plutôt qu'au prochain battement.
                    _wakeup.Release();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Base injoignable : le message est perdu, mais la boucle survit. L'alternative
                    // — remettre dans le canal — ferait tourner en rond tant que la panne dure.
                    _logger.Error(ex,
                        "Impossible de persister le courriel « {Kind} » pour {Recipient} ; message perdu",
                        message.Kind, EmailAddressRedaction.Redact(message.To));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal de l'hôte.
        }
    }

    /// <summary>
    /// Remet les messages dus, un à la fois. Chaque envoi est isolé : une exception ne fait pas
    /// tomber la boucle, sans quoi un seul destinataire invalide arrêterait tous les envois
    /// suivants du processus.
    /// </summary>
    private async Task DeliverDueAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Tant qu'il y a du travail, on enchaîne : attendre le battement suivant entre deux
                // messages ferait de 30 s la cadence maximale de remise de l'installation.
                while (await DeliverOneAsync(stoppingToken)) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Le balayage de la file d'envoi a échoué");
            }

            try
            {
                // Le premier des deux qui parle : un message neuf, ou le battement de sécurité qui
                // ramasse les réessais dus et ce qu'un redémarrage a laissé.
                var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                var woken = _wakeup.WaitAsync(stoppingToken);

                if (await Task.WhenAny(tick, woken) == tick && !await tick) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Traite un message dû. Rend faux quand il n'y en a plus.
    ///
    /// <c>internal</c> pour les tests : attendre le battement de 30 s rendrait chaque test du
    /// réessai tributaire d'une demi-minute, et le rendrait surtout dépendant d'un ordonnancement
    /// qu'il ne contrôle pas. Même arbitrage que <c>RunDataJanitor.SweepAsync</c>.
    /// </summary>
    internal async Task<bool> DeliverOneAsync(CancellationToken ct)
    {
        var entry = await _outbox.ClaimNextDueAsync(DateTime.UtcNow, ClaimLease, ct);
        if (entry is null) return false;

        try
        {
            using var scope = _scopeFactory.CreateScope();

            await _sender.SendAsync(new EmailMessage
            {
                To = entry.ToAddress,
                Subject = entry.Subject,
                TextBody = Resolve<ISecretsBroker>(scope).Decrypt(entry.BodyEncrypted),
                Kind = entry.Kind,
            }, ct);

            // Remise réussie : la ligne disparaît. Un message acheminé n'a plus de raison
            // d'exister ici, et le conserver prolongerait l'exposition d'un secret pour rien.
            await _outbox.DeleteAsync(entry.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Le corps n'est jamais journalisé : il contient le jeton brut.
            _logger.Warning(ex,
                "Échec de l'envoi du courriel « {Kind} » vers {Recipient} (tentative {Attempt})",
                entry.Kind, EmailAddressRedaction.Redact(entry.ToAddress), entry.Attempts);

            // `Attempts` a déjà été incrémenté par la réclamation : la première tentative vaut 1.
            if (entry.Attempts > Backoff.Length)
                await _outbox.AbandonAsync(entry.Id, ex.Message, ct);
            else
                await _outbox.RescheduleAsync(
                    entry.Id, DateTime.UtcNow + Backoff[entry.Attempts - 1], ex.Message, ct);
        }

        return true;
    }
}
