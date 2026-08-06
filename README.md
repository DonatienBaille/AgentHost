# Agent Host

Plateforme d'hébergement et d'exécution d'agents autonomes — implémentation de la
spécification v2.0 ("Agent Host — Spécification complète v2.0").

## Stack

- **Backend** : .NET 8 (LTS) — ASP.NET Core Minimal APIs, SignalR, Dapper + Npgsql (PostgreSQL 16),
  Docker.DotNet (orchestration de conteneurs — **Docker ou Podman**), AWSSDK.S3 (stockage objet des
  artefacts, optionnel), Serilog, OpenTelemetry, FluentValidation, YamlDotNet.
- **Frontend** : Angular 21 (standalone components, Signals, new control flow), Tailwind CSS 4,
  `@microsoft/signalr`, ngx-translate (FR/EN).
- **Infra** : Docker Compose (dev), Helm chart (Kubernetes, mono-réplique — voir « Limites
  connues »).

> **Note de version** : la spécification cible .NET 11 et Angular 22. Le code utilise les versions
> stables réellement disponibles — **.NET 8 LTS** et **Angular 21** (`@angular/core ^21.2.0`) — avec
> les mêmes patterns architecturaux (Minimal APIs, Signals, standalone components, new control flow,
> esbuild) ; la migration vers .NET 11 / Angular 22 sera un upgrade mécanique.

## Structure du repo

```
backend/              ASP.NET Core API (AgentHost.Api) + tests
frontend/             Angular SPA
migrations/           Schéma PostgreSQL (section 5 de la spec)
charts/agenthost/     Helm chart (Kubernetes)
docs/                 Protocole agent, modèle d'authentification, notes d'implémentation
docker-compose.yml    Stack dev/local complète
docker-compose.podman.yml  Overlay Podman (voir « Runtime de conteneurs »)
Dockerfile.backend
Dockerfile.frontend
nginx.conf
```

## Déploiement Kubernetes

```bash
helm template charts/agenthost \
  --set secrets.jwtSecret=... \
  --set secrets.encryptionKey=... \
  --set postgres.auth.password=...
```

Les trois secrets sont **obligatoires** : le chart ne fournit aucune valeur par défaut (un mot de
passe par défaut est un identifiant par défaut dans toute installation qui oublie de le changer).
`postgres.auth.password` n'est requis que si le chart construit lui-même la chaîne de connexion —
un déploiement qui fournit `secrets.connectionString` pour une base externe n'en a pas besoin.

## Démarrage rapide (dev local)

```bash
docker compose up --build
```

- Frontend : http://localhost:4200
- Backend API : http://localhost:5000/api
- Liveness : http://localhost:5000/health/live (processus vivant ; `/health` en est un alias)
- Readiness : http://localhost:5000/health/ready (PostgreSQL joignable)
- Métriques Prometheus : http://localhost:5000/metrics — **non exposées** via nginx (le
  frontend renvoie 404 sur `/metrics`) : scraper le backend directement.

Le conteneur backend tourne en non-root (uid 64198). Les répertoires bind-montés doivent lui
appartenir :

```bash
mkdir -p runs artifacts && sudo chown -R 64198:64198 runs artifacts
```

## Runtime de conteneurs : Docker ou Podman

Podman expose l'API REST de Docker, et l'orchestrateur n'émet que des appels servis à l'identique
par les deux (pull d'image, create/start/wait/logs/remove, listing par label). **Il n'y a donc
volontairement aucun réglage « runtime » à positionner** : un seul chemin de code pilote les deux.
Ce qui diffère réellement se limite à deux choses, et les deux sont configurables.

### 1. L'emplacement du socket

`Docker:Host` reste prioritaire. **Laissé vide, le socket est détecté** dans cet ordre (les chemins
rootless d'abord dès que le processus ne tourne pas en uid 0, car les sockets rootful sont
typiquement en 0660 root:root) :

| Chemin | Runtime |
| --- | --- |
| `/var/run/docker.sock` | Docker rootful |
| `/run/podman/podman.sock` | Podman rootful (`systemctl enable --now podman.socket`) |
| `$XDG_RUNTIME_DIR/podman/podman.sock` (à défaut `/run/user/<uid>/podman/podman.sock`) | Podman rootless (`podman system service --time=0`) |
| `$XDG_RUNTIME_DIR/docker.sock` | Docker rootless |

Sans rien trouver, la valeur historique `unix:///var/run/docker.sock` est conservée. Le runtime
déduit du chemin est **journalisé au démarrage** mais n'est utilisé par aucune décision.

`tecnativa/docker-socket-proxy` filtre un socket Podman exactement comme un socket Docker : c'est un
filtre HTTP devant un socket qui parle l'API Docker. `docker-compose.podman.yml` s'en sert tel quel.

```bash
# Rootful (recommandé — sémantique d'uid identique à Docker)
sudo systemctl enable --now podman.socket
docker compose -f docker-compose.yml -f docker-compose.podman.yml up --build
```

### 2. La propriété des fichiers montés (rootless)

En **rootless**, le backend et les conteneurs d'agents ne voient pas les mêmes uid pour un même
fichier : `chown 64198` sur l'hôte n'est pas ce que voit le conteneur (il faut
`podman unshare chown -R 64198:64198 runs artifacts`, ou le suffixe de montage `:U`), et un agent
tournant en uid 0 dans son propre namespace retombe sur *votre* uid hôte — pas sur 64198 — donc il
ne peut pas lire les fichiers de secrets en 0400.

`Docker:BindMountOptions` (liste séparée par des virgules, ajoutée à **tous** les binds des
conteneurs d'agents) permet de traiter ces cas : `z` pour le relabel SELinux (famille Fedora/RHEL),
`U` pour que Podman chowne l'arborescence montée vers l'utilisateur du conteneur.

**Le compromis, sans enjolivement** : `U` réécrit la propriété du répertoire que le backend doit
ensuite supprimer en fin de run (le nettoyage de `/run/secrets`). Si des erreurs de suppression de
secrets apparaissent dans les logs en rootless, c'est cela — la réponse est le service **rootful**,
ou faire tourner le backend lui-même avec `--userns=keep-id` pour que les deux côtés partagent un
uid. C'est une propriété des user namespaces rootless, pas quelque chose qu'AgentHost peut masquer.

### Ce qui a été corrigé pour Podman

- `HostConfig.CPUCount` est un champ **Windows** : Docker Linux **et** Podman l'ignorent, donc
  `spec.runtime.cpu` ne limitait strictement rien. Remplacé par `NanoCPUs` (cœurs × 1e9), le
  mécanisme Linux portable.
- `CapDrop` utilise désormais `ALL` (l'orthographe normalisée par les deux moteurs) au lieu de `all`.
- `Docker:SecurityOpt` est configurable (défaut `no-new-privileges=true`) : l'orthographe acceptée a
  varié selon les moteurs et les versions ; un Podman ancien qui refuse cette forme se règle sans
  toucher au code.

**Ce qui est vérifié, et sur quel moteur** : aucun runtime de conteneurs n'est joignable dans
l'environnement de développement de ce dépôt, mais la CI (`ubuntu-latest`) en a un.
`ContainerLifecycleTests` y lance un vrai conteneur et **observe sur le démon** l'acceptation de
`no-new-privileges=true`, de `CapDrop: ALL`, du rootfs en lecture seule et de `NanoCPUs` — voir
« Couverture : ce qui est exécuté » plus bas. Restent **non observés sur un démon réel** : Podman
(la CI utilise Docker ; le code ne branche sur aucun des deux, mais l'acceptation des mêmes options
par une version donnée de Podman reste un raisonnement) et les suffixes de montage `:z`/`:U`.

## Chemins de bind mount (backend conteneurisé)

La source d'un bind mount est résolue par le **démon**, jamais par le processus qui émet l'appel
API. Quand le backend tourne lui-même dans un conteneur, les deux ne désignent pas la même chose :
le backend écrit dans `/var/agenthost/runs` (son propre point de montage), le démon doit monter
`/chemin/hôte/runs`. Envoyer le premier au démon ne produit **aucune erreur** — il crée un
répertoire vide — donc l'agent démarre avec un `/workspace` vide et surtout un `/run/secrets` vide.

Deux réglages distincts :

| Clé | Signification |
| --- | --- |
| `Docker:WorkspacePath` | Chemin que **ce processus** lit et écrit. |
| `Docker:HostWorkspacePath` | Chemin auquel **le démon** voit le même répertoire. Vide = identique (cas bare-metal, comportement inchangé). |

Un chemin situé hors de `Docker:WorkspacePath` est **refusé** plutôt que deviné : un bind mount
erroné est silencieux, une exception ne l'est pas.

`docker-compose.yml` positionne `Docker__HostWorkspacePath: ${PWD}/runs`, ce qui rend la stack
compose réellement capable de lancer des agents (ce n'était pas le cas). Surcharger avec
`AGENTHOST_HOST_RUNS_PATH` si le démon est distant ou voit le dépôt ailleurs.

En Kubernetes : `docker.hostWorkspacePath` dans le chart. Attention, ce réglage n'a de sens que si
le **nœud** possède réellement ce chemin — c'est-à-dire avec un volume `runs` de type hostPath monté
au même chemin absolu. Avec un PVC réseau, le démon du nœud n'a aucun chemin vers ce système de
fichiers et les bind mounts d'agents ne peuvent pas fonctionner ; c'est une conséquence de confier
un chemin d'un autre système de fichiers au démon d'un nœud, pas quelque chose que le chart peut
contourner.

## Stockage des artefacts : disque local ou S3

`Artifacts:Provider` sélectionne l'implémentation d'`IArtifactStorage` :

| Valeur | Comportement |
| --- | --- |
| `local` (défaut) | Fichiers sous `Artifacts:StoragePath`. `artifacts.s3_path` contient le chemin absolu — comportement historique, inchangé. |
| `s3` | Objet dans un stockage compatible S3 (AWS S3, MinIO, Ceph RGW). `artifacts.s3_path` contient la clé `artifacts/{runId}/{artifactId}-{nom}`. |

Aucune migration de schéma : la colonne existe déjà et garde une valeur significative pour chaque
backend.

```jsonc
"Artifacts": {
  "Provider": "s3",
  "S3": {
    "Bucket": "agenthost-artifacts",
    "ServiceUrl": "http://minio:9000",   // vide = AWS S3 réel, adressé par Region
    "Region": "eu-west-3",
    "AccessKey": "", "SecretKey": "",     // vides = chaîne d'identifiants ambiante (IRSA, rôle d'instance)
    "ForcePathStyle": "",                 // défaut : true dès que ServiceUrl est renseigné
    "KeyPrefix": "artifacts"
  }
}
```

Une configuration `s3` incomplète **échoue au démarrage** avec la liste complète des problèmes,
plutôt qu'au premier upload avec un seul symptôme.

- **Upload** : streamé (`TransferUtility`, multipart au-delà du seuil) — un artefact volumineux n'est
  jamais matérialisé en mémoire. La taille enregistrée est celle réellement écrite, pas l'en-tête.
- **Download** : streamé **à travers l'API**, pas par URL présignée. Le magasin est souvent un
  MinIO/Ceph interne au cluster, injoignable depuis le navigateur ; et une URL présignée est une
  capacité porteuse qui survit à la requête, hors du contrôle d'organisation que l'endpoint vient
  d'effectuer. Le raisonnement complet est en commentaire dans `S3ArtifactStorage`.
- **Rétention** (`Retention:ArtifactDays`) passe par l'abstraction et fonctionne sur les deux
  backends. Sur S3, une règle de cycle de vie du bucket fait le même travail côté serveur.

Dans le chart : `artifacts.provider=s3` fait **disparaître** le PVC d'artefacts et son montage
(`values-podman.yaml` en donne un exemple complet). C'est le **prérequis** d'un service d'artefacts
multi-réplique, pas sa livraison : l'orchestrateur reste lié à son nœud, donc `replicaCount` reste 1.

**Non testé ici** : les entrées/sorties réelles vers S3. Aucun magasin objet n'est joignable dans cet
environnement, et un test contre un faux endpoint HTTP vérifierait le comportement du SDK AWS, pas le
nôtre. Sont couverts par des tests : l'implémentation locale de bout en bout, la dérivation des clés
(locale et S3) et la validation de configuration S3.

## Développement backend

```bash
cd backend
dotnet build
dotnet test
dotnet run --project src/AgentHost.Api
```

`dotnet test` a besoin du PostgreSQL local (les tests d'intégration démarrent le vrai pipeline
`Program`). Sans démon de conteneurs, les tests de `ContainerLifecycleTests` se déclarent
**SKIPPED** avec la raison et le socket sondé — jamais « passés ». Avec un démon joignable
(`docker version` répond), ils s'exécutent : comptez ~20 s de plus, un pull d'`alpine:3.20` et une
image locale `localhost:5000/agenthost-e2e-agent:*` supprimée en fin de test.

## Couverture : ce qui est exécuté, ce qui est raisonné

Le chemin cœur du produit — lancer un agent dans un conteneur — a longtemps été le moins testé :
tous les tests de cycle de vie de run écrivaient en base l'état qu'un conteneur *aurait* produit
(c'est toujours le cas de `ParkRunAsRunningAsync`, et c'est utile : cela couvre le protocole
indépendamment d'un runtime). Un bug d'encodage a pu, dans ce dépôt, faire renvoyer un dictionnaire
de secrets vide pendant toute la vie du projet sans qu'aucun test ne le remarque.

`backend/tests/AgentHost.Api.Tests/Integration/ContainerLifecycleTests.cs` exécute désormais ce
chemin pour de bon, une fois, contre un vrai démon (en CI sur `ubuntu-latest` ; localement dès qu'un
démon répond). L'agent est `alpine` plus un script shell, construit à la volée via l'endpoint
`/build` du démon — aucune image de fixture dans le dépôt, aucun registre. Le conteneur joint l'API
par la **gateway du bridge Docker**, lue sur le démon et non codée en dur, l'application étant
servie en plus sur Kestrel (un `TestServer` n'a aucun socket à composer).

**Réellement exécuté et vérifié** :

| Étape | Vérification |
| --- | --- |
| Création du conteneur | Flags §13.2 tels que le démon les a enregistrés : `ReadonlyRootfs`, `CapDrop: ALL`, `CapAdd: NET_BIND_SERVICE`, `SecurityOpt`, tmpfs `/tmp`, `NetworkMode` |
| Limites du manifeste | `NanoCPUs` et `Memory`/`MemorySwap` (sans swap) comparés au profil résolu du run |
| Livraison des secrets | Le conteneur lit `/run/secrets/<NOM>` et compare la valeur, octet pour octet, à celle stockée par `POST /api/secrets` |
| Secrets hors environnement | Aucune variable `SECRET_*` — vérifié dans le conteneur *et* dans la config vue par `docker inspect` |
| Protocole agent | `POST /api/agent/runs/{id}/events` avec `AGENTHOST_RUN_TOKEN`, **depuis le conteneur**, relu par un humain via `GET /api/runs/{id}/events` |
| `/workspace` | Bind mount inscriptible : le fichier écrit par l'agent est relu sur l'hôte |
| Fin de run | Sortie 0 → `succeeded`, `exit_code`, `started_at`/`finished_at` |
| Nettoyage | Conteneur supprimé, **répertoire de secrets en clair effacé** (garantie de sécurité, jamais vérifiée jusqu'ici) |

**Non exécuté, raisonné seulement** : Podman (la CI n'a que Docker), les suffixes de montage
`:z`/`:U`, `Docker:HostWorkspacePath` avec un démon distant, l'égress `allowlist` via proxy (voir
plus bas), et la collecte de logs conteneur au-delà du fait qu'elle ne fait pas échouer le run.

La CI échoue si ces tests se contentent de *skipper* : un test sauté se présente comme un succès, ce
qui est exactement l'illusion que cette suite existe pour supprimer.

## Développement frontend

```bash
cd frontend
npm install
npm start        # dev server sur http://localhost:4200
npm run build     # build production
```

## Sécurité opérationnelle

### Isolation des agents (spec §13.2)

Chaque run s'exécute dans un conteneur avec `no-new-privileges`, toutes les capabilities
supprimées, mémoire plafonnée sans swap, **CPU réellement plafonné** (`NanoCPUs` ; l'ancien
`CPUCount` était un champ Windows ignoré sous Linux — la limite ne s'appliquait pas), **rootfs en
lecture seule** (`/workspace` bind-monté en écriture + tmpfs sur `/tmp`), et DNS configurable
(`Docker:Dns` — vide par défaut, donc résolution du démon, aucun résolveur tiers).

Un agent qui a réellement besoin d'un rootfs inscriptible doit le demander explicitement :

```yaml
spec:
  permissions:
    writableRootfs: true
```

**Secrets** : livrés **uniquement** sous forme de fichiers `/run/secrets/<NOM>` (0400, répertoire
0700), jamais en variables d'environnement — `docker inspect` et `/proc/1/environ` les exposeraient.
Le répertoire est supprimé dès la sortie du conteneur ; un balayage périodique
(`Retention:SecretsGraceMinutes`) nettoie ceux qu'un crash aurait laissés. Voir
`docs/agent-protocol.md`.

Ces trois propriétés — flags de durcissement acceptés par le démon, secret lisible dans le
conteneur au bon contenu et absent de son environnement, répertoire en clair effacé après la sortie
— ne sont pas seulement documentées : elles sont **assertées sur un vrai conteneur** par
`ContainerLifecycleTests` (voir « Couverture : ce qui est exécuté »).

**Rétention** : `Retention:WorkspaceHours` (7 j par défaut) purge les workspaces ;
`Retention:ArtifactDays` est désactivé par défaut, car les lignes `artifacts` en base référencent
ces fichiers — ne l'activer qu'avec une politique de rétention BD correspondante.

### `network: allowlist` — ce qui est réellement appliqué

`permissions.network` accepte `none`, `allowlist` et `full`. `allowlist` n'est plus un synonyme de
`full` :

| Valeur | Effet |
| --- | --- |
| `none` | `NetworkMode=none` : aucune interface réseau. |
| `full` | `NetworkMode=bridge` : egress non filtré. |
| `allowlist` | Réseau + `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` pointant sur `Docker:EgressProxy`. Les hôtes de `permissions.networkAllowlist` sont transmis à la couche proxy via le label `agenthost.network_allowlist`. |

```yaml
spec:
  permissions:
    network: allowlist
    networkAllowlist: ["api.github.com", "registry.npmjs.org"]
```

**Ce qui est garanti** : sans `Docker:EgressProxy` configuré (ou avec une allowlist vide), le run
démarre **sans réseau du tout** — fail closed, jamais de dégradation silencieuse vers `full`.

**Ce qui ne l'est pas** : un proxy HTTP ne contraint que les clients qui le respectent. Un processus
qui ouvre une socket TCP directe vers une IP ignore `HTTP_PROXY`. Pour une application réelle, il
faut que le réseau du conteneur ne route nulle part ailleurs que vers le proxy : créer un réseau
Docker `--internal` où seul le proxy est joignable et le désigner via `Docker:AllowlistNetwork`, ou
filtrer le sous-réseau bridge en amont. L'orchestrateur ne peut pas le faire par conteneur sans
sidecar proxy dédié.

### Accès au socket du runtime (Docker ou Podman)

L'API pilote un démon de conteneurs : quiconque atteint ce socket est root sur l'hôte — y compris un
socket Podman **rootful**, qui donne exactement les mêmes pouvoirs. Deux mesures :

1. Le conteneur backend tourne en non-root (uid 64198).
2. `docker-compose.yml` ne bind-monte plus `/var/run/docker.sock` dans le backend. Un
   `tecnativa/docker-socket-proxy` s'intercale et ne laisse passer que les endpoints
   containers/images réellement utilisés ; `Docker__Host` pointe dessus. Le même proxy fronte un
   socket Podman sans modification (`docker-compose.podman.yml`).

**Risque résiduel, honnêtement** : c'est une atténuation, pas une élimination. « Créer et démarrer
un conteneur » suffit encore à s'évader vers l'hôte (rien n'empêche une requête demandant un
conteneur privilégié ou un bind mount de `/`). Une isolation réelle demande un démon dédié par
tenant, une VM, ou un runtime type gVisor/Kata. Dans le chart Helm, `docker.mountSocket` monte le
socket du nœud par défaut ; préférer un proxy filtrant déployé séparément et `docker.host`.

### Limites connues

- **Le backend n'est pas scalable horizontalement.** L'orchestrateur parle au démon de son propre
  nœud (`StopAsync`/`GetLogsAsync` cherchent le conteneur sur *ce* démon) et les workspaces sont des
  fichiers locaux. Le chart est donc `replicaCount: 1`, HPA désactivé. Le backplane SignalR Redis et
  le stockage S3 des artefacts sont en place — deux prérequis du scale-out — mais ne suffisent pas :
  il faut d'abord sortir l'orchestrateur dans un tier « runner » distinct.
- **Bind mounts d'agents en Kubernetes avec un PVC réseau.** `Docker:HostWorkspacePath` (voir
  « Chemins de bind mount ») résout le cas Docker-in-Docker / compose, mais il suppose que le démon
  possède un chemin vers le répertoire des runs. Avec un PVC réseau ce chemin n'existe pas côté
  nœud : il faut un volume `runs` de type hostPath, ou le tier « runner » évoqué ci-dessus.
- **X-Forwarded-For** n'est pris en compte que pour les proxys déclarés
  (`ForwardedHeaders:KnownProxies` / `:KnownNetworks`, `forwardedHeaders.*` dans le chart). Sans
  déclaration, tous les appelants anonymes partagent le bucket de rate limiting du proxy.

## État d'implémentation (voir section 18 de la spec — phases de livraison)

- **P0 — Socle** : API REST, PostgreSQL, orchestration Docker, SignalR, UI Angular de base. ✅
- **P1 — Agents** : agents OCI + externes, mémoire agentique v1, approvals, protocole agent
  (`docs/agent-protocol.md`), watchdog de runs. ✅
- **P2 — Production** : durcissement conteneurs (§13.2), secrets chiffrés AES-GCM livrés par
  fichier, JWT + RBAC, rate limiting, chart Helm avec volumes/PVC, logging structuré Serilog,
  métriques Prometheus (`/metrics`) et traces OTLP (§14). ✅
  Non fournis : le déploiement des backends d'observabilité eux-mêmes (Grafana/Loki/Jaeger) et
  l'agrégation de logs — l'application expose les données, la plateforme reste à câbler.
- **P3 — Optimisation** : non couvert (scale-out du tier runner, cache Redis applicatif,
  optimisations de coût).
