# Contribuer

Les conventions de ce dépôt, et surtout la méthode de travail qui les explique.

---

## 1. La règle qui gouverne les autres

**Un test vert ne prouve rien tant qu'on n'a pas vu ce qui le fait rougir.**

Tout mécanisme non trivial ajouté ici est vérifié par **sabotage** : on défait délibérément le
correctif, on constate que le bon test passe au rouge, on restaure, on reconstate le vert. Ce n'est
pas une formalité — cette discipline a montré, dans ce dépôt, qu'un test d'approbation concurrente
restait vert alors qu'on avait défait le mécanisme qu'il était censé couvrir (un *autre* garde le
protégeait), et qu'une course choisie pour un test ne s'était jamais produite parce que la transition
retenue n'était pas valide.

Corollaire : **dire ce qui n'est pas vérifié**. Un document qui tait ses angles morts est plus
dangereux qu'un document absent. `docs/testing.md` §6 tient cette liste ; l'alimenter fait partie du
travail.

---

## 2. Langue

| Élément | Langue |
| --- | --- |
| Commentaires de code | **Français** |
| Documentation, `README`, `ROADMAP` | **Français** |
| Messages de commit | **Français** |
| Noms de tests | **Anglais**, sous forme de phrase : `Concurrent_run_creations_never_collide_on_a_run_number` |
| Identifiants de code (classes, méthodes, variables) | **Anglais** |
| Chaînes visibles par l'utilisateur | **ngx-translate**, jamais en dur dans un gabarit |

---

## 3. Commentaires : le *pourquoi*, jamais le *quoi*

Un commentaire qui paraphrase la ligne suivante est du bruit. Un commentaire utile répond à l'une de
ces questions :

- **Pourquoi cette approche plutôt que l'évidente ?** (« un `SELECT MAX + 1` sous concurrence produit
  deux fois le même numéro »)
- **Qu'est-ce qui casse si on l'enlève ?** (« sans cette condition, quatre approbations simultanées
  reprennent quatre fois le même run »)
- **Qu'est-ce qui a été mesuré plutôt que déduit ?** (« mesuré : 20 s sans abandon pour un délai
  configuré à 2 s »)

Les arbitrages assumés s'écrivent **en toutes lettres**, y compris quand ils sont inconfortables — le
dépassement de plafond borné à un run, la dérogation WORM de la purge, le risque résiduel du socket.

---

## 4. Code backend

- **Quatre couches, une règle chacune** : les endpoints traduisent HTTP ↔ domaine (aucune règle,
  aucun SQL), les services portent les règles (aucun `HttpContext`, aucun Npgsql), les dépôts portent
  le SQL (aucune règle), le domaine ne dépend de rien. Voir
  [docs/architecture.md](docs/architecture.md) §2.
- **L'isolation multi-locataire est dans le `WHERE`**, jamais dans un `if` après coup. Une ressource
  d'un autre locataire rend **404, jamais 403**.
- **Les invariants concurrents se décident dans l'instruction SQL qui écrit** — `UPDATE … WHERE
  <état attendu>`, `… RETURNING`, `FOR UPDATE SKIP LOCKED`. Un lire-puis-écrire en C# n'exclut rien.
- **Les colonnes énumérées sont typées `string` dans les DTO de ligne** (`Repositories/DbRows.cs`) :
  Dapper court-circuite ses gestionnaires de types pour un paramètre énuméré et écrirait l'entier.
  Les conversions passent par `ToDbString()` / `FromDbString()`, et une contrainte `CHECK` refuse
  toute valeur non documentée.
- **`IDbConnectionFactory.CreateConnection()` rend une connexion DÉJÀ ouverte.** L'ouvrir à nouveau
  lève.
- **`ISecretsBroker` est *scoped*** : un singleton de fond doit le résoudre via
  `IServiceScopeFactory`.

## 5. Code frontend

Composants standalone, Signals, nouveau flux de contrôle (`@if` / `@for` / `@let`), `OnPush`. Pas de
gestion d'état globale : l'état serveur vit dans des services à signaux, l'état d'écran dans les
composants.

---

## 6. Migrations

- Fichiers SQL **numérotés**, appliqués au démarrage, **idempotents**.
- **Jamais réécrites une fois livrées.** Une correction est une nouvelle migration.
- **Pas de « down ».** Une migration qui se défait est une migration qu'on écrit à l'endroit.
- Un en-tête de commentaire explique **ce que la migration corrige et pourquoi**, pas ce qu'elle
  fait.

---

## 7. Tests

- **Un double ne remplace pas une dépendance dont le comportement est l'objet du test.** Les tests du
  répartiteur de courriels utilisent une file en mémoire — leur objet est le répartiteur. Les tests
  d'envoi utilisent un vrai serveur SMTP — leur objet est le dialogue.
- **Un test sauté n'est pas un test passé.** Ce qui ne peut pas s'exécuter se déclare `SKIPPED` avec
  sa raison, et la CI échoue si les tests conteneur skippent.
- **Les tests de concurrence lancent leurs appels réellement en parallèle**, libérés par un point de
  rendez-vous. Les sérialiser les rendrait verts sans rien prouver.
- Attention aux crochets **globaux au processus** (`MeterListener`, `ServicePointManager`) : filtrer
  par **identité** et non par nom, ou préférer un crochet porté par instance.

---

## 8. Commits

Sujet court à l'impératif, puis un corps qui explique **le problème et l'arbitrage**, pas la liste
des fichiers touchés — le diff la donne déjà.

```
Dette : mesurer le comportement sous concurrence, et corriger ce qu'il révèle

La dette disait « aucun test de charge ». Un banc de performance n'aurait rien
appris — « combien de requêtes par seconde » dépend de la machine. La question
utile est celle des invariants quand deux appelants arrivent ensemble.

[…] Vérification par sabotage : chacune des cinq corrections a été défaite tour
à tour, et le test correspondant est passé au rouge à chaque fois.
```

Terminer par l'état des suites (`N tests backend, tous verts`) quand le changement touche du code.

---

## 9. Avant de proposer une modification

```bash
cd backend  && dotnet build && dotnet test
cd frontend && npm test && npm run build
```

Et mettre à jour, si le changement les concerne : [ROADMAP.md](ROADMAP.md),
[docs/testing.md](docs/testing.md) §6 (ce qui n'est pas vérifié), et
[docs/configuration.md](docs/configuration.md) (toute nouvelle clé).
