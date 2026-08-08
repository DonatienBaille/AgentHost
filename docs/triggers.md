# Déclencheurs et chaînage de runs

Feuille de route, lot 4. Ce document décrit **ce qui fait partir un agent sans que personne ne
clique** — une livraison de forge, une échéance, ou un autre agent — et les garde-fous qui vont
avec.

---

## 1. Pourquoi ces trois choses sont dans le même document

`TriggeredByType` déclare depuis l'origine `manual`, `webhook`, `cron`, `api` et `chain`. Seuls
`manual` et `api` fonctionnaient : les trois autres étaient des valeurs d'énumération sans code
derrière. Un agent ne pouvait être lancé que par quelqu'un qui clique ou qui appelle l'API avec un
jeton — c'est-à-dire jamais tout seul, ce qui est pourtant la raison d'être d'une plateforme
d'agents autonomes.

Les trois répondent à la même question — « à quelle occasion ce run part-il » — et posent le même
risque : **une dépense que personne n'a demandée au moment où elle a lieu**. C'est pour cela qu'ils
partagent un document, et que chacun porte des limites explicites.

---

## 2. Webhooks entrants

### 2.1 Configurer

```
POST /api/projects/{projectId}/triggers      (maintainer)
{
  "agentId": "01H…",
  "name": "Push sur main",
  "type": "webhook",
  "provider": "github",           // github | gitlab | generic
  "events": ["push"],             // vide = tous
  "branches": ["main", "release/*"] // vide = toutes
}
```

La réponse porte **le secret, une seule fois** :

```json
{
  "trigger": { "id": "01H…", "webhookPath": "/api/hooks/01H…", … },
  "secret": "kM3…"
}
```

Il n'est stocké que chiffré (AES-256-GCM, même clé que les secrets applicatifs, donc couvert par
`--rekey-secrets`). Aucune lecture ultérieure ne le redonne : un secret qu'une API relit à volonté
n'est plus protégé par le chiffrement au repos, il est protégé par l'autorisation de lecture — un
cran plus faible et un cran plus facile à perdre.

Chez GitHub : **Settings → Webhooks → Add webhook**, URL `https://<votre-hôte>/api/hooks/{id}`,
content type `application/json`, secret = celui rendu ci-dessus.

### 2.2 Recevoir

```
POST /api/hooks/{triggerId}     (anonyme)
```

Anonyme **par nécessité** : c'est une forge qui appelle, elle n'a pas de compte ici. Ce qui autorise
est la preuve qu'elle détient le secret.

| Émetteur  | En-tête vérifié           | Calcul                                    |
| --------- | ------------------------- | ----------------------------------------- |
| `github`  | `X-Hub-Signature-256`     | `sha256=` + HMAC-SHA256(secret, corps brut) |
| `gitlab`  | `X-Gitlab-Token`          | comparaison directe du secret              |
| `generic` | `X-AgentHost-Signature-256` | comme GitHub                             |

On ne choisit pas comment GitHub signe. Exiger une signature de GitLab reviendrait à ne jamais
accepter ses livraisons ; accepter un jeton en clair de GitHub dégraderait une preuve de possession
en jeton porteur. Le `X-Hub-Signature` **SHA-1** hérité est refusé : il est cassé, et le tolérer
rouvrirait l'algorithme pour la seule raison qu'il traîne dans la requête.

Le HMAC porte sur les **octets exacts reçus**. L'endpoint lit un tableau d'octets et ne se fait pas
lier un modèle : re-sérialiser le JSON changerait les espaces, l'ordre des clés et l'échappement, et
toutes les signatures deviendraient fausses. Le corps est borné à 1 Mio.

### 2.3 Codes de retour, et pourquoi

Ils sont pensés pour l'émetteur, pas pour un humain. GitHub réessaie sur 5xx et marque les 4xx en
échec dans son interface.

| Situation                                             | Réponse | Raison                                        |
| ----------------------------------------------------- | ------- | --------------------------------------------- |
| Signature valide, filtre passé                        | `202`   | run lancé, son identifiant est dans le corps  |
| Réémission d'une livraison déjà traitée               | `200`   | traitée, rien à refaire — pas un échec        |
| Livraison authentique hors du filtre                  | `200`   | idem                                          |
| Signature absente ou fausse                           | `401`   | —                                             |
| Agent sans version publiée, budget mensuel épuisé     | `422`   | comprise, effet impossible ; un 5xx ferait réessayer en boucle |
| Déclencheur inconnu, supprimé ou désactivé            | `404`   | les trois se répondent pareil (voir §5)       |
| Corps > 1 Mio                                          | `413`   | —                                             |

### 2.4 Déduplication

GitHub réémet une livraison quand la réponse tarde, et offre un bouton « redeliver ». Sans mémoire,
chaque réémission relancerait l'agent : le premier run coûterait de l'argent, le second aussi, et
rien dans l'IHM ne dirait qu'il s'agit du même événement.

La table `trigger_deliveries` porte l'unicité `(trigger_id, delivery_id)` **en clé primaire**, et
non dans une vérification applicative : deux réémissions simultanées passeraient toutes les deux un
`SELECT … IF NOT EXISTS`. L'identifiant vient de `X-GitHub-Delivery` / `X-Gitlab-Event-UUID`, ou à
défaut de l'empreinte SHA-256 du corps.

La livraison est enregistrée **avant** le filtre : une réémission ne doit pas relancer l'agent, y
compris quand la première a été écartée.

### 2.5 Ce que l'agent reçoit

```json
{
  "trigger": { "id": "…", "name": "…", "type": "webhook", "provider": "github",
               "event": "push", "branch": "main", "deliveryId": "…" },
  "payload": { … la charge utile complète, telle que reçue … }
}
```

dans `runs.context`. Les entrées fixes du déclencheur vont, elles, dans `runs.inputs` : un
déclencheur n'a personne devant lui pour remplir un formulaire.

---

## 3. Planification cron

```
POST /api/projects/{projectId}/triggers      (maintainer)
{ "agentId": "01H…", "name": "Rapport quotidien", "type": "cron",
  "cronExpression": "0 9 * * 1-5", "timeZone": "Europe/Paris" }
```

Cinq champs POSIX : `minute heure jour-du-mois mois jour-de-semaine`. `*`, `n`, `a-b`, `a-b/s`,
`*/s`, listes, et noms de trois lettres (`mon`, `mar`). Pas de secondes, pas de `@reboot`, pas de
`L` ni de `#`.

**Trois pièges, traités et non ignorés :**

- **Le fuseau n'est pas un détail.** « Tous les jours à 9 h » n'a pas de sens sans lui, et résoudre
  en UTC déplacerait l'exécution de deux heures deux fois par an pour toute équipe qui n'est pas à
  Greenwich. L'expression est évaluée dans le fuseau du déclencheur ; seule la conversion finale
  ramène en UTC, ce que la base stocke.
- **Jour du mois et jour de la semaine se combinent en OU** quand les deux sont restreints. C'est la
  règle POSIX : `0 0 1 * 1` signifie « le premier du mois, ET AUSSI tous les lundis ». Faire
  autrement produirait des planifications divergeant silencieusement de ce que la même expression
  donne dans n'importe quel crontab.
- **Une heure locale inexistante est sautée**, pas inventée : le jour du passage à l'heure d'été,
  2 h 30 n'existe pas, et l'occurrence de ce jour-là n'a pas lieu.

Une expression ou un fuseau invalide est refusé **à la création**, avec le message qui nomme le
champ fautif — pas des heures plus tard, au premier réveil, dans un journal que personne ne lit.

### 3.1 Le planificateur

`TriggerScheduler` bat toutes les 20 secondes et interroge un index sur `next_run_at`. Il ne dort
pas jusqu'à l'échéance : les déclencheurs sont créés, modifiés et supprimés pendant l'attente, et un
processus endormi six heures ne verrait rien de tout cela.

**Plusieurs répliques sont prévues, pas tolérées.** La réservation se fait en une instruction :

```sql
UPDATE triggers SET next_run_at = @Next
WHERE id = @Id AND next_run_at = @Expected AND type = 'cron' AND is_active AND deleted_at IS NULL
RETURNING …
```

La seconde réplique ne réserve rien et n'a rien à faire. Aucun verrou distribué, aucune élection de
chef : la ligne elle-même est le jeton.

**Les occurrences manquées ne sont pas rattrapées.** Un backend arrêté trois jours ne doit pas, au
redémarrage, lancer soixante-douze fois un agent horaire — ce serait la facture d'une panne payée
deux fois. La prochaine échéance est recalculée à partir de maintenant, et le retard est journalisé.

Désactiver un déclencheur efface son échéance : la laisser ferait repartir le déclencheur au réveil
avec toutes les occurrences manquées d'un coup.

---

## 4. Chaînage de runs

Un agent en cours d'exécution en déclenche un autre :

```
POST /api/agent/runs/{runId}/chain      (jeton de run, protocole agent)
{ "agentId": "01H…", "inputs": { … }, "budgetMaxUsd": 2.0 }
→ 202 { "runId": "…", "rootRunId": "…", "chainDepth": 1 }
```

Ni `parentRunId` ni `triggeredByType` dans le corps : le parent est le run que le jeton désigne, et
le type est `chain` par construction. Les accepter permettrait à un agent de rattacher son enfant à
un arbre auquel il n'appartient pas, et d'en contourner les limites. **Le jeton n'est pas
transitif** : il autorise à chaîner depuis ce run, il ne devient pas un jeton pour l'enfant.

### 4.1 Les limites

| Limite                  | Valeur | Ce qu'elle empêche                                        |
| ----------------------- | ------ | --------------------------------------------------------- |
| `MaxChainDepth`         | 5      | la récursion — un agent qui se chaîne lui-même            |
| `MaxChainChildren`      | 10     | l'explosion d'un seul niveau, qu'un arbre plat produit aussi |
| `MaxChainTreeSize`      | 50     | la combinaison des deux (5 niveaux × 10 enfants = 10⁵)     |

Elles ne sont pas un raffinement : un agent qui se chaîne lui-même est une boucle infinie qui
consomme le budget mensuel du projet jusqu'à épuisement, et **rien dans un run ne dit qu'il est le
millième** — chaque maillon pris isolément est parfaitement légitime.

Un run chaîné doit viser un agent **du même projet** : sans cela, un agent enfermé dans un projet
ferait dépenser le budget d'un autre, et l'isolation s'arrêterait à la porte du premier run.

Un refus est un `422`, jamais un `500` : l'agent doit pouvoir le traiter plutôt que croire à une
panne.

### 4.2 Lire l'arbre

```
GET /api/runs/{id}/tree
```

Depuis **n'importe lequel** de ses membres, pas seulement depuis la racine : on arrive sur un run
parce qu'il a échoué ou coûté cher, et c'est à ce moment-là qu'on veut savoir de quelle cascade il
fait partie. Exiger la racine obligerait à la connaître déjà.

La réponse porte les totaux de l'ensemble — nombre de runs, coût cumulé, profondeur — parce que
c'est la question qu'on se pose devant une cascade, et non ce qu'a coûté le maillon qu'on regarde.

L'arbre entier se lit en une condition indexée (`root_run_id = @Root OR id = @Root`) : `root_run_id`
est porté par **tous** les descendants, pas seulement par les enfants directs. Aucune CTE récursive.

---

## 5. Principes communs

**Le périmètre vient du jeton, jamais de la requête.** Sauf sur `/api/hooks/{id}`, qui n'a pas de
jeton — et où c'est la signature qui décide.

**Les réponses ne renseignent pas.** Déclencheur inconnu, supprimé, désactivé ou appartenant à un
autre locataire : `404` dans les quatre cas. Un `403` confirmerait l'existence de l'objet à qui ne
le connaît pas, ce qui suffit à cartographier une installation.

**Poser un déclencheur exige `maintainer`.** C'est donner à un tiers — une forge, une horloge, un
autre agent — le droit de dépenser le budget du projet. Le lire n'engage rien et reste ouvert à
tous les rôles.

**Tout laisse une trace** dans le journal d'audit : `trigger.created`, `trigger.updated`,
`trigger.deleted`, `trigger.fired`. Les déclenchements portent un acteur **nul**, et c'est exact :
personne n'a cliqué. Attribuer le run à qui a configuré le déclencheur des mois plus tôt serait une
information fausse dans une table qui existe pour être exacte.

---

## 6. Ce qui reste hors de portée

- **Aucune vérification de l'IP source.** Les plages de GitHub sont publiques et changent ; s'y
  fier ajouterait une dépendance réseau et une source de pannes silencieuses pour une garantie que
  la signature apporte déjà.
- **Aucune fenêtre d'anti-rejeu temporelle.** La déduplication par identifiant de livraison couvre
  le cas réel (la réémission) ; une fenêtre d'horodatage supposerait un en-tête d'horodatage signé,
  que ni GitHub ni GitLab n'envoient.
- **La purge de `trigger_deliveries` n'est pas automatisée.** L'index sur `received_at` la rend
  bon marché ; un `DELETE FROM trigger_deliveries WHERE received_at < NOW() - INTERVAL '30 days'`
  périodique suffit. C'est noté comme dette dans la feuille de route.
