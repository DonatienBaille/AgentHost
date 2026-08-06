namespace AgentHost.Api.Services.Email;

/// <summary>
/// Mode de chiffrement du transport SMTP.
///
/// Volontairement limité à deux valeurs : voir <see cref="SmtpEmailSender"/> pour la raison
/// (le TLS implicite du port 465 n'est pas réalisable avec le client SMTP du BCL).
/// </summary>
public enum SmtpSecurity
{
    /// <summary>Aucun chiffrement. À réserver à un relais local de confiance.</summary>
    None,

    /// <summary>TLS explicite : connexion en clair puis commande STARTTLS. Le cas normal (port 587).</summary>
    StartTls,
}

/// <summary>
/// Configuration de la section <c>Email</c>, lue une seule fois au démarrage.
///
/// Le principe directeur : <b>ne rien envoyer par défaut</b>. Un déploiement qui ne configure rien
/// garde exactement le comportement historique du dépôt (aucun courriel, jeton d'invitation rendu
/// à l'appelant), et l'envoi réel est une décision explicite — jamais un effet de bord d'une
/// valeur par défaut.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>Nom de la section de configuration.</summary>
    public const string SectionName = "Email";

    /// <summary>Fournisseur « aucun envoi » : <see cref="NoOpEmailSender"/>. Valeur par défaut.</summary>
    public const string NoneProvider = "none";

    /// <summary>Fournisseur SMTP : <see cref="SmtpEmailSender"/>.</summary>
    public const string SmtpProvider = "smtp";

    /// <summary>Le front Angular en développement (<c>ng serve</c>) écoute sur ce port.</summary>
    public const string DefaultAppBaseUrl = "http://localhost:4200";

    /// <summary>Chemin front du formulaire de nouveau mot de passe.</summary>
    public const string DefaultResetPasswordPath = "/reset-password";

    /// <summary>Chemin front du formulaire d'acceptation d'invitation.</summary>
    public const string DefaultAcceptInvitationPath = "/accept-invitation";

    /// <summary>
    /// Fournisseur retenu, normalisé en minuscules. Tout ce qui n'est pas <see cref="SmtpProvider"/>
    /// vaut <see cref="NoneProvider"/> : une valeur mal orthographiée ne doit pas activer un envoi.
    /// </summary>
    public string Provider { get; init; } = NoneProvider;

    /// <summary>Adresse d'expédition (enveloppe et en-tête <c>From</c>).</summary>
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>Nom affiché de l'expéditeur. Facultatif.</summary>
    public string FromName { get; init; } = "AgentHost";

    /// <summary>
    /// Racine publique du front, sans barre oblique finale. C'est elle qui rend les liens
    /// utilisables : un <c>http://localhost</c> codé en dur n'aurait aucun sens dans un courriel.
    /// </summary>
    public string AppBaseUrl { get; init; } = DefaultAppBaseUrl;

    /// <summary>Chemin du formulaire de réinitialisation, relatif à <see cref="AppBaseUrl"/>.</summary>
    public string ResetPasswordPath { get; init; } = DefaultResetPasswordPath;

    /// <summary>Chemin du formulaire d'acceptation, relatif à <see cref="AppBaseUrl"/>.</summary>
    public string AcceptInvitationPath { get; init; } = DefaultAcceptInvitationPath;

    /// <summary>Langue des messages. Français par défaut.</summary>
    public EmailLanguage Language { get; init; } = EmailLanguage.French;

    /// <summary>Paramètres du transport SMTP. Ignorés quand <see cref="Provider"/> ne vaut pas « smtp ».</summary>
    public SmtpOptions Smtp { get; init; } = new();

    /// <summary>Vrai quand un fournisseur d'envoi réel est demandé.</summary>
    public bool IsSmtp => string.Equals(Provider, SmtpProvider, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lit la section <c>Email</c>. Toute valeur absente ou illisible retombe sur le défaut : la
    /// configuration d'un mailer ne doit jamais faire échouer le démarrage d'un déploiement qui,
    /// justement, n'en veut pas.
    /// </summary>
    public static EmailOptions FromConfiguration(IConfiguration config)
    {
        var provider = (config[$"{SectionName}:Provider"] ?? NoneProvider).Trim();

        return new EmailOptions
        {
            Provider = string.Equals(provider, SmtpProvider, StringComparison.OrdinalIgnoreCase)
                ? SmtpProvider
                : NoneProvider,
            FromAddress = (config[$"{SectionName}:FromAddress"] ?? string.Empty).Trim(),
            FromName = Fallback(config[$"{SectionName}:FromName"], "AgentHost"),
            AppBaseUrl = Fallback(config[$"{SectionName}:AppBaseUrl"], DefaultAppBaseUrl).TrimEnd('/'),
            ResetPasswordPath = Fallback(config[$"{SectionName}:ResetPasswordPath"], DefaultResetPasswordPath),
            AcceptInvitationPath = Fallback(config[$"{SectionName}:AcceptInvitationPath"], DefaultAcceptInvitationPath),
            Language = ParseLanguage(config[$"{SectionName}:Language"]),
            Smtp = SmtpOptions.FromConfiguration(config),
        };
    }

    /// <summary>Lien de réinitialisation, jeton brut inclus.</summary>
    public string PasswordResetLink(string rawToken) => BuildLink(ResetPasswordPath, rawToken);

    /// <summary>Lien d'acceptation d'invitation, jeton brut inclus.</summary>
    public string InvitationLink(string rawToken) => BuildLink(AcceptInvitationPath, rawToken);

    private string BuildLink(string path, string rawToken)
    {
        var normalizedPath = path.StartsWith('/') ? path : "/" + path;
        // Les jetons de OpaqueToken sont déjà base64url, donc sûrs en chaîne de requête ; on
        // échappe quand même pour ne pas dépendre de ce détail d'implémentation.
        return $"{AppBaseUrl}{normalizedPath.TrimEnd('/')}?token={Uri.EscapeDataString(rawToken)}";
    }

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static EmailLanguage ParseLanguage(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "en" or "en-us" or "en-gb" or "english" or "anglais" => EmailLanguage.English,
            // Y compris pour une valeur vide ou inconnue : le français est la langue du produit.
            _ => EmailLanguage.French,
        };
}

/// <summary>Paramètres du transport SMTP. Voir <see cref="SmtpEmailSender"/>.</summary>
public sealed class SmtpOptions
{
    /// <summary>Port par défaut : soumission de message avec STARTTLS (RFC 6409).</summary>
    public const int DefaultPort = 587;

    /// <summary>
    /// Délai d'expiration par défaut. Généreux comparé à un appel HTTP, parce que l'envoi est
    /// hors du chemin de réponse (voir <see cref="BackgroundEmailDispatcher"/>) : personne
    /// n'attend derrière.
    /// </summary>
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>Hôte du relais. Vide = SMTP non configurable, on retombe sur le no-op.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Port TCP.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Chiffrement du transport.</summary>
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    /// <summary>Identifiant. Vide = pas d'authentification (relais ouvert sur le réseau interne).</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Mot de passe / clé d'API. Ignoré si <see cref="UserName"/> est vide.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Délai d'expiration de l'envoi, en secondes.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary>Vrai quand des identifiants sont fournis.</summary>
    public bool HasCredentials => !string.IsNullOrWhiteSpace(UserName);

    /// <summary>Lit la sous-section <c>Email:Smtp</c>.</summary>
    public static SmtpOptions FromConfiguration(IConfiguration config)
    {
        const string prefix = $"{EmailOptions.SectionName}:Smtp";

        return new SmtpOptions
        {
            Host = (config[$"{prefix}:Host"] ?? string.Empty).Trim(),
            Port = int.TryParse(config[$"{prefix}:Port"], out var port) && port is > 0 and <= 65535
                ? port
                : DefaultPort,
            Security = ParseSecurity(config[$"{prefix}:Security"]),
            UserName = (config[$"{prefix}:UserName"] ?? string.Empty).Trim(),
            Password = config[$"{prefix}:Password"] ?? string.Empty,
            TimeoutSeconds = int.TryParse(config[$"{prefix}:TimeoutSeconds"], out var t) && t > 0
                ? t
                : DefaultTimeoutSeconds,
        };
    }

    /// <summary>
    /// Analyse le mode de chiffrement. Tout ce qui n'est pas explicitement « none » active
    /// STARTTLS : le défaut sûr est de chiffrer, et une faute de frappe ne doit pas dégrader le
    /// transport en clair. « ssl » est accepté et traité comme STARTTLS — voir la note sur le TLS
    /// implicite dans <see cref="SmtpEmailSender"/>.
    /// </summary>
    public static SmtpSecurity ParseSecurity(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "none" or "false" or "plain" or "aucun" => SmtpSecurity.None,
            _ => SmtpSecurity.StartTls,
        };
}
