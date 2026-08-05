# Agent Host

Plateforme d'hébergement et d'exécution d'agents autonomes — implémentation de la
spécification v2.0 ("Agent Host — Spécification complète v2.0").

## Stack

- **Backend** : .NET 8 (LTS) — ASP.NET Core Minimal APIs, SignalR, Dapper + Npgsql (PostgreSQL 16),
  Docker.DotNet (orchestration de conteneurs), Serilog, OpenTelemetry, FluentValidation, YamlDotNet.
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
docs/                 Protocole agent, notes d'implémentation
docker-compose.yml    Stack dev/local complète
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

## Développement backend

```bash
cd backend
dotnet build
dotnet test
dotnet run --project src/AgentHost.Api
```

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
supprimées, mémoire plafonnée sans swap, **rootfs en lecture seule** (`/workspace` bind-monté en
écriture + tmpfs sur `/tmp`), et DNS configurable (`Docker:Dns` — vide par défaut, donc résolution
du démon Docker, aucun résolveur tiers).

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

### Accès au démon Docker

L'API pilote un démon Docker : quiconque atteint ce socket est root sur l'hôte. Deux mesures :

1. Le conteneur backend tourne en non-root (uid 64198).
2. `docker-compose.yml` ne bind-monte plus `/var/run/docker.sock` dans le backend. Un
   `tecnativa/docker-socket-proxy` s'intercale et ne laisse passer que les endpoints
   containers/images réellement utilisés ; `Docker__Host` pointe dessus.

**Risque résiduel, honnêtement** : c'est une atténuation, pas une élimination. « Créer et démarrer
un conteneur » suffit encore à s'évader vers l'hôte (rien n'empêche une requête demandant un
conteneur privilégié ou un bind mount de `/`). Une isolation réelle demande un démon dédié par
tenant, une VM, ou un runtime type gVisor/Kata. Dans le chart Helm, `docker.mountSocket` monte le
socket du nœud par défaut ; préférer un proxy filtrant déployé séparément et `docker.host`.

### Limites connues

- **Le backend n'est pas scalable horizontalement.** L'orchestrateur parle au démon Docker de son
  propre nœud (`StopAsync`/`GetLogsAsync` cherchent le conteneur sur *ce* démon) et les workspaces
  sont des fichiers locaux. Le chart est donc `replicaCount: 1`, HPA désactivé. Le backplane SignalR
  Redis est en place (prérequis du scale-out) mais ne suffit pas : il faut d'abord sortir
  l'orchestrateur dans un tier « runner » distinct.
- **Chemins de bind mount en Docker-in-Docker.** Les chemins de `Docker:WorkspacePath` sont résolus
  par le **démon** (donc sur l'hôte), pas dans le conteneur backend. Dans la stack compose, `./runs`
  est monté sur `/var/agenthost/runs` côté backend mais le démon ne connaît pas ce chemin : pour
  lancer réellement des agents depuis la stack compose, monter le répertoire des runs au **même
  chemin absolu** côté hôte et côté conteneur.
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
