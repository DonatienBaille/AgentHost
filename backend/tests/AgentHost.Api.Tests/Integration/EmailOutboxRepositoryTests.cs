using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Le SQL de la file d'envoi durable, contre la vraie base.
///
/// <b>Ce qui ne se vérifie pas à la lecture.</b> La réclamation tient en une instruction —
/// <c>UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING</c> — dont toute la
/// valeur est atomique. Deux répartiteurs qui réclament en même temps ne doivent pas partir avec le
/// même message : un lien de réinitialisation dupliqué dans deux courriels est une confusion
/// gratuite pour l'utilisateur, et le signe d'une file qu'on ne peut pas répliquer.
///
/// Le second point est le bail : le compteur de tentatives monte à la <b>réclamation</b>, pas à
/// l'échec, et la date de prochaine tentative est repoussée avant même l'envoi. Sans cela, un
/// processus qui meurt pendant la remise laisserait le message réservé pour toujours.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class EmailOutboxRepositoryTests
{
    private readonly AgentHostApiFactory _factory;

    public EmailOutboxRepositoryTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_inserted_message_comes_back_intact()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddSeconds(-1));

        await repository.InsertAsync(entry);
        var claimed = await ClaimMineAsync(repository, entry.Id);

        Assert.NotNull(claimed);
        Assert.Equal(entry.ToAddress, claimed!.ToAddress);
        Assert.Equal(entry.Subject, claimed.Subject);
        Assert.Equal(entry.Kind, claimed.Kind);
        // Les octets chiffrés doivent traverser BYTEA sans altération : une conversion d'encodage
        // ici rendrait le corps indéchiffrable, et le message partirait vide.
        Assert.Equal(entry.BodyEncrypted, claimed.BodyEncrypted);
    }

    [Fact]
    public async Task Claiming_increments_the_attempt_count_and_pushes_the_next_attempt_out()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddSeconds(-1));
        await repository.InsertAsync(entry);

        var claimed = await ClaimMineAsync(repository, entry.Id);

        Assert.Equal(1, claimed!.Attempts);
        // Le bail : si le processus meurt maintenant, la ligne redevient candidate à son expiration
        // au lieu de rester réservée pour toujours.
        Assert.True(claimed.NextAttemptAt > DateTime.UtcNow.AddMinutes(3));
    }

    /// <summary>Le test qui compte : deux répartiteurs, un seul envoi.</summary>
    [Fact]
    public async Task Two_dispatchers_racing_on_the_same_message_do_not_both_get_it()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddSeconds(-1));
        await repository.InsertAsync(entry);

        var now = DateTime.UtcNow;
        var claims = await Task.WhenAll(
            repository.ClaimNextDueAsync(now, TimeSpan.FromMinutes(5)),
            repository.ClaimNextDueAsync(now, TimeSpan.FromMinutes(5)));

        // Chacune a pu réclamer un message — mais pas LE MÊME. `SKIP LOCKED` fait passer la seconde
        // au suivant plutôt que de la faire attendre, et c'est ce qui rend la file réplicable.
        var mine = claims.Where(c => c?.Id == entry.Id).ToList();
        Assert.Single(mine);
    }

    [Fact]
    public async Task A_message_that_is_not_due_yet_is_not_claimed()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddHours(2));
        await repository.InsertAsync(entry);

        // Le recul exponentiel n'a de sens que si la date est respectée : sans ce filtre, un
        // message en échec serait réessayé en boucle serrée contre un relais déjà en peine.
        Assert.Null(await ClaimMineAsync(repository, entry.Id));
    }

    [Fact]
    public async Task Delivery_deletes_the_row_and_leaves_nothing_behind()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddSeconds(-1));
        await repository.InsertAsync(entry);

        await repository.DeleteAsync(entry.Id);

        // Un message acheminé n'a plus de raison de garder un secret en base.
        Assert.Equal(0, await CountRowsAsync(entry.Id));
    }

    [Fact]
    public async Task An_abandoned_message_stays_visible_and_is_never_claimed_again()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddSeconds(-1));
        await repository.InsertAsync(entry);

        await repository.AbandonAsync(entry.Id, "relais injoignable");

        // Toujours en base, avec sa raison : un message abandonné en silence est un message dont
        // personne n'apprend jamais l'existence.
        Assert.Equal(1, await CountRowsAsync(entry.Id));
        Assert.Null(await ClaimMineAsync(repository, entry.Id));
    }

    [Fact]
    public async Task Rescheduling_records_the_reason_and_the_new_date()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddSeconds(-1));
        await repository.InsertAsync(entry);

        var next = DateTime.UtcNow.AddMinutes(10);
        await repository.RescheduleAsync(entry.Id, next, "550 boîte pleine");

        using var db = Connection();
        var row = await db.QuerySingleAsync<(DateTime NextAttemptAt, string LastError)>(
            "SELECT next_attempt_at, last_error FROM email_outbox WHERE id = @Id", new { Id = entry.Id });

        Assert.Equal(next, row.NextAttemptAt, TimeSpan.FromSeconds(1));
        // La raison est ce qu'un exploitant lit pour savoir POURQUOI un message ne part pas.
        Assert.Contains("boîte pleine", row.LastError);
    }

    [Fact]
    public async Task An_oversized_error_message_cannot_break_the_write()
    {
        var repository = Repository();
        var entry = NewEntry(due: DateTime.UtcNow.AddSeconds(-1));
        await repository.InsertAsync(entry);

        // Un relais bavard — ou hostile — ne doit pas faire échouer l'enregistrement de l'échec,
        // ce qui perdrait l'information au moment précis où elle sert.
        await repository.RescheduleAsync(entry.Id, DateTime.UtcNow.AddMinutes(1), new string('x', 50_000));

        Assert.Equal(1, await CountRowsAsync(entry.Id));
    }

    // ---- helpers ----

    private IEmailOutboxRepository Repository() =>
        _factory.Services.GetRequiredService<IEmailOutboxRepository>();

    private System.Data.IDbConnection Connection() =>
        _factory.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();

    private static OutboxEntry NewEntry(DateTime due) => new()
    {
        Id = UlidGenerator.NewUlid(),
        ToAddress = $"{Guid.NewGuid():N}@example.com",
        Subject = "Réinitialisation",
        BodyEncrypted = [1, 2, 3, 250, 251, 252],
        Kind = "password_reset",
        Attempts = 0,
        NextAttemptAt = due,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// Réclame jusqu'à retrouver CE message, ou conclut qu'il n'est pas réclamable.
    ///
    /// La base de test est partagée : d'autres messages peuvent être dus au même instant, et
    /// prendre la première réclamation pour la sienne rendrait ces tests dépendants de l'ordre
    /// d'exécution des autres.
    /// </summary>
    private static async Task<OutboxEntry?> ClaimMineAsync(IEmailOutboxRepository repository, string id)
    {
        for (var i = 0; i < 20; i++)
        {
            var claimed = await repository.ClaimNextDueAsync(DateTime.UtcNow, TimeSpan.FromMinutes(5));
            if (claimed is null) return null;
            if (claimed.Id == id) return claimed;
        }

        return null;
    }

    private async Task<int> CountRowsAsync(string id)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM email_outbox WHERE id = @Id", new { Id = id });
    }
}
