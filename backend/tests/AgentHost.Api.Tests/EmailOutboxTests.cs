using AgentHost.Api.Repositories;
using AgentHost.Api.Services.Email;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// La file d'envoi durable (dette identifiée, hors lots).
///
/// <b>Ce que ce lot corrige.</b> La file était un canal en mémoire : un échec de remise n'était
/// jamais réessayé, et un arrêt du processus perdait tout ce qui n'était pas encore parti. Pour une
/// invitation ou une réinitialisation, cela veut dire un utilisateur qui attend un courriel qui ne
/// viendra jamais — sans que rien nulle part ne le signale.
///
/// <b>Ce que ces tests épinglent en priorité.</b> Que la persistance ait lieu <b>avant</b> la
/// tentative de remise, sans quoi un échec ne laisserait rien à réessayer ; que le corps soit
/// chiffré au repos, parce qu'il contient le jeton en clair ; et qu'une remise réussie
/// <b>supprime</b> la ligne, parce qu'un message acheminé n'a plus de raison de garder un secret
/// en base.
/// </summary>
public class EmailOutboxTests
{
    private static readonly Serilog.ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();

    private static EmailMessage Message(string to = "destinataire@example.com") => new()
    {
        To = to,
        Subject = "Réinitialisation",
        TextBody = "https://agenthost.test/reset-password?token=abc",
        Kind = "password_reset",
    };

    [Fact]
    public async Task A_queued_message_is_persisted_before_any_delivery_is_attempted()
    {
        var outbox = new InMemoryOutbox();
        // L'expéditeur échoue : s'il fallait qu'il réussisse pour que la ligne existe, il n'y
        // aurait jamais rien à réessayer — c'est exactement le défaut d'origine.
        var dispatcher = TestDispatcher.Create(new CapturingEmailSender { ThrowOnSend = true }, outbox, Logger);
        await dispatcher.StartAsync(CancellationToken.None);

        dispatcher.Enqueue(Message());

        await WaitUntil(() => outbox.Inserted.Count == 1);
        await dispatcher.StopAsync(CancellationToken.None);

        var entry = Assert.Single(outbox.Inserted);
        Assert.Equal("destinataire@example.com", entry.ToAddress);
        Assert.Equal("password_reset", entry.Kind);
    }

    [Fact]
    public async Task The_body_never_touches_the_database_in_clear_text()
    {
        var outbox = new InMemoryOutbox();
        var dispatcher = TestDispatcher.Create(new CapturingEmailSender { ThrowOnSend = true }, outbox, Logger);
        await dispatcher.StartAsync(CancellationToken.None);

        dispatcher.Enqueue(Message());
        await WaitUntil(() => outbox.Inserted.Count == 1);
        await dispatcher.StopAsync(CancellationToken.None);

        // Le corps EST le lien de réinitialisation. Le persister tel quel ferait d'une ligne en
        // attente une prise de contrôle de compte utilisable par quiconque lit la table — ce que
        // le stockage par empreinte des jetons existe précisément pour empêcher.
        //
        // Ce qu'un double peut prouver — et ce qu'il ne peut pas. Il prouve que le corps PASSE par
        // le courtier de secrets à l'écriture : les octets stockés ne sont pas ceux du message. La
        // force du chiffrement, elle, se prouve ailleurs, sur le vrai courtier
        // (`SecretsKeyRotationTests`) ; l'affirmer ici contre un double réversible serait une
        // assertion qui ne teste que le double.
        var stored = outbox.Inserted[0].BodyEncrypted;
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(Message().TextBody), stored);
        Assert.Equal(Message().TextBody, new ReversibleSecretsBroker().Decrypt(stored));
    }

    [Fact]
    public async Task A_successful_delivery_removes_the_row_rather_than_marking_it()
    {
        var outbox = new InMemoryOutbox();
        var sender = new CapturingEmailSender();
        var dispatcher = TestDispatcher.Create(sender, outbox, Logger);
        await dispatcher.StartAsync(CancellationToken.None);

        dispatcher.Enqueue(Message());

        Assert.NotNull(await sender.WaitForAsync("destinataire@example.com"));
        await WaitUntil(() => outbox.Deleted.Count == 1);
        await dispatcher.StopAsync(CancellationToken.None);

        // Conserver la ligne prolongerait l'exposition d'un secret sans rien apporter : ce qui a
        // été envoyé est déjà tracé côté journal, sans le corps.
        Assert.Empty(outbox.Snapshot());
    }

    [Fact]
    public async Task The_message_is_decrypted_on_its_way_out()
    {
        var outbox = new InMemoryOutbox();
        var sender = new CapturingEmailSender();
        var dispatcher = TestDispatcher.Create(sender, outbox, Logger);
        await dispatcher.StartAsync(CancellationToken.None);

        dispatcher.Enqueue(Message());
        var delivered = await sender.WaitForAsync("destinataire@example.com");
        await dispatcher.StopAsync(CancellationToken.None);

        // Chiffré au repos, en clair pour le relais : un aller-retour raté enverrait un corps
        // illisible, et l'utilisateur recevrait un message vide de sens.
        Assert.NotNull(delivered);
        Assert.Contains("token=abc", delivered!.TextBody);
    }

    [Fact]
    public async Task A_failed_delivery_is_rescheduled_rather_than_lost()
    {
        var outbox = new InMemoryOutbox();
        var sender = new CapturingEmailSender { ThrowOnSend = true };
        var dispatcher = TestDispatcher.Create(sender, outbox, Logger);

        await outbox.InsertAsync(Entry("id-1"));
        Assert.True(await dispatcher.DeliverOneAsync(CancellationToken.None));

        var entry = Assert.Single(outbox.Snapshot());
        Assert.Null(entry.AbandonedAt);
        Assert.NotNull(entry.LastError);
        // Repoussée dans le futur : c'est ce report, persisté, qui survit au redémarrage — l'ancien
        // répartiteur n'avait rien de tel et perdait le message à la première erreur.
        Assert.True(entry.NextAttemptAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task A_message_is_abandoned_once_its_attempts_are_exhausted_and_stays_visible()
    {
        var outbox = new InMemoryOutbox();
        var dispatcher = TestDispatcher.Create(new CapturingEmailSender { ThrowOnSend = true }, outbox, Logger);

        await outbox.InsertAsync(Entry("id-1"));

        // Une de plus que le nombre de reculs : la dernière doit faire renoncer.
        for (var i = 0; i < 10; i++)
        {
            outbox.MakeDue("id-1");
            if (!await dispatcher.DeliverOneAsync(CancellationToken.None)) break;
            if (outbox.Abandoned.Count > 0) break;
        }

        Assert.Contains("id-1", outbox.Abandoned);

        // Abandonnée mais TOUJOURS LÀ : un message qu'on renonce à envoyer doit rester visible avec
        // sa raison. Le supprimer le ferait disparaître dans une ligne de journal que personne ne
        // relit, et l'utilisateur attendrait un courriel dont plus rien ne dit qu'il a existé.
        var entry = Assert.Single(outbox.Snapshot());
        Assert.NotNull(entry.AbandonedAt);
        Assert.NotNull(entry.LastError);
    }

    [Fact]
    public async Task An_abandoned_message_is_never_retried_again()
    {
        var outbox = new InMemoryOutbox();
        var dispatcher = TestDispatcher.Create(new CapturingEmailSender { ThrowOnSend = true }, outbox, Logger);

        await outbox.InsertAsync(Entry("id-1"));
        await outbox.AbandonAsync("id-1", "définitif");

        // Sans ce filtre, un message abandonné serait relu à chaque battement, pour toujours.
        Assert.False(await dispatcher.DeliverOneAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Messages_left_by_a_previous_process_are_picked_up_on_start()
    {
        var outbox = new InMemoryOutbox();
        // Une ligne qu'aucun canal en mémoire ne connaît : c'est exactement ce qu'un arrêt brutal
        // laisse derrière lui, et ce que l'ancienne file perdait définitivement.
        await outbox.InsertAsync(Entry("orphelin"));

        var sender = new CapturingEmailSender();
        var dispatcher = TestDispatcher.Create(sender, outbox, Logger);
        await dispatcher.StartAsync(CancellationToken.None);

        Assert.NotNull(await sender.WaitForAsync("destinataire@example.com"));
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Nothing_due_is_a_no_op_rather_than_an_error()
    {
        var outbox = new InMemoryOutbox();
        var dispatcher = TestDispatcher.Create(new CapturingEmailSender(), outbox, Logger);

        Assert.False(await dispatcher.DeliverOneAsync(CancellationToken.None));
    }

    // ---- helpers ----

    private static OutboxEntry Entry(string id) => new()
    {
        Id = id,
        ToAddress = "destinataire@example.com",
        Subject = "Réinitialisation",
        BodyEncrypted = new ReversibleSecretsBroker().Encrypt("https://agenthost.test/reset?token=abc"),
        Kind = "password_reset",
        Attempts = 0,
        NextAttemptAt = DateTime.UtcNow.AddSeconds(-1),
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>Attend une condition, ou échoue — jamais une attente fixe qui rendrait le test lent et fragile.</summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail($"La condition n'a pas été satisfaite en {timeoutMs} ms.");
    }
}
