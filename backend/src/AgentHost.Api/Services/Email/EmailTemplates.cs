namespace AgentHost.Api.Services.Email;

/// <summary>
/// Langue des messages sortants.
///
/// Il n'existe aucune préférence de langue par utilisateur dans le schéma (la table <c>users</c>
/// n'a pas de colonne locale), et les deux flux concernés — réinitialisation de mot de passe et
/// invitation — s'adressent régulièrement à quelqu'un qui n'a pas encore de compte. Le choix est
/// donc volontairement une décision de déploiement (<c>Email:Language</c>) et non une négociation
/// par destinataire : c'est le seul mécanisme honnête tant qu'on n'a rien à négocier.
/// </summary>
public enum EmailLanguage
{
    /// <summary>Français — langue par défaut du produit.</summary>
    French,

    /// <summary>Anglais.</summary>
    English,
}

/// <summary>
/// Les deux messages transactionnels du produit, en français et en anglais.
///
/// Choix de rédaction communs aux deux :
/// <list type="bullet">
/// <item>le lien complet apparaît en clair sur sa propre ligne — les clients de messagerie en
/// texte brut ne rendent pas tous les liens cliquables, et un lien coupé par un retour à la ligne
/// est un jeton perdu ;</item>
/// <item>la durée de validité est annoncée, parce qu'un lien expiré sans explication ressemble à
/// une panne ;</item>
/// <item>aucun des deux messages n'affirme quoi que ce soit sur l'existence d'un compte au-delà de
/// ce que son destinataire sait déjà : le message de réinitialisation n'est envoyé qu'à une
/// adresse qui en a un, et il n'existe aucun message « cette adresse n'a pas de compte » — écrire
/// à quelqu'un pour lui dire qu'il n'a pas de compte serait un canal d'énumération de plus.</item>
/// </list>
/// </summary>
public static class EmailTemplates
{
    /// <summary>Étiquette de journalisation du courriel de réinitialisation.</summary>
    public const string PasswordResetKind = "password-reset";

    /// <summary>Étiquette de journalisation du courriel d'invitation.</summary>
    public const string InvitationKind = "invitation";

    /// <summary>
    /// Courriel de réinitialisation de mot de passe. <paramref name="link"/> porte le jeton brut :
    /// c'est le seul endroit du système où cette valeur quitte le processus vers son destinataire
    /// légitime.
    /// </summary>
    public static EmailMessage PasswordReset(string to, string link, TimeSpan validity, EmailLanguage language) =>
        language == EmailLanguage.English
            ? new EmailMessage
            {
                To = to,
                Kind = PasswordResetKind,
                Subject = "Reset your AgentHost password",
                TextBody = $"""
                    Hello,

                    Someone asked to reset the password of the AgentHost account attached to this
                    address. Open the link below to choose a new one:

                    {link}

                    This link is valid for {Humanize(validity, language)} and can be used only once.

                    If you did not ask for this, you can ignore this message: your password stays
                    unchanged and nothing has happened to your account.

                    -- AgentHost
                    """,
            }
            : new EmailMessage
            {
                To = to,
                Kind = PasswordResetKind,
                Subject = "Réinitialisation de votre mot de passe AgentHost",
                TextBody = $"""
                    Bonjour,

                    Une réinitialisation du mot de passe du compte AgentHost associé à cette adresse
                    a été demandée. Ouvrez le lien ci-dessous pour en choisir un nouveau :

                    {link}

                    Ce lien est valable {Humanize(validity, language)} et ne peut servir qu'une fois.

                    Si vous n'êtes pas à l'origine de cette demande, ignorez ce message : votre mot
                    de passe reste inchangé et rien n'a été modifié sur votre compte.

                    -- AgentHost
                    """,
            };

    /// <summary>
    /// Courriel d'invitation. <paramref name="link"/> porte le jeton brut.
    /// </summary>
    public static EmailMessage Invitation(string to, string link, TimeSpan validity, EmailLanguage language) =>
        language == EmailLanguage.English
            ? new EmailMessage
            {
                To = to,
                Kind = InvitationKind,
                Subject = "You have been invited to AgentHost",
                TextBody = $"""
                    Hello,

                    You have been invited to join an organization on AgentHost. Open the link below
                    to create your account and choose your own password:

                    {link}

                    This invitation is valid for {Humanize(validity, language)}.

                    If you were not expecting this invitation, you can ignore this message: no
                    account is created until the link is opened.

                    -- AgentHost
                    """,
            }
            : new EmailMessage
            {
                To = to,
                Kind = InvitationKind,
                Subject = "Vous êtes invité·e sur AgentHost",
                TextBody = $"""
                    Bonjour,

                    Vous avez été invité·e à rejoindre une organisation sur AgentHost. Ouvrez le
                    lien ci-dessous pour créer votre compte et choisir votre propre mot de passe :

                    {link}

                    Cette invitation est valable {Humanize(validity, language)}.

                    Si vous n'attendiez pas cette invitation, ignorez ce message : aucun compte
                    n'est créé tant que le lien n'a pas été ouvert.

                    -- AgentHost
                    """,
            };

    /// <summary>
    /// Rend une durée en toutes lettres dans l'unité qui a du sens pour elle : 30 minutes pour un
    /// jeton de réinitialisation, 7 jours pour une invitation. Écrire « 0,02 jour » ou
    /// « 10080 minutes » serait exact et inutilisable.
    /// </summary>
    private static string Humanize(TimeSpan validity, EmailLanguage language)
    {
        if (validity < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Round(validity.TotalMinutes));
            return language == EmailLanguage.English ? $"{minutes} minutes" : $"{minutes} minutes";
        }

        if (validity < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)Math.Round(validity.TotalHours));
            return language == EmailLanguage.English
                ? hours == 1 ? "1 hour" : $"{hours} hours"
                : hours == 1 ? "1 heure" : $"{hours} heures";
        }

        var days = Math.Max(1, (int)Math.Round(validity.TotalDays));
        return language == EmailLanguage.English
            ? days == 1 ? "1 day" : $"{days} days"
            : days == 1 ? "1 jour" : $"{days} jours";
    }
}
