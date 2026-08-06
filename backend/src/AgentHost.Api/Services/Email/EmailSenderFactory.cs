using System.Net.Mail;
using Serilog;

namespace AgentHost.Api.Services.Email;

/// <summary>
/// Choisit l'implémentation d'<see cref="IEmailSender"/> à partir de la configuration.
///
/// <b>Une configuration de mailer incomplète ne fait jamais échouer le démarrage.</b> Chaque cas
/// invalide retombe sur <see cref="NoOpEmailSender"/> avec une raison lisible, qui sera répétée à
/// chaque message non parti. Le raisonnement : refuser de démarrer parce que l'adresse
/// d'expédition est mal orthographiée transformerait une fonctionnalité annexe en dépendance du
/// service entier, alors même que le mode nominal de ce dépôt est de n'envoyer aucun courriel.
/// L'inverse — envoyer sans le vouloir — est empêché par le fait que rien ne s'active tant que
/// <c>Email:Provider</c> n'a pas été mis à « smtp » explicitement.
/// </summary>
public static class EmailSenderFactory
{
    /// <summary>Construit l'expéditeur correspondant à <paramref name="options"/>.</summary>
    public static IEmailSender Create(EmailOptions options, ILogger logger)
    {
        if (!options.IsSmtp)
            return new NoOpEmailSender(logger, "Email:Provider n'est pas positionné sur « smtp »");

        if (string.IsNullOrWhiteSpace(options.Smtp.Host))
            return new NoOpEmailSender(logger, "Email:Provider vaut « smtp » mais Email:Smtp:Host est vide");

        if (!IsUsableAddress(options.FromAddress))
        {
            return new NoOpEmailSender(logger,
                $"Email:FromAddress est absent ou invalide (« {options.FromAddress} ») : un relais SMTP " +
                "rejetterait tous les envois");
        }

        logger.Information(
            "Expéditeur SMTP actif : {Host}:{Port} ({Security}, authentification {Auth}), From {From}, liens vers {BaseUrl}, langue {Language}",
            options.Smtp.Host, options.Smtp.Port, options.Smtp.Security,
            options.Smtp.HasCredentials ? "activée" : "désactivée",
            options.FromAddress, options.AppBaseUrl, options.Language);

        return new SmtpEmailSender(options, logger);
    }

    /// <summary>
    /// Vrai quand l'adresse est syntaxiquement exploitable. On délègue à
    /// <see cref="MailAddress"/> — c'est exactement l'analyseur qui sera utilisé à l'envoi, donc
    /// le seul dont l'accord compte.
    /// </summary>
    private static bool IsUsableAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;

        try
        {
            _ = new MailAddress(address);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
