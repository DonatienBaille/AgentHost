# Configuration

Toutes les clés de configuration du backend, leur défaut, et ce que change chaque valeur.

**Sources**, dans l'ordre de priorité croissante : `appsettings.json` → `appsettings.{Environment}.json`
→ variables d'environnement → arguments de ligne de commande. En variable d'environnement, le
séparateur `:` s'écrit `__` : `Docker:HostWorkspacePath` devient `Docker__HostWorkspacePath`.

`appsettings.json` documente chaque clé sur place, dans une entrée `"//Nom"` voisine. Ce fichier-ci
est la vue d'ensemble ; le fichier reste la référence au plus près du code.

---

## Obligatoire

Rien ne démarre sans ces trois valeurs. Aucune n'a de défaut, et c'est délibéré : **un secret par
défaut est un secret partagé par toutes les installations qui oublient de le changer.**

| Clé | Rôle |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | PostgreSQL 16. |
| `Jwt:Secret` | Signature des jetons d'accès. Le changer invalide toutes les sessions. |
| `Secrets:EncryptionKey` | Clé AES-256 (base64, 32 octets) des secrets et des seeds TOTP. **La perdre rend les secrets définitivement illisibles.** |

```bash
openssl rand -base64 32   # pour Jwt:Secret comme pour Secrets:EncryptionKey
```

---

## Base de données et cache

| Clé | Défaut | Effet |
| --- | --- | --- |
| `ConnectionStrings:DefaultConnection` | — | Chaîne Npgsql. |
| `Redis:Host` | vide | Vide = pas de Redis. |
| `Redis:Port` | `6379` | |

Redis est **facultatif** : backplane SignalR (indispensable dès plusieurs répliques) et cache
d'agrégats des endpoints de métriques. Injoignable, il n'empêche jamais le démarrage — le cache
retombe sur la fabrique et le backplane sur le mode mono-processus.

---

## Runtime de conteneurs

| Clé | Défaut | Effet |
| --- | --- | --- |
| `Docker:Host` | vide | Socket du démon. Vide = **détection automatique** (voir [containers.md](containers.md)). |
| `Docker:WorkspacePath` | `/var/agenthost/runs` | Chemin que **ce processus** lit et écrit. |
| `Docker:HostWorkspacePath` | vide | Chemin auquel **le démon** voit le même répertoire. Vide = identique. |
| `Docker:BindMountOptions` | vide | Options ajoutées à tous les binds : `z` (SELinux), `U` (Podman rootless). |
| `Docker:Runtime` | vide | `runsc` (gVisor), `kata-runtime`, `sysbox-runc`. Vide = runc, qui **partage le noyau de l'hôte**. |
| `Docker:SecurityOpt` | `no-new-privileges=true` | Ne surcharger que si le moteur refuse cette orthographe. |
| `Docker:Dns` | vide | Vide = résolution du démon. |
| `Docker:EgressProxy` | vide | Proxy pour `permissions.network: allowlist`. **Non renseigné, ces runs n'ont aucun réseau** (fail closed). |
| `Docker:AllowlistNetwork` | vide | Réseau `--internal` où seul le proxy est joignable. |
| `Docker:AllowUnconfinedAllowlist` | `false` | Autorise `allowlist` sans confinement réseau. À n'activer qu'en connaissance de cause. |
| `Docker:TmpfsSizeMb` | `64` | Taille du tmpfs `/tmp` des agents. |

`Docker:HostWorkspacePath` mérite une explication : la source d'un bind mount est résolue par le
**démon**, jamais par le processus qui émet l'appel. Quand le backend tourne lui-même en conteneur,
les deux ne désignent pas la même chose — et envoyer le mauvais chemin ne produit **aucune erreur**,
seulement un `/workspace` et un `/run/secrets` vides. Voir [containers.md](containers.md).

---

## Tier runner

| Clé | Défaut | Effet |
| --- | --- | --- |
| `Runner:Mode` | `inprocess` | `inprocess` ou `remote`. |
| `Runner:BaseUrl` | vide | URL du service runner (mode `remote`). |
| `Runner:AuthToken` | vide | Jeton partagé backend ↔ runner. |
| `Runner:WaitTimeoutSeconds` | `30` | |
| `Runner:RequestTimeoutSeconds` | `30` | |
| `Runner:MaxConsecutiveWaitFailures` | `5` | Au-delà, le run part en `infra_error` plutôt que de rester suspendu. |

Le mode `remote` est ce qui rend le backend réplicable. Voir [runner.md](runner.md).

---

## Artefacts

| Clé | Défaut | Effet |
| --- | --- | --- |
| `Artifacts:Provider` | `local` | `local` ou `s3`. |
| `Artifacts:StoragePath` | vide | Racine du stockage local. |
| `Artifacts:S3:Bucket` | — | Requis en `s3`. |
| `Artifacts:S3:ServiceUrl` | vide | Vide = AWS S3 réel. Renseigné = MinIO, Ceph RGW… |
| `Artifacts:S3:Region` | vide | |
| `Artifacts:S3:AccessKey` / `SecretKey` | vide | Vides = chaîne d'identifiants ambiante (IRSA, rôle d'instance). |
| `Artifacts:S3:ForcePathStyle` | auto | `true` dès que `ServiceUrl` est renseigné. |
| `Artifacts:S3:KeyPrefix` | `artifacts` | |

Une configuration `s3` incomplète **échoue au démarrage** avec la liste complète des problèmes,
plutôt qu'au premier dépôt d'artefact avec un seul symptôme.

---

## Courriel

| Clé | Défaut | Effet |
| --- | --- | --- |
| `Email:Provider` | `none` | `none` (n'envoie rien) ou `smtp`. |
| `Email:FromAddress` | vide | Requis en `smtp`. |
| `Email:FromName` | `AgentHost` | |
| `Email:AppBaseUrl` | `http://localhost:4200` | Racine publique du front — c'est elle qui rend les liens utilisables. |
| `Email:ResetPasswordPath` | `/reset-password` | |
| `Email:AcceptInvitationPath` | `/accept-invitation` | |
| `Email:Language` | `fr` | `fr` ou `en`. |
| `Email:Smtp:Host` | vide | |
| `Email:Smtp:Port` | `587` | |
| `Email:Smtp:Security` | `starttls` | `starttls` (587), `ssl` (465, TLS implicite), `none`. |
| `Email:Smtp:UserName` / `Password` | vide | Vides = pas d'authentification. |
| `Email:Smtp:TimeoutSeconds` | `15` | |

Trois propriétés valent d'être connues :

- **Rien ne s'active par accident** : il faut avoir écrit `smtp` explicitement. Une configuration
  `smtp` incomplète **retombe sur le no-op** avec une raison lisible, plutôt que d'empêcher le
  démarrage — un mailer mal réglé ne doit pas transformer une fonctionnalité annexe en dépendance du
  service entier.
- **Le chiffrement ne se dégrade jamais en silence.** Toute valeur de `Security` autre que `none`
  chiffre, et les deux modes chiffrants sont *exigeants* : ils échouent si le serveur n'offre pas
  TLS, au lieu de retomber en clair.
- **L'envoi n'est jamais sur le chemin de réponse HTTP.** C'est une propriété de sécurité (§ oracle
  d'énumération, voir [auth.md](auth.md) §4), pas une optimisation.

---

## Authentification

| Clé | Défaut | Effet |
| --- | --- | --- |
| `Jwt:Secret` | — | **Obligatoire.** |
| `Jwt:Issuer` / `Jwt:Audience` | `agenthost` | |
| `Jwt:ExpiryMinutes` | `15` | Durée du jeton d'accès. Le jeton de rafraîchissement est révocable, pas lui. |
| `Auth:AllowSelfRegistration` | `true` | `false` = les organisations sont créées hors bande. |
| `Auth:ReturnResetTokenInResponse` | `false` | **Développement uniquement.** Rend le jeton brut dans la réponse. |
| `Auth:BreachedPasswordCheck:Enabled` | `false` | Vérification k-anonyme contre Have I Been Pwned. |
| `Auth:BreachedPasswordCheck:BaseUrl` | `https://api.pwnedpasswords.com` | |
| `Auth:BreachedPasswordCheck:TimeoutSeconds` | `2` | Court : le service indisponible ne doit pas bloquer une inscription. |
| `Auth:BreachedPasswordCheck:MinimumOccurrences` | `1` | Seuil de refus. |

---

## Secrets

| Clé | Défaut | Effet |
| --- | --- | --- |
| `Secrets:EncryptionKey` | — | **Obligatoire.** Clé courante, utilisée en écriture. |
| `Secrets:PreviousEncryptionKeys` | vide | Anciennes clés, acceptées **en lecture seule**. |

La rotation est en trois temps, et l'ordre n'est pas négociable — voir [operations.md](operations.md)
§4. Tant qu'une clé fuitée figure dans `PreviousEncryptionKeys`, la rotation n'a rien réglé.

---

## Rétention

| Clé | Défaut | Effet |
| --- | --- | --- |
| `Retention:SweepIntervalMinutes` | `60` | Cadence du balayage. |
| `Retention:SecretsGraceMinutes` | `60` | Nettoyage des répertoires de secrets qu'un crash aurait laissés. |
| `Retention:WorkspaceHours` | `168` (7 j) | Purge des workspaces. |
| `Retention:ArtifactDays` | `0` (désactivé) | **Les lignes `artifacts` référencent ces fichiers** — n'activer qu'avec une politique BD correspondante. |
| `Retention:TriggerDeliveryDays` | `30` | Historique des livraisons de webhooks entrants. `0` = désactivé. |

---

## Observabilité et réseau

| Clé | Défaut | Effet |
| --- | --- | --- |
| `OpenTelemetry:ServiceName` | `agenthost-backend` | |
| `OpenTelemetry:OtlpEndpoint` | vide | Vide = pas d'export de traces. |
| `Cors:AllowedOrigins` | vide | Vide = aucune origine croisée autorisée. |
| `ForwardedHeaders:KnownProxies` | vide | |
| `ForwardedHeaders:KnownNetworks` | vide | |

**`X-Forwarded-For` n'est honoré que pour les proxys déclarés.** Sans déclaration, tous les appelants
anonymes partagent le même compartiment de limitation de débit — celui du proxy. Faire confiance à
un en-tête non déclaré permettrait à n'importe qui de se choisir une identité de limitation.

---

## Limitation de débit

Pas de clé de configuration : les seuils sont dans `Program.cs` (120 requêtes/minute en général,
plus strict sur les endpoints d'authentification). La file est **de taille nulle** — le limiteur
refuse au lieu de mettre en attente. Un limiteur qui met en file immobilise les threads de
traitement, c'est-à-dire produit le déni de service qu'il devait éviter.

---

## Commandes hors ligne

Trois opérations n'ont pas d'endpoint, et c'est délibéré : il n'y a ni appelant à autoriser, ni
locataire à scoper.

```bash
dotnet AgentHost.Api.dll --rekey-secrets        # rechiffre avec la clé courante
dotnet AgentHost.Api.dll --purge-org <orgId>    # effacement RGPD définitif
```

Codes de sortie et détails dans [operations.md](operations.md).
