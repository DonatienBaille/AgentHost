# Observabilité opérationnelle

Spécification §14, feuille de route lot 4. Ce document décrit ce qu'un **exploitant** surveille —
distinct du tableau de bord du lot 3, qui répond aux questions d'un **utilisateur** de la
plateforme.

---

## 1. Deux publics, deux surfaces

| | Lot 3 — `/monitoring` dans l'IHM | Lot 4 — ce document |
| --- | --- | --- |
| Public | l'utilisateur de la plateforme | l'exploitant |
| Question | « combien m'ont coûté mes agents, lesquels échouent, vais-je dépasser mon budget » | « le service tient-il debout, et si non, depuis quand » |
| Périmètre | son organisation, imposé par le JWT | l'installation entière, sans notion d'organisation |
| Source | la table `runs`, agrégée par l'API | les séries Prometheus |

Les deux sont complémentaires et aucune ne remplace l'autre. Une série Prometheus ne sait pas
répondre « MES agents » sans donner accès à ceux des autres ; une agrégation SQL scopée ne dit rien
de la santé du processus.

---

## 2. Ce que le backend expose

`/metrics`, au format Prometheus, sans authentification (à protéger au niveau réseau — ce sont des
données d'exploitation). Deux familles :

- **Technique**, fournie par les instrumentations OpenTelemetry standard : ASP.NET Core
  (`http_server_request_duration_seconds_*`), HttpClient, runtime .NET, Npgsql
  (`db_client_connections_*`).
- **Métier**, meter `AgentHost.Business` (voir `Infrastructure/AgentHostMetrics.cs`) :

| Instrument | Série Prometheus | Nature |
| --- | --- | --- |
| `agenthost.run.duration` | `agenthost_run_duration_seconds_{bucket,sum,count}` | histogramme |
| `agenthost.run.finished` | `agenthost_run_finished_total` | compteur, ventilé par statut |
| `agenthost.run.cost` | compteur, non alerté (voir §4) | compteur |
| `agenthost.approval.wait` | `agenthost_approval_wait_seconds_{bucket,sum,count}` | histogramme |
| `agenthost.budget.exhausted` | `agenthost_budget_exhausted_total` | compteur |
| `agenthost.infra.error` | `agenthost_infra_error_total` | compteur |
| `agenthost.run.in_flight` | `agenthost_run_in_flight` | jauge (UpDownCounter) |

**La cardinalité est bornée par construction** : les étiquettes sont l'organisation, le projet,
l'agent et le statut — **jamais** un identifiant de run ou d'utilisateur. Un run est un événement,
pas une dimension.

---

## 3. Les règles d'alerte

`deploy/observability/alerts.yml`, à charger dans Prometheus (`rule_files`). Neuf règles, en trois
familles : disponibilité, exécution des agents, dépendances.

**Les seuils sont des points de départ** et doivent être relus avec un mois de données réelles. Ce
qui n'est pas négociable, en revanche, c'est la forme, et un test la vérifie :

- chaque règle porte un `for` non nul — une alerte qui se déclenche sur un point isolé est une
  alerte qu'on apprend à ignorer, ce qui est pire que pas d'alerte ;
- chaque règle porte une `severity` et un `summary` — une alerte qui arrive sans phrase oblige à
  lire sa requête pour savoir ce qui se passe, à l'heure où on le sait le moins.

### Une alerte inhabituelle : `AgentHostQueueDepthNegative`

Elle surveille une impossibilité — une jauge de profondeur de file strictement négative. Elle existe
parce que **c'est arrivé** : jusqu'au lot 4, `agenthost_run_in_flight` ne faisait que décroître
(l'incrément était écrit mais appelé nulle part, et un run refusé avant lancement était décompté
sans avoir été compté). La série valait `-1` après un seul run.

Rien ne le signalait : un tableau de bord affichant `-3` se lit comme « rien à signaler ». Cette
règle est la trace de ce défaut, et sa garantie de non-retour.

---

## 4. Ce qui n'est délibérément pas alerté

**Le coût.** Le dépassement de budget est un événement métier, déjà remonté dans l'IHM de
l'organisation concernée, qui est la seule à pouvoir décider quoi en faire. Réveiller un exploitant
parce qu'un client dépense son propre argent serait une astreinte sur une décision qui ne lui
appartient pas.

`agenthost_budget_exhausted_total` figure quand même dans les règles, mais en `severity: info` et
sur un agrégat global : un pic à l'échelle de l'installation sent l'erreur de configuration — un
plafond par défaut trop bas après un déploiement — et non la dépense d'un client.

Cette exclusion est **écrite dans le test** (`OperationalAlertsTests.DeliberatelyNotAlerted`) avec sa
raison. Tout instrument qui n'est ni alerté ni explicitement exclu fait échouer la suite : ajouter un
instrument sans décider s'il mérite une alerte, c'est instrumenter pour la forme.

---

## 5. Pourquoi un fichier de configuration est testé

Une règle qui interroge une série inexistante ne produit **aucune erreur**. Elle produit zéro,
indéfiniment, donc une alerte qui ne se déclenche jamais — le pire des deux mondes, puisque
l'exploitant se croit couvert. Renommer un instrument, changer son unité, ou en ajouter un sans le
brancher : les trois passent silencieusement et ne se découvrent qu'à l'incident qu'on croyait
surveillé.

`backend/tests/AgentHost.Api.Tests/OperationalAlertsTests.cs` confronte donc le fichier de règles aux
instruments **réellement publiés**, obtenus d'un `MeterListener` — le mécanisme même de l'exporteur —
et non d'une liste recopiée qui divergerait au premier ajout.

Vérifié en sabotant le fichier : renommer une série en `agenthost_infra_errors_total` (un « s » de
trop, exactement la faute qu'on ne voit pas) et retirer la règle des approbations font virer deux
tests au rouge.

---

## 6. Mise en place

```yaml
# prometheus.yml
scrape_configs:
  - job_name: agenthost-backend      # le nom compte : les règles filtrent dessus
    metrics_path: /metrics
    static_configs:
      - targets: ["agenthost-backend:8080"]

rule_files:
  - /etc/prometheus/rules/agenthost-alerts.yml
```

Le `job_name` doit être `agenthost-backend` : `AgentHostBackendDown` et `AgentHostHighErrorRate`
filtrent sur cette étiquette. Un autre nom rend ces deux règles muettes — sans erreur, évidemment.

---

## 7. Ce qui reste hors de portée

- **Pas de tableau de bord Grafana livré.** Un fichier JSON de dashboard n'est vérifiable par rien
  ici : il ne compile pas, aucun test ne peut l'exécuter, et une capture d'écran ne prouve pas
  qu'il interroge les bonnes séries. Les règles ci-dessus, elles, sont confrontées aux instruments
  réels par la suite de tests. Un dashboard construit à la main dans Grafana à partir du tableau du
  §2 sera plus juste qu'un JSON écrit à l'aveugle.
- **Pas de règles d'enregistrement (`recording rules`).** Elles n'ont d'intérêt qu'une fois les
  requêtes coûteuses identifiées sur des données réelles ; les écrire d'avance, c'est optimiser
  sans mesure.
- **Pas de SLO ni de budget d'erreur.** Ils supposent un objectif de service négocié, qui est une
  décision de produit et non d'implémentation.
