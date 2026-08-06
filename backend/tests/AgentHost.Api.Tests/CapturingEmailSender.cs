using System.Diagnostics;
using AgentHost.Api.Services.Email;

namespace AgentHost.Api.Tests;

/// <summary>
/// Un <see cref="IEmailSender"/> qui garde les messages en mémoire.
///
/// C'est le seul outil de test acceptable ici : aucun test ne parle à un vrai serveur SMTP.
/// Ce qui mérite d'être vérifié est ce que ce dépôt écrit — quel message part, vers qui, avec
/// quel lien, et surtout ce qui se passe quand l'envoi échoue — pas la conformité du BCL au
/// protocole SMTP.
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _messages = new();

    /// <summary>Ce que rapporte <see cref="IEmailSender.IsConfigured"/>. Vrai par défaut : ce faux simule un mailer branché.</summary>
    public bool IsConfigured { get; init; } = true;

    /// <summary>
    /// Quand vrai, chaque envoi lève après avoir été enregistré — de quoi simuler un relais
    /// injoignable ou un destinataire refusé.
    /// </summary>
    public bool ThrowOnSend { get; init; }

    /// <summary>Copie instantanée des messages reçus.</summary>
    public IReadOnlyList<EmailMessage> Messages
    {
        get { lock (_messages) return _messages.ToList(); }
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        lock (_messages) _messages.Add(message);

        if (ThrowOnSend)
            throw new InvalidOperationException("Relais SMTP injoignable (simulé)");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Attend un message destiné à <paramref name="recipient"/>. L'acheminement est asynchrone par
    /// construction (voir BackgroundEmailDispatcher), donc un test qui veut le voir doit attendre —
    /// mais en filtrant sur le destinataire, jamais sur un simple « il y a au moins un message »,
    /// pour rester insensible à ce qu'un autre test a pu laisser dans la file.
    /// </summary>
    public async Task<EmailMessage?> WaitForAsync(string recipient, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);

        while (deadline.Elapsed < limit)
        {
            var match = Messages.FirstOrDefault(m =>
                string.Equals(m.To, recipient, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            await Task.Delay(25);
        }

        return null;
    }

    /// <summary>Vrai si un message a été adressé à <paramref name="recipient"/>.</summary>
    public bool HasMessageFor(string recipient) =>
        Messages.Any(m => string.Equals(m.To, recipient, StringComparison.OrdinalIgnoreCase));
}
