# Agent Host

Plateforme d'hébergement et d'exécution d'**agents autonomes** : elle exécute du code tiers, dans des
conteneurs isolés, pour le compte de plusieurs organisations sur une même installation.
Implémentation de la spécification v2.0.

[![CI](https://github.com/DonatienBaille/AgentHost/actions/workflows/ci.yml/badge.svg)](https://github.com/DonatienBaille/AgentHost/actions/workflows/ci.yml)

---

## Ce que ça fait

- **Exécuter des agents** décrits par un manifeste YAML (ou par l'IHM), dans un conteneur durci :
  rootfs en lecture seule, aucune capability, CPU et mémoire plafonnés, réseau au choix.
- **Livrer des secrets** chiffrés (AES-256-GCM) par **fichier**, jamais par variable
  d'environnement, et les effacer à la sortie du conteneur.
- **Suivre les runs en temps réel** (SignalR), avec approbations humaines, budget par run, chien de
  garde et journal d'événements.
- **Déclencher** par API, par **webhook entrant** (GitHub, GitLab, générique) ou par **cron**, et
  enchaîner les runs entre eux.
- **Rendre des comptes** : journal d'audit append-only consultable et filtrable, métriques
  Prometheus, traces OTLP, et effacement RGPD réel.

Le tout est **multi-locataire** : l'isolation est portée par le SQL, et une ressource d'un autre
locataire répond 404 — jamais 403.

---

## Démarrage rapide

```bash
docker compose up --build
mkdir -p runs artifacts && sudo chown -R 64198:64198 runs artifacts   # le backend tourne en non-root
```

| Service | URL |
| --- | --- |
| Frontend | http://localhost:4200 |
| API | http://localhost:5000/api |
| Liveness | http://localhost:5000/health/live (`/health` en est un alias) |
| Readiness | http://localhost:5000/health/ready (PostgreSQL joignable) |
| Métriques | http://localhost:5000/metrics — **non exposées** par nginx : scraper le backend directement |

Trois valeurs n'ont **aucun défaut** et doivent être fournies :
`ConnectionStrings:DefaultConnection`, `Jwt:Secret`, `Secrets:EncryptionKey`. Un secret par défaut est
un secret partagé par toutes les installations qui oublient de le changer. Voir
[docs/configuration.md](docs/configuration.md).

---

## Stack

| Couche | Technologies |
| --- | --- |
| **Backend** | .NET 8 LTS — ASP.NET Core Minimal APIs, SignalR, Dapper + Npgsql (PostgreSQL 16), Docker.DotNet (Docker **ou** Podman), MailKit, AWSSDK.S3 *(optionnel)*, StackExchange.Redis *(optionnel)*, Serilog, OpenTelemetry, FluentValidation, Polly, YamlDotNet |
| **Frontend** | Angular 21 — composants standalone, Signals, nouveau flux de contrôle, Tailwind CSS 4, `@microsoft/signalr`, ngx-translate (FR/EN) |
| **Tests** | xUnit + Moq, Vitest, Playwright |
| **Infra** | Docker Compose, chart Helm |

> **Note de version** : la spécification cible .NET 11 et Angular 22. Le code utilise les versions
> stables réellement disponibles — **.NET 8 LTS** et **Angular 21** — avec les mêmes patterns
> architecturaux ; la migration sera un upgrade mécanique.

---

## Structure du dépôt

```
backend/
  src/AgentHost.Api/        API, hubs SignalR, tâches de fond, orchestration
  src/AgentHost.Runner/     Tier d'exécution optionnel (débloque replicaCount > 1)
  src/AgentHost.Shared/     Contrats communs
  tests/                    xUnit — unitaires, intégration, concurrence, conteneur réel
frontend/                   SPA Angular
migrations/                 Schéma PostgreSQL, fichiers numérotés et idempotents
charts/agenthost/           Chart Helm
scripts/                    Sauvegarde, restauration, PITR
deploy/observability/       Règles d'alerte Prometheus
docs/                       Documentation (voir l'index ci-dessous)
docker-compose.yml          Stack dev/local complète
docker-compose.podman.yml   Overlay Podman
docker-compose.pitr.yml     Overlay archivage WAL / PITR
```

---

## Documentation

| Document | Contenu |
| --- | --- |
| [architecture.md](docs/architecture.md) | Les décisions structurantes et leurs conséquences. **Commencer ici.** |
| [configuration.md](docs/configuration.md) | Toutes les clés de configuration, leurs défauts, leurs effets |
| [operations.md](docs/operations.md) | Sauvegarde, restauration, PITR, rotation de clé, purge RGPD |
| [containers.md](docs/containers.md) | Docker/Podman, sockets, bind mounts, durcissement, confinement réseau |
| [security.md](docs/security.md) | Modèle de menace, garanties, et risques résiduels |
| [agent-protocol.md](docs/agent-protocol.md) | Le contrat entre un agent et l'hôte |
| [auth.md](docs/auth.md) | Authentification, MFA, invitations, réinitialisation |
| [triggers.md](docs/triggers.md) | Webhooks entrants et planification cron |
| [runner.md](docs/runner.md) | Le tier d'exécution et le passage à plusieurs répliques |
| [manifest-editor.md](docs/manifest-editor.md) | Édition d'agents en double mode YAML / IHM |
| [observabilite-operationnelle.md](docs/observabilite-operationnelle.md) | Métriques, alertes, tableaux de bord |
| [testing.md](docs/testing.md) | Ce qui est exécuté, ce qui est raisonné |
| [validation-reelle.md](docs/validation-reelle.md) | Ce qui a été confronté à une vraie dépendance |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Conventions de code, de tests et de commits |
| [ROADMAP.md](ROADMAP.md) | État détaillé, lot par lot, et dette identifiée |

---

## Développement

```bash
# Backend
cd backend
dotnet build && dotnet test
dotnet run --project src/AgentHost.Api

# Frontend
cd frontend
npm install
npm start          # http://localhost:4200
npm test           # Vitest
npm run e2e        # Playwright
npm run build      # production
```

`dotnet test` a besoin d'un PostgreSQL local : les tests d'intégration démarrent le vrai pipeline
`Program`. Sans démon de conteneurs, les tests de cycle de vie conteneur se déclarent **SKIPPED**
avec la raison et le socket sondé — jamais « passés », et **la CI échoue s'ils skippent**. Détails
dans [docs/testing.md](docs/testing.md).

---

## Déploiement

### Docker Compose

```bash
docker compose up --build                                              # dev/local
docker compose -f docker-compose.yml -f docker-compose.podman.yml up   # Podman
docker compose -f docker-compose.yml -f docker-compose.pitr.yml up -d  # avec archivage WAL
```

### Kubernetes

```bash
helm template charts/agenthost \
  --set secrets.jwtSecret=... \
  --set secrets.encryptionKey=... \
  --set postgres.auth.password=...
```

Les secrets sont **obligatoires** : le chart ne fournit aucune valeur par défaut.
`postgres.auth.password` n'est requis que si le chart construit lui-même la chaîne de connexion.

**`replicaCount > 1` exige `runner.enabled: true`**, et le chart le refuse explicitement sinon —
avec un message qui explique pourquoi, plutôt qu'un comportement dégradé silencieux. Sans le tier
runner, le backend parle au démon de son propre nœud : une seconde réplique répondrait « annulé »
sans rien avoir arrêté. Voir [docs/runner.md](docs/runner.md).

---

## État d'implémentation

| Phase | État |
| --- | --- |
| **P0 — Socle** : API REST, PostgreSQL, orchestration, SignalR, IHM | ✅ |
| **P1 — Agents** : agents OCI et externes, mémoire agentique, approbations, protocole agent, chien de garde | ✅ |
| **P2 — Production** : durcissement §13.2, secrets chiffrés, JWT + RBAC, limitation de débit, chart Helm, Serilog, `/metrics`, traces OTLP | ✅ |
| **P3 — Optimisation** : tier runner, cache Redis d'agrégats, déclencheurs, chaînage de runs | ✅ |

Non fournis, et énoncés comme tels : le déploiement des backends d'observabilité eux-mêmes
(Grafana/Loki/Jaeger), les pools de conteneurs pré-chauffés, et la bascule automatique de base de
données. Le détail lot par lot est dans [ROADMAP.md](ROADMAP.md).

---

## Limites connues

- **Le socket du runtime reste un pouvoir de root sur l'hôte.** Le proxy filtrant est une
  atténuation, pas une élimination : « créer et démarrer un conteneur » suffit à s'évader. Une
  isolation réelle demande un démon par locataire, une VM, ou gVisor/Kata.
- **`network: allowlist` dépend de la coopération du client.** Un processus qui ouvre une socket TCP
  directe ignore `HTTP_PROXY` ; le confinement réel demande un réseau `--internal`.
- **Bind mounts d'agents en Kubernetes avec un PVC réseau** : le démon du nœud n'a aucun chemin vers
  ce système de fichiers. Il faut un volume `runs` en hostPath, ou le tier runner.
- **`X-Forwarded-For` n'est honoré que pour les proxys déclarés.** Sans déclaration, tous les
  appelants anonymes partagent le compartiment de limitation de débit du proxy.
- **Le plafond mensuel d'un projet peut être franchi d'un run** par une rafale de créations
  simultanées : c'est un arbitrage assumé, expliqué dans
  [architecture.md](docs/architecture.md) §6.

---

## Licence

Aucune licence n'est déclarée à ce jour. En l'absence de fichier `LICENSE`, le code reste sous droit
d'auteur exclusif de son auteur : personne d'autre n'a le droit de l'utiliser, de le modifier ou de
le redistribuer. Ajouter un `LICENSE` est une décision du propriétaire du dépôt.
