using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AgentHost.Api.Tests;

/// <summary>
/// Un serveur SMTP minimal, en mémoire, qui parle assez du protocole pour qu'un vrai
/// <see cref="System.Net.Mail.SmtpClient"/> lui remette un message.
///
/// <b>Pourquoi ce fichier existe.</b> La feuille de route porte depuis des mois une dette explicite :
/// « <c>SmtpEmailSender</c> n'a jamais parlé à un vrai serveur SMTP ». Sa sélection par configuration
/// et sa construction étaient testées ; la poignée de main STARTTLS, l'authentification et le délai
/// d'expiration reposaient sur le contrat documenté du BCL, <b>pas sur une observation</b>. Or c'est
/// exactement là que se logent les surprises : un <c>EnableSsl</c> qui ne négocie pas, des
/// identifiants jamais présentés, un message accepté mais vide.
///
/// Un relais réel n'est joignable nulle part dans cet environnement. Plutôt que de laisser la dette
/// ouverte, on amène le serveur à soi : cent lignes de protocole suffisent à exercer le chemin
/// complet, y compris la montée en TLS, qui est la partie qu'aucun double ne peut simuler.
///
/// <b>Ce que ce serveur ne prouve pas, et il faut le dire.</b> Le certificat est auto-signé et le
/// test désactive sa validation côté client. La négociation TLS est donc réellement exercée — le
/// <c>ServerHello</c>, la montée en chiffrement, la reprise du dialogue SMTP dans le tunnel — mais
/// la <b>vérification de chaîne</b> ne l'est pas. Un relais public reste à confronter une fois, au
/// même titre que Podman, S3 et HIBP (lot 1.3).
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private readonly X509Certificate2 _certificate;
    private readonly TaskCompletionSource<ReceivedMessage> _received =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Le port réellement attribué. Zéro serait pris pour « choisis-en un » par le client.</summary>
    public int Port { get; }

    /// <summary>Le serveur a-t-il annoncé, puis effectué, une montée en TLS ?</summary>
    public bool StartTlsNegotiated { get; private set; }

    /// <summary>Identifiants présentés par le client, ou null s'il n'en a pas envoyé.</summary>
    public (string User, string Password)? Credentials { get; private set; }

    /// <summary>
    /// La session a-t-elle été chiffrée dès l'ouverture de la socket, sans <c>STARTTLS</c> ?
    ///
    /// Distinct de <see cref="StartTlsNegotiated"/>, et pas par coquetterie : les deux modes
    /// aboutissent à une session chiffrée, mais un client qui ferait du STARTTLS là où l'on attend
    /// du TLS implicite ne pourrait tout simplement pas parler à un relais qui n'écoute qu'en 465.
    /// C'est précisément la différence que le port 465 rendait inaccessible.
    /// </summary>
    public bool ImplicitTlsNegotiated { get; private set; }

    /// <param name="implicitTls">
    /// Chiffre dès l'acceptation de la connexion (port 465, SMTPS). Le serveur n'annonce alors pas
    /// <c>STARTTLS</c> : un relais en TLS implicite n'a rien à monter, la session l'est déjà.
    /// </param>
    public FakeSmtpServer(bool offerStartTls = true, bool requireAuth = true, bool implicitTls = false)
    {
        OffersStartTls = offerStartTls && !implicitTls;
        RequiresAuth = requireAuth;
        ImplicitTls = implicitTls;

        _certificate = CreateSelfSignedCertificate();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _loop = Task.Run(AcceptLoopAsync);
    }

    private bool OffersStartTls { get; }
    private bool RequiresAuth { get; }
    private bool ImplicitTls { get; }

    /// <summary>Attend le message, ou échoue au bout du délai plutôt que de bloquer la suite.</summary>
    public async Task<ReceivedMessage> WaitForMessageAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_received.Task, Task.Delay(timeout));
        if (completed != _received.Task)
            throw new TimeoutException($"No SMTP message received within {timeout}");

        return await _received.Task;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        Stream stream = client.GetStream();

        try
        {
            // TLS implicite : on négocie AVANT d'écrire la bannière 220. C'est toute la différence
            // avec STARTTLS — il n'y a aucun échange en clair, pas même la salutation, donc rien à
            // observer ni à supprimer sur le fil pour faire retomber la session en clair.
            if (ImplicitTls)
            {
                var tunnel = new SslStream(stream, leaveInnerStreamOpen: false);
                await tunnel.AuthenticateAsServerAsync(_certificate, false, checkCertificateRevocation: false);
                stream = tunnel;
                ImplicitTlsNegotiated = true;
            }

            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

            await writer.WriteLineAsync("220 fake.agenthost.test ESMTP");

            string? from = null;
            var recipients = new List<string>();

            while (await reader.ReadLineAsync() is { } line)
            {
                var upper = line.ToUpperInvariant();

                if (upper.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    // Les extensions sont annoncées ligne à ligne, la dernière sans tiret. C'est
                    // cette liste que SmtpClient lit pour décider s'il peut faire STARTTLS et AUTH.
                    await writer.WriteLineAsync("250-fake.agenthost.test");
                    if (OffersStartTls && stream is not SslStream)
                        await writer.WriteLineAsync("250-STARTTLS");
                    if (RequiresAuth)
                        await writer.WriteLineAsync("250-AUTH LOGIN PLAIN");
                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (upper.StartsWith("HELO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250 fake.agenthost.test");
                }
                else if (upper.StartsWith("STARTTLS", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("220 Ready to start TLS");

                    // La vraie montée en chiffrement : tout le dialogue qui suit passe dans le
                    // tunnel, et le client redemande EHLO. C'est la partie qu'aucun double ne
                    // simule, et donc la seule raison d'écrire un serveur plutôt qu'un stub.
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(_certificate, false, checkCertificateRevocation: false);
                    stream = ssl;
                    StartTlsNegotiated = true;

                    reader = new StreamReader(ssl, Encoding.ASCII, leaveOpen: true);
                    writer = new StreamWriter(ssl, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                }
                else if (upper.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                {
                    // SmtpClient choisit PLAIN quand les deux sont annoncés. Ne gérer que LOGIN
                    // faisait échouer l'authentification avec un « Authentication failed » qui
                    // accusait le mot de passe alors que le serveur de test était en cause.
                    // Le format est base64(\0utilisateur\0mot de passe), éventuellement en ligne.
                    var inline = line.Length > "AUTH PLAIN".Length
                        ? line["AUTH PLAIN".Length..].Trim()
                        : string.Empty;

                    if (inline.Length == 0)
                    {
                        await writer.WriteLineAsync("334 ");
                        inline = (await reader.ReadLineAsync() ?? string.Empty).Trim();
                    }

                    var parts = Decode(inline).Split('\0');
                    if (parts.Length >= 3) Credentials = (parts[1], parts[2]);
                    await writer.WriteLineAsync("235 Authentication successful");
                }
                else if (upper.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
                {
                    var inlineUser = line.Length > "AUTH LOGIN".Length
                        ? line["AUTH LOGIN".Length..].Trim()
                        : string.Empty;

                    string user;
                    if (inlineUser.Length > 0)
                    {
                        user = Decode(inlineUser);
                    }
                    else
                    {
                        await writer.WriteLineAsync("334 VXNlcm5hbWU6");    // « Username: »
                        user = Decode(await reader.ReadLineAsync());
                    }

                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6");        // « Password: »
                    var password = Decode(await reader.ReadLineAsync());
                    Credentials = (user, password);
                    await writer.WriteLineAsync("235 Authentication successful");
                }
                else if (upper.StartsWith("MAIL FROM", StringComparison.Ordinal))
                {
                    from = Between(line, '<', '>');
                    await writer.WriteLineAsync("250 OK");
                }
                else if (upper.StartsWith("RCPT TO", StringComparison.Ordinal))
                {
                    recipients.Add(Between(line, '<', '>'));
                    await writer.WriteLineAsync("250 OK");
                }
                else if (upper.StartsWith("DATA", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");

                    var body = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        body.AppendLine(dataLine);

                    await writer.WriteLineAsync("250 Queued");
                    _received.TrySetResult(new ReceivedMessage(from ?? "", recipients, body.ToString()));
                }
                else if (upper.StartsWith("QUIT", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("221 Bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
        }
        catch (Exception ex)
        {
            // Le client ferme parfois brutalement après QUIT ; ce n'est pas un échec du test.
            _received.TrySetException(new InvalidOperationException("SMTP session failed", ex));
        }
        finally
        {
            if (stream is SslStream ssl) await ssl.DisposeAsync();
        }
    }

    private static string Decode(string? base64) =>
        string.IsNullOrEmpty(base64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    private static string Between(string value, char open, char close)
    {
        var start = value.IndexOf(open);
        var end = value.LastIndexOf(close);
        return start >= 0 && end > start ? value[(start + 1)..end] : value;
    }

    /// <summary>Un certificat jetable, généré en mémoire : rien à installer, rien à nettoyer.</summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Sur Linux, SslStream exige une clé privée exportable et attachée : le passage par PFX est
        // le chemin portable pour l'obtenir. (`X509CertificateLoader` serait plus propre mais
        // n'existe qu'à partir de .NET 9 ; ce projet cible net8.0.)
#pragma warning disable SYSLIB0057
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        _certificate.Dispose();

        try { await _loop; } catch { /* la boucle s'arrête sur l'annulation */ }

        _stopping.Dispose();
    }

    public sealed record ReceivedMessage(string From, IReadOnlyList<string> Recipients, string Data)
    {
        public bool HasHeader(string name, string value) =>
            Data.Contains($"{name}: {value}", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Le corps, décodé selon son <c>Content-Transfer-Encoding</c>.
        ///
        /// <b>Pourquoi ce décodage n'est pas une commodité de test.</b> <c>MailMessage</c> encode
        /// le corps — base64 ou quoted-printable — dès qu'il contient un caractère non ASCII, ce
        /// que fait tout message en français. Chercher le jeton en clair dans les octets reçus
        /// échouerait donc toujours, et l'on conclurait à tort que le lien n'est pas arrivé. C'est
        /// le destinataire qui décode ; le test doit faire pareil pour poser la bonne question.
        /// </summary>
        public string DecodedBody
        {
            get
            {
                var separator = Data.IndexOf("\n\n", StringComparison.Ordinal);
                if (separator < 0) return Data;

                var headers = Data[..separator];
                var body = Data[(separator + 2)..];

                if (headers.Contains("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase))
                {
                    var joined = string.Concat(body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()));
                    try { return Encoding.UTF8.GetString(Convert.FromBase64String(joined)); }
                    catch (FormatException) { return body; }
                }

                if (headers.Contains("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase))
                    return DecodeQuotedPrintable(body);

                return body;
            }
        }

        private static string DecodeQuotedPrintable(string body)
        {
            // Les « soft line breaks » (`=` en fin de ligne) recollent une ligne coupée à 76
            // colonnes : les ignorer découperait le lien en deux au milieu du jeton.
            var joined = body.Replace("=\n", "").Replace("=\r\n", "");
            var bytes = new List<byte>();

            for (var i = 0; i < joined.Length; i++)
            {
                if (joined[i] == '=' && i + 2 < joined.Length &&
                    byte.TryParse(joined.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                        null, out var decoded))
                {
                    bytes.Add(decoded);
                    i += 2;
                }
                else
                {
                    bytes.Add((byte)joined[i]);
                }
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
