# Le tier runner

Comment l'exécution des conteneurs sort du backend, et ce que ça débloque.

---

## 1. Le problème

Le backend parlait au démon de conteneurs de **son propre nœud**. `StopAsync(runId)` et
`GetLogsAsync(runId)` cherchaient le conteneur portant l'étiquette `agenthost.run_id` — sur *ce*
démon-là.

Une seconde réplique du backend n'y trouvait rien. Et surtout, **elle n'en tirait aucune
conclusion** : elle répondait « annulé » sans rien avoir arrêté. C'est le pire des résultats, parce
qu'il est silencieux.

La cause n'est pas Docker : c'est que **« où tourne le conteneur de ce run » n'était écrit nulle
part**. C'était une propriété implicite du processus qui avait reçu la requête de création. Cette
absence est ce qui imposait `replicaCount: 1`, le HPA désactivé et la stratégie `Recreate` — donc
une coupure à chaque déploiement, et une panne totale à la perte d'un pod.

## 2. La forme retenue

Trois pièces :

- **`AgentHost.Shared`** — la plomberie conteneur commune (orchestrateur, correspondance de
  chemins, résolution du point de terminaison du démon, politique réseau). Elle est partagée plutôt
  que dupliquée : le mode `inprocess` et le runner exécutent le *même* code de lancement.
- **`AgentHost.Runner`** — une API minimale qui expose cette plomberie sur HTTP :
  `POST /runner/runs`, `POST /runner/runs/{runId}/stop`, `GET /runner/runs/{runId}/logs`,
  `GET /runner/runs/{runId}/wait`. Un jeton porteur partagé la protège ; ce n'est pas une API
  publique.
- **`RemoteContainerOrchestrator`** — la seconde implémentation d'`IContainerOrchestrator` côté
  backend, qui parle à un runner au lieu d'un démon.

**`runs.runner_url` (migration 0009) est la pièce qui compte.** Au lancement, le backend écrit
l'adresse du runner qui a pris le run. L'arrêt et la lecture des journaux relisent cette colonne et
routent vers ce runner-là — quelle que soit la réplique qui reçoit la requête.

### Pourquoi une URL et pas un identifiant de runner

Un `runner_id` supposerait un annuaire : une table de runners enregistrés, leur adresse, leur santé,
un battement de cœur pour repérer les disparus, et le nettoyage des entrées périmées. Un composant
entier dont la seule fonction serait de retrouver une adresse **que l'on connaissait déjà au moment
du lancement**.

L'URL rend la ligne autosuffisante. En DaemonSet, l'adresse à composer est justement l'IP du pod
runner du nœud : il n'existe pas de Service par nœud vers lequel router.

Le reproche que l'on peut faire à l'URL est réel : une IP de pod n'est pas stable, et elle peut être
**réattribuée à un pod d'un autre nœud**. Une URL enregistrée peut donc pointer vers un runner qui
n'a jamais eu ce run. C'est traité, pas ignoré : le runner répond explicitement qu'il ne connaît pas
le run (404 sur `/wait`, `confirmed: false` sur `/stop`), et le backend rapporte une annulation
**non confirmée** au lieu de prendre le silence d'un runner étranger pour un succès.

## 3. Les trois réponses possibles à une annulation

C'est le point qui décide si l'utilisateur est informé ou trompé :

| Situation | Ce que voit l'appelant |
| --- | --- |
| Le runner a arrêté le conteneur | Annulation **confirmée** |
| Aucun runner enregistré (run antérieur à la migration, lancé en `inprocess`, ou jamais parti) | **`runner_unknown`** — rien n'a été arrêté, et c'est dit |
| Le runner ne répond plus, ou ne connaît pas ce run | **`runner_unreachable`** / non confirmée |

Aucun de ces trois cas ne se présente comme un succès silencieux.

## 4. Activation

Le défaut est **`inprocess`**, et toute valeur autre que `remote` exactement y retombe : une faute
de frappe ne doit pas rediriger l'exécution vers un tier absent. Une installation existante ne
change pas de comportement.

### docker-compose

```yaml
Runner__Mode: "remote"
Runner__BaseUrl: "http://runner:5001"
Runner__AuthToken: "<jeton partagé>"
```

### Kubernetes

```yaml
runner:
  enabled: true          # rend le DaemonSet et bascule le backend en mode remote
  authToken: "<jeton partagé>"
replicaCount: 3          # devient défendable une fois le runner activé
```

Le runner est un **DaemonSet** : il lui faut le socket du démon local, donc un par nœud. Le backend
redevient un Deployment réplicable.

`replicaCount` reste à 1 par défaut : la mise à l'échelle est un choix d'exploitation, pas un effet
de bord d'une mise à jour de chart. Avec plusieurs répliques, deux points deviennent obligatoires et
non plus recommandés : le stockage d'artefacts en **S3** (un volume `ReadWriteOnce` n'est visible
que d'un pod) et le **backplane Redis** de SignalR.

## 5. Ce qui n'est pas prouvé

**Aucun conteneur n'a jamais été lancé à travers le runner.** L'environnement de développement n'a
pas de démon de conteneurs — voir [validation-reelle.md](validation-reelle.md).

Ce qui est vérifié : le vrai `RunnerEndpoints` servi par Kestrel sur une boucle locale, devant un
superviseur simulé. Les 31 tests couvrent le protocole, le routage — dont le cas d'une seconde
réplique qui annule un run qu'elle n'a pas lancé, avec deux runners distincts, en vérifiant que le
bon reçoit l'ordre et que l'autre ne reçoit rien —, l'authentification par jeton, et les trois
réponses ci-dessus.

Ce qui ne l'est pas : le lancement réel d'un conteneur par le runner, le DaemonSet sous
Kubernetes, et le comportement de plusieurs répliques backend sous trafic concurrent. Le premier
tomberait dans le champ de `ContainerLifecycleTests` si la CI exécutait le runner ; les deux autres
demandent un cluster.
