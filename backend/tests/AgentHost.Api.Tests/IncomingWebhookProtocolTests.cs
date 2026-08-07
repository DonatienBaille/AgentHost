using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentHost.Api.Domain;
using AgentHost.Api.Services;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// La vérification des livraisons de webhook entrantes (lot 4).
///
/// <b>C'est la seule chose qui autorise l'endpoint d'ingestion.</b> Il est anonyme par nécessité —
/// c'est une forge qui appelle, elle n'a pas de compte ici — donc chaque défaut de cette
/// vérification est directement la capacité, pour n'importe qui, de lancer des runs sur le budget
/// d'un projet qui n'est pas le sien.
///
/// Ces tests calculent les signatures avec la même primitive que le code de production, ce qui ne
/// prouverait rien s'ils s'arrêtaient là ; ils vérifient donc surtout ce qui doit être REFUSÉ :
/// signature absente, tronquée, d'un autre algorithme, calculée sur un corps re-sérialisé, ou
/// valide pour un autre secret.
/// </summary>
public class IncomingWebhookProtocolTests
{
    private const string Secret = "s3cr3t-de-declencheur";
    private static readonly byte[] Body = "{\"ref\":\"refs/heads/main\",\"after\":\"abc\"}"u8.ToArray();

    private static Func<string, string?> Headers(params (string Name, string Value)[] headers)
    {
        var map = headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static string Sign(byte[] body, string secret = Secret) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    // ---- GitHub ----

    [Fact]
    public void A_correctly_signed_github_delivery_is_accepted()
    {
        var verdict = IncomingWebhookProtocol.Verify(
            TriggerProvider.GitHub, Secret, Body,
            Headers((IncomingWebhookProtocol.GitHubSignatureHeader, Sign(Body))));

        Assert.Equal(SignatureVerdict.Valid, verdict);
    }

    [Fact]
    public void An_unsigned_delivery_is_missing_not_invalid()
    {
        // La nuance sert au diagnostic : une signature absente est presque toujours une mauvaise
        // configuration, une signature fausse est une tentative.
        Assert.Equal(SignatureVerdict.Missing,
            IncomingWebhookProtocol.Verify(TriggerProvider.GitHub, Secret, Body, Headers()));
    }

    [Fact]
    public void A_signature_computed_with_another_secret_is_refused()
    {
        var verdict = IncomingWebhookProtocol.Verify(
            TriggerProvider.GitHub, Secret, Body,
            Headers((IncomingWebhookProtocol.GitHubSignatureHeader, Sign(Body, "un-autre-secret"))));

        Assert.Equal(SignatureVerdict.Invalid, verdict);
    }

    [Fact]
    public void A_signature_of_a_different_body_is_refused()
    {
        // Le point qui compte pour l'endpoint : le HMAC porte sur les octets EXACTS reçus. Un
        // corps re-sérialisé — mêmes données, espaces ou ordre des clés différents — ne signe pas
        // pareil, et c'est pourquoi l'ingestion lit un byte[] au lieu de lier un modèle.
        var reserialized = "{ \"ref\": \"refs/heads/main\", \"after\": \"abc\" }"u8.ToArray();

        var verdict = IncomingWebhookProtocol.Verify(
            TriggerProvider.GitHub, Secret, reserialized,
            Headers((IncomingWebhookProtocol.GitHubSignatureHeader, Sign(Body))));

        Assert.Equal(SignatureVerdict.Invalid, verdict);
    }

    [Theory]
    [InlineData("deadbeef")]                                    // trop courte
    [InlineData("sha256=deadbeef")]                             // bon préfixe, mauvaise longueur
    [InlineData("sha1=da39a3ee5e6b4b0d3255bfef95601890afd80709")] // autre algorithme
    [InlineData("sha256=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // pas de l'hexadécimal
    public void Malformed_signatures_are_refused(string presented)
    {
        // `sha1` en particulier : GitHub envoie encore un en-tête SHA-1 hérité, et l'accepter
        // rouvrirait un algorithme cassé pour la seule raison qu'il traîne dans la requête.
        Assert.Equal(SignatureVerdict.Invalid, IncomingWebhookProtocol.Verify(
            TriggerProvider.GitHub, Secret, Body,
            Headers((IncomingWebhookProtocol.GitHubSignatureHeader, presented))));
    }

    // ---- GitLab ----

    [Fact]
    public void Gitlab_presents_the_secret_itself_and_it_must_match_exactly()
    {
        Assert.Equal(SignatureVerdict.Valid, IncomingWebhookProtocol.Verify(
            TriggerProvider.GitLab, Secret, Body,
            Headers((IncomingWebhookProtocol.GitLabTokenHeader, Secret))));

        Assert.Equal(SignatureVerdict.Invalid, IncomingWebhookProtocol.Verify(
            TriggerProvider.GitLab, Secret, Body,
            Headers((IncomingWebhookProtocol.GitLabTokenHeader, Secret + "x"))));

        Assert.Equal(SignatureVerdict.Missing, IncomingWebhookProtocol.Verify(
            TriggerProvider.GitLab, Secret, Body, Headers()));
    }

    [Fact]
    public void A_github_signature_does_not_authorize_a_gitlab_trigger_and_the_reverse()
    {
        // Chaque émetteur a son en-tête. Accepter l'un pour l'autre reviendrait à accepter un
        // jeton porteur là où on attend une preuve de possession.
        Assert.Equal(SignatureVerdict.Missing, IncomingWebhookProtocol.Verify(
            TriggerProvider.GitLab, Secret, Body,
            Headers((IncomingWebhookProtocol.GitHubSignatureHeader, Sign(Body)))));

        Assert.Equal(SignatureVerdict.Missing, IncomingWebhookProtocol.Verify(
            TriggerProvider.GitHub, Secret, Body,
            Headers((IncomingWebhookProtocol.GitLabTokenHeader, Secret))));
    }

    [Fact]
    public void The_generic_provider_follows_the_github_scheme()
    {
        Assert.Equal(SignatureVerdict.Valid, IncomingWebhookProtocol.Verify(
            TriggerProvider.Generic, Secret, Body,
            Headers((IncomingWebhookProtocol.GenericSignatureHeader, Sign(Body)))));
    }

    // ---- déduplication ----

    [Fact]
    public void The_delivery_id_comes_from_the_emitters_header_when_there_is_one()
    {
        var id = IncomingWebhookProtocol.DeliveryId(
            TriggerProvider.GitHub, Body,
            Headers((IncomingWebhookProtocol.GitHubDeliveryHeader, "d1-d2-d3")));

        Assert.Equal("d1-d2-d3", id);
    }

    [Fact]
    public void Without_a_header_the_body_hash_stands_in()
    {
        var first = IncomingWebhookProtocol.DeliveryId(TriggerProvider.Generic, Body, Headers());
        var same = IncomingWebhookProtocol.DeliveryId(TriggerProvider.Generic, Body, Headers());
        var other = IncomingWebhookProtocol.DeliveryId(TriggerProvider.Generic, "{}"u8.ToArray(), Headers());

        // Deux livraisons au contenu rigoureusement identique sont indiscernables et seront
        // traitées comme un doublon : mieux vaut ne pas relancer qu'en facturer deux.
        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.StartsWith("sha256:", first);
    }

    [Fact]
    public void An_oversized_delivery_header_cannot_break_the_insert()
    {
        var id = IncomingWebhookProtocol.DeliveryId(
            TriggerProvider.GitHub, Body,
            Headers((IncomingWebhookProtocol.GitHubDeliveryHeader, new string('x', 500))));

        // La colonne fait 128 caractères ; un en-tête hostile n'a pas à faire échouer l'écriture.
        Assert.Equal(128, id.Length);
    }

    // ---- filtrage ----

    [Fact]
    public void An_absent_filter_accepts_everything()
    {
        // Le défaut permissif est délibéré : un filtre vide qui ne laisserait rien passer donnerait
        // un déclencheur muet — l'IHM dirait « actif », la forge dirait « livré », rien ne
        // partirait, et personne ne comprendrait pourquoi.
        Assert.True(IncomingWebhookProtocol.Accepts(null, "push", "main"));
        Assert.True(IncomingWebhookProtocol.Accepts(new TriggerEventFilter(), "issues", null));
    }

    [Fact]
    public void An_event_filter_only_lets_its_own_events_through()
    {
        var filter = new TriggerEventFilter { Events = ["push"] };

        Assert.True(IncomingWebhookProtocol.Accepts(filter, "push", null));
        Assert.True(IncomingWebhookProtocol.Accepts(filter, "PUSH", null));
        Assert.False(IncomingWebhookProtocol.Accepts(filter, "issues", null));
        // Un émetteur qui ne déclare pas d'événement ne peut pas satisfaire un filtre qui en exige
        // un : le laisser passer viderait le filtre de son sens.
        Assert.False(IncomingWebhookProtocol.Accepts(filter, null, null));
    }

    [Fact]
    public void A_branch_filter_matches_exactly_or_by_trailing_wildcard()
    {
        var filter = new TriggerEventFilter { Branches = ["main", "release/*"] };

        Assert.True(IncomingWebhookProtocol.Accepts(filter, null, "main"));
        Assert.True(IncomingWebhookProtocol.Accepts(filter, null, "release/2031-03"));
        Assert.False(IncomingWebhookProtocol.Accepts(filter, null, "feature/x"));
        // `mainline` ne doit pas passer pour `main` : sans joker, la correspondance est stricte.
        Assert.False(IncomingWebhookProtocol.Accepts(filter, null, "mainline"));
    }

    [Fact]
    public void A_delivery_without_a_branch_is_not_rejected_by_a_branch_filter()
    {
        var filter = new TriggerEventFilter { Branches = ["main"] };

        // L'ouverture d'une issue n'est pas « sur une mauvaise branche » : elle est hors sujet pour
        // ce critère, et le rejeter ferait taire tout déclencheur non lié à un push.
        Assert.True(IncomingWebhookProtocol.Accepts(filter, "issues", null));
    }

    [Fact]
    public void The_branch_is_read_from_the_git_ref()
    {
        Assert.Equal("main", IncomingWebhookProtocol.BranchOf(JsonNode.Parse("""{"ref":"refs/heads/main"}""")));
        Assert.Equal("release/x", IncomingWebhookProtocol.BranchOf(JsonNode.Parse("""{"ref":"refs/heads/release/x"}""")));
        // Une référence qui n'est pas une branche (un tag) est rendue telle quelle plutôt que
        // tronquée n'importe comment.
        Assert.Equal("refs/tags/v1", IncomingWebhookProtocol.BranchOf(JsonNode.Parse("""{"ref":"refs/tags/v1"}""")));
        Assert.Null(IncomingWebhookProtocol.BranchOf(JsonNode.Parse("""{"action":"opened"}""")));
        Assert.Null(IncomingWebhookProtocol.BranchOf(null));
    }
}
