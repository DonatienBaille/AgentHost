# Architecture

Comment Agent Host est fait, et **pourquoi** il est fait comme ça. Ce document décrit les décisions
structurantes et leurs conséquences ; les manipulations quotidiennes sont dans
[operations.md](operations.md), les réglages dans [configuration.md](configuration.md).

---

## 1. Vue d'ensemble

Agent Host exécute du **code tiers** — des agents autonomes — pour le compte de plusieurs
organisations sur une même installation. Ces deux mots gouvernent presque toutes les décisions qui
suivent : le code exécuté est hostile par hypothèse, et les données de deux locataires ne doivent
jamais se croiser.

```
┌──────────────┐   HTTPS    ┌───────────────────────────────────┐
│  Navigateur  │───────────►│  nginx (SPA Angular + reverse     │
│  (Angular)   │◄───────────│  proxy /api, /hubs)               │
└──────────────┘  WebSocket └─────────────────┬─────────────────┘
                                              │
                             ┌────────────────▼─────────────────┐
                             │  AgentHost.Api (ASP.NET Core)    │
                             │  Minimal APIs · SignalR · Dapper │
                             └──┬──────────┬─────────┬──────────┘
                                │          │         │
              ┌─────────────────▼──┐  ┌────▼─────┐  ┌▼────────────────────┐
              │  PostgreSQL 16     │  │  Redis   │  │  Runtime conteneurs │
              │  (source de vérité)│  │(optionnel)│ │  Docker ou Podman   │
              └────────────────────┘  └──────────┘  └──────┬──────────────┘
                                                            │
                                                   ┌────────▼─────────┐
                                                   │ Conteneur d'agent│
                                                   │ /workspace       │
                                                   │ /run/secrets     │
                                                   └────────┬─────────┘
                                                            │ HTTP + jeton de run
                                                            └──► /api/agent/...
```

**Trois projets .NET** :

| Projet | Rôle |
| --- | --- |
| `AgentHost.Api` | L'API, les hubs SignalR, les tâches de fond, l'orchestration |
| `AgentHost.Runner` | Le tier d'exécution optionnel (§7) — reçoit une spécification, pilote le démon local |
| `AgentHost.Shared` | Les contrats communs aux deux |

**Redis est facultatif et le reste.** Il sert de backplane SignalR (plusieurs répliques) et de cache
d'agrégats pour les endpoints de métriques. Une instance Redis absente ou injoignable **ne bloque
jamais le démarrage** : le cache retombe sur la fabrique, le backplane sur le mode mono-processus.
Une dépendance optionnelle qui empêche de démarrer n'est pas optionnelle.

---

## 2. Le découpage du backend

Quatre couches, et une règle par couche.

| Couche | Répertoire | Règle |
| --- | --- | --- |
| **Endpoints** | `Endpoints/` | Traduisent HTTP ↔ domaine. Aucune règle métier, aucun SQL. |
| **Services** | `Services/` | Les règles. Ne connaissent ni `HttpContext` ni Npgsql. |
| **Dépôts** | `Repositories/` | Le SQL, écrit à la main. Aucune règle métier. |
| **Domaine** | `Domain/` | Les types. Aucune dépendance sortante. |

Deux conséquences non évidentes.

**Il n'y a pas d'ORM.** Dapper exécute du SQL écrit à la main. Le coût est réel — chaque colonne
ajoutée se propage dans un `INSERT`, un `UPDATE` et un `SELECT` — et il est assumé : les propriétés
qui comptent le plus dans ce produit (isolation multi-locataire, écritures atomiques, verrouillage)
s'énoncent en SQL et se perdent dans un générateur de requêtes. Voir §5.

**Les lignes de base sont des types à part.** `Repositories/DbRows.cs` porte un DTO par table, dont
les colonnes énumérées sont typées `string`. Ce n'est pas de la cérémonie : Dapper **court-circuite**
ses gestionnaires de types pour un paramètre énuméré et écrit sa valeur entière — la colonne
recevrait `14` au lieu de `infra_error`, sans erreur, et la relecture échouerait ailleurs. Les
conversions passent donc par `ToDbString()` / `FromDbString()`, et des contraintes `CHECK` en base
refusent tout ce qui n'est pas une valeur documentée.

---

## 3. Le cycle de vie d'un run

C'est le cœur du produit, et la seule machine à états du dépôt.

```
pending → queued → provisioning → preparing → running ──► finalizing → succeeded
                                                 │                   └► failed
                                                 ├─► awaiting_approval ──► running
                                                 └─► awaiting_input ────► running

  depuis tout état non terminal : cancelled · timed_out · budget_exceeded
                                  rejected · infra_error
```

**Les transitions sont validées puis écrites sous condition.** `RunStateMachine.TransitionAsync`
vérifie la table ci-dessus sur l'objet en mémoire, puis écrit avec `WHERE status = @Attendu`. Les
deux sont nécessaires et ni l'un ni l'autre ne suffit : le contrôle en mémoire donne un message
d'erreur utile, la condition SQL est le seul point où deux appelants concurrents peuvent être
départagés. Sans elle, une annulation humaine et une expiration du chien de garde arrivant ensemble
produisaient deux fins pour un seul run — deux salves de webhooks, deux entrées d'historique, et une
jauge de file qui passait sous zéro.

**Les états terminaux sont définitifs.** Aucune transition n'en sort ; c'est vérifié à l'entrée de
la machine, avant même la table.

**Deux variantes d'appel.** `TransitionAsync` lève ; `TryTransitionAsync` rend `false`. La seconde
existe pour les appelants qui courent *légitimement* contre une autre transition — le moniteur de
conteneur qui observe une sortie pour un run qu'un humain vient d'annuler. Une course attendue ne
doit pas produire de ligne de journal en niveau erreur, sans quoi les alertes deviennent du bruit.

---

## 4. Le protocole agent → hôte

Un agent est un conteneur. Il ne reçoit **ni identifiants de base, ni jeton d'utilisateur** : il
reçoit un `AGENTHOST_RUN_TOKEN`, valable pour *son* run et pour rien d'autre, et parle à un groupe
d'endpoints dédié (`/api/agent/...`) qui n'expose que ce dont un agent a besoin — publier un
événement, demander une approbation, rapporter une consommation, déposer un artefact.

Le détail est dans [agent-protocol.md](agent-protocol.md). Deux points d'architecture :

- **Les secrets arrivent par fichier**, jamais par variable d'environnement. `/run/secrets/<NOM>` en
  0400, répertoire en 0700, effacé à la sortie du conteneur. Une variable d'environnement est
  lisible par `docker inspect` et par `/proc/1/environ` — donc par tout processus du conteneur, y
  compris ceux que l'agent lance sans y penser.
- **La consommation est cumulée en base, atomiquement** (`UPDATE … RETURNING`). Les rapports d'usage
  arrivent en rafale ; un lire-modifier-écrire y perdrait des incréments, et un budget sous-évalué
  est un budget qui ne s'épuise jamais.

---

## 5. Isolation multi-locataire

**La règle : l'isolation est dans le SQL, jamais dans une vérification que quelqu'un peut oublier
d'écrire.**

Chaque lecture porte `org_id` dans sa clause `WHERE`. Il n'existe pas de `GetAsync(id)` public suivi
d'un `if (entity.OrgId != caller.OrgId)` — la surcharge non scopée existe, mais elle est réservée
aux appelants système (tâches de fond, exécuteur de run) et documentée comme telle.

**Une ressource d'un autre locataire rend 404, jamais 403.** Un 403 confirmerait l'existence de la
ressource ; l'énumération d'identifiants deviendrait une source d'information. Les deux réponses ont
l'air équivalentes et ne le sont pas.

Les tables sans `org_id` (`approvals`, `artifacts`, `run_events`…) sont scopées **par jointure** vers
le run ou le projet qui les porte. Le chemin de la jointure est le même dans les lectures et dans la
purge, pour que ce que la purge efface soit exactement ce que l'application sait atteindre.

---

## 6. Concurrence : où sont les invariants

Quatre défauts de concurrence ont été trouvés en écrivant des tests qui lancent leurs appels
réellement en parallèle. Aucun n'était visible sur une requête isolée. Ce qui les a corrigés est
toujours la même chose : **déplacer la décision dans l'instruction SQL qui écrit.**

| Invariant | Mécanisme |
| --- | --- |
| Numéro de run unique par projet | `UPDATE projects SET run_counter = run_counter + 1 … RETURNING` |
| Une seule fin par run | `UPDATE runs … WHERE status = @Attendu` |
| Aucune réponse d'approbation perdue | concaténation `jsonb` en base, jamais de réécriture de liste |
| Une approbation ne franchit la porte qu'une fois | `UPDATE approvals … WHERE status = 'pending'` |
| Un jeton de rafraîchissement n'engendre qu'un successeur | révocation **avant** émission, `WHERE revoked_at IS NULL` |
| Aucun incrément de budget perdu | `UPDATE … SET budget_used = budget_used + @Delta RETURNING` |
| Un seul répartiteur traite un courriel | `FOR UPDATE SKIP LOCKED` + bail |

**Ce qui reste volontairement non sérialisé** : le plafond mensuel d'un projet est un
lire-puis-décider, donc une rafale de créations peut le franchir **d'un run**. Sérialiser toutes les
créations d'un projet derrière un verrou coûterait, sur un projet actif, davantage que le
dépassement — borné par le budget d'un seul run, chaque run ayant ensuite son propre plafond
contrôlé. La garantie exacte est : *le plafond arrête un projet, mais la dernière rafale peut le
franchir d'un run.*

---

## 7. Exécution : en processus, ou tier runner

Deux topologies, un seul code d'orchestration.

**En processus** (`runner.enabled: false`, le défaut). Le backend parle au démon de conteneurs de son
propre nœud. Simple, et c'est ce qu'il faut pour une installation unique — mais `StopAsync(runId)`
cherche le conteneur sur *ce* démon. Une seconde réplique ne le trouverait pas, et — le pire — n'en
tirerait aucune conclusion : elle répondrait « annulé » sans rien avoir arrêté.

**Tier runner** (`runner.enabled: true`). L'exécution sort dans un service distinct, et
`runs.runner_url` (migration `0009`) écrit **où** tourne chaque run. C'est cette colonne, et pas le
service lui-même, qui débloque `replicaCount > 1` : « où tourne ce conteneur » cesse d'être une
propriété implicite du processus qui a reçu la requête de création.

Le chart **refuse** `replicaCount > 1` sans `runner.enabled`, avec un message qui explique pourquoi
plutôt qu'un comportement dégradé silencieux. Détails dans [runner.md](runner.md).

---

## 8. Déclencheurs

Un run peut naître de trois façons : un appel d'API, un **webhook entrant**, ou une **planification
cron**. Les deux derniers sont décrits dans [triggers.md](triggers.md).

Deux propriétés structurantes :

- **Les occurrences manquées ne sont pas rejouées.** Un ordonnanceur arrêté deux jours qui
  redémarrerait en lançant quarante-huit exécutions ferait plus de dégâts que l'interruption
  elle-même.
- **La réclamation d'une échéance est atomique** (`UPDATE … WHERE next_run_at = @Attendu RETURNING`),
  de sorte que deux ordonnanceurs concurrents ne déclenchent pas deux fois la même occurrence.

Les runs peuvent aussi s'enchaîner (`parent_run_id`, `root_run_id`, `chain_depth` — migration
`0012`), avec trois bornes : profondeur, nombre d'enfants directs, taille totale de l'arbre. Un agent
qui s'appelle lui-même est un accident normal, pas une hypothèse d'école.

---

## 9. Sécurité

Le détail est dans [security.md](security.md). Les décisions de structure :

- **Secrets chiffrés en AES-256-GCM**, déchiffrés à la volée, jamais rendus en clair par l'API. La
  clé est rotative (`--rekey-secrets`) et une clé perdue rend les secrets **définitivement**
  illisibles — d'où la règle : la clé ne se sauvegarde pas au même endroit que les dumps.
- **Les jetons ne sont stockés que par empreinte SHA-256.** Rafraîchissement, réinitialisation,
  invitation : le dépôt ne peut pas restituer un jeton, seulement en vérifier un.
- **`audit_log` est append-only** (migration `0008`), garanti par trois triggers — `UPDATE`, `DELETE`
  et `TRUNCATE`, ce dernier avec son propre trigger d'instruction, parce qu'un trigger de niveau
  ligne ne le voit jamais. La seule dérogation du dépôt est la purge RGPD, hors ligne et documentée.
- **Le corps des courriels en attente est chiffré au repos** : il contient un jeton en clair, et le
  persister nu aurait défait la propriété précédente par la porte de service.

---

## 10. Observabilité

L'application **expose** les données ; elle ne déploie pas la plateforme qui les consomme.

- **Journaux** structurés (Serilog, JSON), sans secret ni jeton ni corps de courriel.
- **Métriques** Prometheus sur `/metrics`, dont les métriques métier (`agenthost.run.*`).
  `deploy/observability/alerts.yml` fournit neuf règles prêtes à l'emploi.
- **Traces** OTLP.

`/metrics` n'est **pas** exposé par nginx : la SPA renvoie 404 dessus, et la collecte scrape le
backend directement. Une surface de métriques accessible depuis Internet est une fuite d'information
d'exploitation.

---

## 11. Persistance

PostgreSQL 16 est la **source de vérité unique**. Rien d'autre ne porte d'état durable : Redis est un
cache, les workspaces sont jetables, les artefacts sont adressés depuis la base.

Les migrations sont des fichiers SQL numérotés, appliqués au démarrage, **idempotents**, et jamais
réécrits une fois livrés. Il n'y a pas de « down » : une migration qui se défait est une migration
qu'on écrit à l'endroit.

Deux mécanismes de sauvegarde, complémentaires et non interchangeables (voir
[operations.md](operations.md) §2 et §6) : le dump logique survit à un changement de version majeure
et se restaure table par table ; la sauvegarde physique plus l'archivage WAL permet de revenir à un
**instant choisi**, ce qu'un dump ne sait pas faire.

---

## 12. Frontend

Angular 21 en composants standalone, Signals, nouveau flux de contrôle (`@if` / `@for`), détection de
changement `OnPush`. Pas de NgRx : l'état serveur vit dans des services à signaux, et l'état
d'écran dans les composants.

Le temps réel passe par SignalR (`@microsoft/signalr`) : les événements de run et les demandes
d'approbation arrivent poussés, pas par sondage.

L'internationalisation est en place (`ngx-translate`, FR par défaut, EN disponible) et **aucune
chaîne visible n'est écrite en dur** dans un gabarit.

---

## 13. Ce que l'architecture ne fait pas

Énoncé ici pour que ce ne soit pas découvert en production.

- **Pas de bascule automatique de base de données.** L'archivage WAL et le PITR sont en place, la
  détection de panne et l'adresse virtuelle ne le sont pas : cela relève d'un gestionnaire de grappe
  (Patroni, repmgr) et du déploiement.
- **Le socket du runtime de conteneurs reste un pouvoir de root sur l'hôte.** Le proxy filtrant est
  une atténuation, pas une élimination — « créer et démarrer un conteneur » suffit à s'évader. Une
  isolation réelle demande un démon par locataire, une VM, ou un runtime gVisor/Kata.
- **`network: allowlist` dépend de la coopération du client.** Un processus qui ouvre une socket TCP
  directe ignore `HTTP_PROXY`. Le confinement réel demande un réseau `--internal` où seul le proxy
  est joignable.
- **Pas de pools de conteneurs pré-chauffés** ni de réutilisation de workspaces : chaque run part
  d'un conteneur neuf.
