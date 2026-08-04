# Agent Host

Plateforme d'hébergement et d'exécution d'agents autonomes — implémentation de la
spécification v2.0 ("Agent Host — Spécification complète v2.0").

## Stack

- **Backend** : ASP.NET Core Minimal APIs, SignalR, Dapper + Npgsql (PostgreSQL 16),
  Docker.DotNet (orchestration de conteneurs), Serilog, FluentValidation, YamlDotNet.
- **Frontend** : Angular (standalone components, Signals, new control flow), Tailwind CSS,
  `@microsoft/signalr`, ngx-translate (FR/EN).
- **Infra** : Docker Compose (dev), Helm chart (Kubernetes, scaling).

> **Note de version** : la spécification cible .NET 11 et Angular 22, non encore publiés au
> moment de l'implémentation. Le code utilise les dernières versions stables réellement
> disponibles (.NET 8 LTS, Angular 18) avec les mêmes patterns architecturaux (Minimal APIs,
> Signals, standalone components, new control flow, esbuild) — la migration vers .NET 11 /
> Angular 22 sera un upgrade mécanique quand ces versions seront publiées.

## Structure du repo

```
backend/            ASP.NET Core API (AgentHost.Api) + tests
frontend/            Angular SPA
migrations/           Schéma PostgreSQL (section 5 de la spec)
charts/agenthost/     Helm chart (Kubernetes)
docker-compose.yml     Stack dev/local complète
Dockerfile.backend
Dockerfile.frontend
nginx.conf
```

## Démarrage rapide (dev local)

```bash
docker compose up --build
```

- Frontend : http://localhost:4200
- Backend API : http://localhost:5000/api
- Health check : http://localhost:5000/health

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

## État d'implémentation (voir section 18 de la spec — phases de livraison)

- **P0 — Socle** : API REST, PostgreSQL, orchestration Docker, SignalR, UI Angular de base. ✅
- **P1 — Agents** : agents OCI + externes, mémoire agentique v1, approvals. ✅
- **P2 — Production** : durcissement sécurité de base (voir section 13), Helm chart,
  logging structuré. ✅ (socle) — monitoring Prometheus/Grafana/Loki/Jaeger non fournis
  (hors périmètre de ce commit, cf. section 14).
- **P3 — Optimisation** : non couvert par cette implémentation initiale.
