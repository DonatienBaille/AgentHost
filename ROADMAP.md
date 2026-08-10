# Feuille de route — Agent Host

État de référence : branche `claude/specification-implementation-ppg4nn`.
560 tests backend, 778 tests frontend, 22 tests end-to-end Playwright, build sans warning.
(Chiffres mesurés en exécutant les trois suites après fusion, pas déduits.)

Ce document est la todolist du projet. Chaque lot indique **pourquoi** il existe, **ce qu'il
contient** concrètement, et **à quoi on reconnaît qu'il est fini**. L'ordre est un défaut
argumenté, pas un dogme : les lots 2 et 3 sont des fonctionnalités produit et peuvent remonter si
la priorité business le demande.

---

## Lot 0 — Tests complets de l'IHM ✅ terminé

**Livré.** 28 → 575 tests frontend, plus 8 tests end-to-end Playwright contre la vraie pile.
Les 16 composants et pages ont un spec, tous les services sont couverts contre
`provideHttpClientTesting`, les gardes et le routage aussi, et le temps réel est testé avec un hub
simulé (événements de run, changements d'état, approbation qui arrive sur le fil, mémoire de
projet). La CI exécute les tests de composants **et** Playwright dans un job `e2e` distinct.

Deux réserves à connaître :

- **Le parcours Playwright s'arrête au run lancé.** Approbation et téléchargement d'artefact
  exigent qu'un agent tourne réellement, donc un runtime de conteneurs. Le test vérifie que le run
  atteint un état *terminal* sans rechargement ; le chemin conteneur lui-même reste prouvé côté
  backend par `ContainerLifecycleTests`, que la CI force à s'exécuter.
- **La suite était non déterministe** (verte une fois sur deux) parce que
  `@angular/build:unit-test` désactive l'isolation des modules par défaut, ce qui rend `vi.mock()`
  dépendant de l'ordre de chargement. Corrigé par `frontend/vitest.config.ts` ; la suite passe de
  ~3 s à ~23 s. À garder en tête si quelqu'un est tenté de retirer ce fichier.

**Défauts d'IHM découverts pendant l'écriture des tests** — les 7 sont **corrigés**. Chacun était
épinglé par un test décrivant le comportement fautif ; ces tests ont été retournés, ce qui rend la
correction visible dans l'historique.

1. **Agent introuvable → page vide.** `agent.service.fetchAgent` avalait le 404 et renvoyait
   `null` en posant son propre signal d'erreur ; le `catch` de la page ne s'exécutait donc jamais
   et le corps restait entièrement vide. La méthode lève désormais, la page affiche son bloc
   d'erreur dédié, oublie l'agent précédent, et ne demande plus les versions d'un agent inexistant.
2. **Barrières de rôle absentes** sur les pages utilisateurs, webhooks et journal d'audit, alors
   que le serveur exige `maintainer`. Ajoutées, avec un message expliquant pourquoi la page est
   inerte plutôt qu'un 403 générique après remplissage d'un formulaire. Le journal d'audit ne
   demande plus au serveur ce qu'il n'a pas le droit de lire.
3. **Messages d'erreur anglais codés en dur** dans une IHM en français : les 8 services passent
   par des clés `errors.*`, présentes en `fr` et en `en`, et les 11 gabarits concernés les font
   traverser `| translate`. Les clés inconnues étant rendues telles quelles, un message venant du
   serveur passe sans dommage.
4. **Comparateur de tri incohérent** dans le tableau de bord (ne renvoyait jamais 0) : remplacé
   par `localeCompare`.
5. **Le tableau de bord avalait les échecs de chargement des projets** : l'erreur est désormais
   affichée, et le panneau dit « échec » au lieu de « aucun projet ».
6. **`submit()` / `publish()` ne revérifiaient pas le rôle** hors du gabarit (projets, agents,
   utilisateurs, webhooks) : garde ajoutée. Le serveur reste l'autorité — c'est de la défense en
   profondeur.
7. **Aucune action par ligne dans la page utilisateurs** : changement de rôle et suppression
   ajoutés (`PUT`/`DELETE /api/users/{id}`), réservés à `maintainer+`. Sa propre ligne n'offre
   aucune action : se rétrograder ou se supprimer pourrait priver l'organisation de son dernier
   administrateur sans qu'aucun écran ne le dise.

<details>
<summary>Énoncé initial du lot</summary>

**Pourquoi en premier.** Le frontend compte 28 tests, presque tous concentrés sur l'intercepteur
d'authentification. Tout le reste — pages, formulaires, gardes de routes, temps réel — n'a aucune
couverture. Or c'est la surface qui a le plus changé pendant la reprise (contrats modifiés, flux de
refresh, UI d'approbation devenue réellement atteignable) et celle où une régression est invisible
en CI aujourd'hui. Le backend a 289 tests ; le déséquilibre est le risque principal du dépôt.

**Contenu.**

- **Tests de composants** (Vitest + `@angular/build:unit-test`, déjà en place) sur chaque page :
  dashboard, liste et détail de projet, liste et détail d'agent, liste et détail de run,
  formulaire de nouveau run, et les cinq pages d'administration (users, organisations, secrets,
  webhooks, audit-log). Pour chacune : état vide, état d'erreur, état chargé, et masquage des
  actions selon le rôle de l'utilisateur.
- **Tests de services** : chaque service Angular contre `provideHttpClientTesting`, en vérifiant
  la **forme réelle des payloads** envoyés — c'est exactement là qu'un `orgId` résiduel ou un champ
  d'identité supprimé passerait inaperçu.
- **Gardes et routage** : accès anonyme redirigé vers `/login` avec `returnUrl` préservé, retour
  après connexion, routes admin inaccessibles aux rôles insuffisants.
- **Temps réel** : les composants qui s'abonnent à SignalR, avec un hub simulé — réception d'un
  événement de run, changement d'état, apparition d'une demande d'approbation.
- **Tests end-to-end Playwright** contre la stack réelle (Chromium et Playwright sont déjà
  disponibles dans l'environnement) : parcours inscription → connexion → création de projet →
  création d'agent → lancement de run → approbation → téléchargement d'artefact. C'est le pendant
  frontend du test E2E conteneur côté backend.
- **Intégration CI** : le job frontend doit exécuter les tests de composants **et** Playwright, et
  échouer sur régression. Aujourd'hui il n'exécute que `npm test`.

**Fini quand.** Couverture significative sur toutes les pages et tous les services ; au moins un
parcours Playwright complet vert en CI ; le job frontend casse quand on retire un champ d'un
contrat.

</details>

---

## Lot 1 — Franchir la barre de la production

**Pourquoi.** Trois choses empêchent aujourd'hui une mise en production défendable, et elles sont
liées.

### 1.1 Sortir l'orchestrateur dans un tier « runner » ✅ livré

Le backend parle au démon de conteneurs de **son propre nœud** : `StopAsync` et `GetLogsAsync`
cherchent le conteneur sur *ce* démon. D'où `replicaCount: 1`, HPA désactivé et stratégie
`Recreate` — chaque déploiement est une coupure de service, et la perte d'un pod est une panne
totale.

Découpler l'exécution derrière une API (DaemonSet, ou runner externe avec une file de travaux)
débloque d'un seul coup la scalabilité horizontale, la haute disponibilité et le déploiement sans
interruption. Le backplane Redis SignalR et le stockage objet S3 sont déjà en place : ce sont les
**prérequis** de ce chantier, pas sa solution.

Effet de bord à traiter dans le même mouvement : la tension actuelle entre `hostWorkspacePath`
(qui n'a de sens qu'avec un volume `hostPath`) et un PVC réseau. Avec un tier runner, le workspace
devient local au runner et le problème disparaît.

**Livré.** `AgentHost.Shared` (plomberie conteneur commune), `AgentHost.Runner` (l'orchestration
exposée sur HTTP, protégée par jeton porteur partagé), `RemoteContainerOrchestrator` côté backend,
et surtout la **migration 0009** : `runs.runner_url` rend l'affinité run → runner explicite et
persistée. C'est elle qui débloque `replicaCount > 1` — n'importe quelle réplique peut désormais
arrêter et lire un run qu'elle n'a pas lancé. Chart : runner en DaemonSet, backend redevenu
réplicable. Le mode `inprocess` reste le défaut, donc aucune installation existante ne change.

Les trois issues d'une annulation sont distinctes et aucune ne se présente comme un succès
silencieux : confirmée, `runner_unknown` (aucun runner enregistré), `runner_unreachable`. Détail
dans [docs/runner.md](docs/runner.md).

**Non prouvé** : aucun conteneur n'a jamais été lancé à travers le runner — pas de démon dans
l'environnement de développement (§1.3). Les 31 tests exercent le vrai `RunnerEndpoints` sur un
vrai socket devant un superviseur simulé, dont le scénario de la seconde réplique avec deux runners
distincts. Restent non exercés : le lancement réel, le DaemonSet sous Kubernetes, et plusieurs
répliques sous trafic concurrent.

### 1.2 Isolation d'exécution renforcée ✅ pour ce qui est faisable ici

Le socket du démon reste équivalent à root sur le nœud. Le proxy filtrant atténue, mais
« créer + démarrer un conteneur » suffit à atteindre l'hôte. Pour une plateforme dont le métier est
d'exécuter du code tiers, la réponse structurelle est un runtime isolé : gVisor, Kata Containers ou
sysbox.

Dans le même lot : réseau `--internal` pour que `permissions.network: allowlist` devienne
**contraignant** et non plus indicatif (aujourd'hui un socket TCP brut ignore `HTTP_PROXY`).

**Livré.** `Docker:Runtime` expose `HostConfig.Runtime` (`runsc`, `kata-runtime`, `sysbox-runc`),
vide par défaut puisque le runtime doit être installé côté démon. Et l'allowlist ne retombe plus
sur un bridge ordinaire : sans réseau `--internal`, c'est désormais un échec fermé — le conteneur
n'a pas de réseau du tout — avec `Docker:AllowUnconfinedAllowlist` comme sortie de secours
explicite et journalisée à chaque run. La décision est sortie de l'orchestrateur
(`Services/NetworkPolicyResolver.cs`) parce que c'est de la politique de sécurité et que tous ses
cas intéressants sont des refus ; 10 tests la couvrent.

**Non livré, et ce n'est pas un oubli** : gVisor, Kata et sysbox ne sont installés dans aucun
environnement accessible ici, donc `Docker:Runtime` n'a jamais été exercé contre un vrai runtime
isolé — voir §1.3.

### 1.3 Confronter au réel ce qui n'a jamais été exécuté — **bloqué ici**

Cinq surfaces ont été écrites et raisonnées, jamais observées contre le vrai service : **Podman**,
**l'E/S S3**, **l'API HIBP**, **l'envoi SMTP** et le **runtime isolé**. Elles compilent, leurs
tests passent, et leur comportement en production reste une hypothèse.

**Aucune n'est vérifiable dans l'environnement de développement utilisé jusqu'ici** — constaté, pas
supposé : le client Docker est présent mais aucun démon ne tourne (`/var/run/docker.sock` n'existe
pas), Podman n'est pas installé, aucun runtime isolé non plus, MinIO est impossible sans démon, et
`api.pwnedpasswords.com` est refusé par la politique réseau (403 sur le CONNECT du proxy). Ce volet
demande une machine autrement équipée ; il n'y a pas de contournement honnête.

Le détail de chaque surface, ce qui est en jeu et **comment la valider concrètement**, est dans
[docs/validation-reelle.md](docs/validation-reelle.md).

**Fini quand.** Plus d'un réplica backend sert du trafic sans casser le contrôle des runs ; un
déploiement ne coupe plus le service ; l'allowlist réseau résiste à une tentative de contournement ;
les trois surfaces ci-dessus ont tourné au moins une fois contre le vrai service.

---

## Lot 2 — Configuration des agents : deux modes, YAML et UI ✅ livré

**Pourquoi.** Définir un agent imposait d'écrire un manifeste YAML à la main. C'est puissant et
versionnable, mais cela réservait la création d'agents à un public technique, alors que la
plateforme vise aussi des utilisateurs métier qui doivent pouvoir composer un agent sans connaître
la syntaxe.

**Livré.** `components/manifest-editor`, branché sur les deux endroits qui écrivaient un manifeste à
la main : la création (`agents-list`) et la publication d'une nouvelle version (`agent-detail`).

- **Mode UI, par défaut.** Huit sections couvrant le manifeste : identité, type et image ou
  fournisseur externe avec ses réglages, constructeur de champs pour les entrées **et** les sorties,
  permissions (VCS, réseau, allowlist, secrets, docker, rootfs inscriptible), profil d'exécution,
  budgets, porte d'approbation. Chaque champ porte une phrase disant ce qu'il fait, en FR et EN.
- **Mode YAML, avancé.** Le même `<textarea>` qu'avant — aucune capacité retirée — plus la
  validation en direct contre `POST /api/agents/validate-manifest`, nouvel endpoint qui fait tourner
  le **vrai** `IAgentManifestParser` et renvoie la ligne fautive quand YamlDotNet la connaît.
- **Bascule réversible.** Le formulaire produit le YAML canonique ; le YAML se relit dans le
  formulaire par le serveur. L'aller-retour est sans perte sur un manifeste non trivial, et cette
  attente est vérifiée des deux côtés : le texte canonique attendu par le test frontend est rejoué à
  l'octet près contre le vrai parseur (`CanonicalManifestContractTests`).
- **Politique des constructions non représentables : préserver et nommer.** Une clé inconnue est
  gardée telle quelle et réémise à sa place ; un schéma hors du sous-ensemble éditable est gardé
  entier et rendu non éditable ; un bandeau nomme chaque cas avec son chemin précis. La bascule
  n'est refusée que dans le seul cas où il n'y a rien à projeter : un YAML qui ne parse pas.
- **Le YAML reste la source de vérité.** Rien du modèle de formulaire n'est persisté, et le YAML
  d'origine n'est réécrit qu'au premier vrai changement — ouvrir l'onglet pour regarder ne reformate
  le fichier de personne.
- **Aperçu du YAML généré en direct** depuis le mode formulaire.

Arbitrages, limites connues et méthode de vérification :
[docs/manifest-editor.md](docs/manifest-editor.md).

**Ce qui reste.**

- Pas de **coloration syntaxique** ni d'autocomplétion en mode YAML : c'est un `<textarea>` nu. La
  faire proprement veut dire une dépendance d'éditeur (CodeMirror, Monaco), qui n'a pas été prise.
- Les **ancres et alias YAML** sont développés par la projection JSON. Un fragment préservé qui en
  contenait est réémis développé : même sens, texte différent, et ce n'est **pas** signalé — la
  seule perte silencieuse qui subsiste.
- La détection des **commentaires** repose sur une heuristique textuelle : un `#` dans une chaîne
  non citée (`description: rapport #12`) déclenche un avertissement inutile. Faux positif assumé,
  faux négatif refusé.
- Le constructeur de champs ne **crée** ni schémas imbriqués, ni tableaux, ni compositions
  (`oneOf`, `$ref`, `additionalProperties`) ; il les préserve mais n'ouvre pas leur édition. Les
  ouvrir n'a de sens qu'une fois que `new-run-form` saura les rendre.
- La validation en direct est un aller-retour serveur, anti-rebond de 400 ms, sans repli hors ligne.
- Rien n'a été exercé dans un navigateur : les 97 tests du lot sont des tests de composant et
  d'intégration HTTP, pas une session réelle.

**Fini quand.** Un utilisateur non technique crée un agent fonctionnel sans écrire une ligne de
YAML ; un aller-retour formulaire → YAML → formulaire sur un manifeste non trivial est sans perte ;
les cas non représentables sont signalés explicitement. — Les deux derniers sont vérifiés par
tests ; le premier demande un utilisateur réel devant l'IHM.

---

## Lot 3 — Monitoring : OTEL en sortie, UI dans l'IHM ✅ livré

**Pourquoi.** L'instrumentation OpenTelemetry existe (traces, métriques, endpoint `/metrics`
Prometheus, export OTLP configurable), mais elle n'est exploitable qu'avec un Grafana ou équivalent
à côté. Un utilisateur de la plateforme n'a aujourd'hui **aucune vue** sur la santé et le coût de
ses agents depuis l'IHM.

**Principe directeur.** OTEL reste le canal d'exposition propre et complet — on n'invente pas un
système de métriques parallèle. L'IHM consomme un sous-ensemble agrégé, servi par l'API.

**Livré côté backend.** `Infrastructure/AgentHostMetrics.cs` ajoute les instruments **métier** qui
manquaient — l'instrumentation existante était purement technique (ASP.NET Core, HTTP, runtime,
Npgsql) et ne répondait qu'à « le service est-il en bonne santé » : durée de run, issue ventilée par
statut, coût cumulé, attente d'approbation, budgets épuisés, erreurs d'infrastructure, profondeur de
la file. Émis depuis `RunStateMachine` à chaque transition terminale, exposés par le Meter
`AgentHost.Business` sur `/metrics` et OTLP comme le reste.

La cardinalité est le piège de ce fichier et il est traité : étiquettes bornées par la taille de
l'installation (organisation, projet, agent, statut), **jamais** d'identifiant de run ou
d'utilisateur — un run est un événement, pas une dimension.

`Endpoints/MetricsEndpoints.cs` sert à l'IHM quatre agrégats scopés par le JWT : vue d'ensemble,
par agent, par projet, et série quotidienne. La source est `runs` et non les compteurs OTEL, parce
qu'une série Prometheus ne sait pas répondre « MES agents » sans donner accès à celles des autres,
et parce que l'attribution fine par utilisateur est justement ce que les étiquettes évitent. Les
deux canaux sont complémentaires, pas redondants.

10 tests d'intégration contre la vraie base — du SQL ne se vérifie pas à la lecture. Vérifié en
introduisant une fuite inter-organisation : 4 tests virent au rouge, dont celui qui existe pour ça.

**Livré côté IHM.** `pages/monitoring/` : chiffres de tête (runs, réussite, coût, durée moyenne,
runs en cours), ventilation des échecs — échecs ordinaires, erreurs d'infrastructure et budgets
épuisés comptés **séparément**, parce qu'un taux de réussite seul ne dit pas où intervenir —, série
quotidienne en barres, classements par agent et par projet avec la part de budget consommée, et
sélecteur de fenêtre (7 / 30 / 90 jours).

**Les alertes sont la partie utile.** Un tableau de chiffres demande à être lu et interprété ; une
alerte dit ce qui ne va pas. Trois seuils, dérivés des données déjà servies — donc rien à configurer,
donc rien à oublier de configurer : budget à 80 % (avertissement) puis dépassé (danger) ; agent qui
échoue sur plus de la moitié de ses runs, à partir de 5 runs — en dessous il n'y a rien à conclure ;
approbation en attente depuis plus de 24 h. Les dangers passent devant : personne ne lit la
troisième ligne d'un bandeau d'alertes.

Aucune dépendance de graphique : trente barres CSS coûtent moins qu'une bibliothèque et se lisent
aussi bien. Les barres sont mises à l'échelle du jour le plus chargé, et un jour à zéro garde une
barre d'un pixel pour se distinguer d'une absence de donnée.

**Livré : le journal d'audit devient consultable.** Il était servi par `WHERE org_id = ? ORDER BY
created_at DESC LIMIT ? OFFSET ?` : techniquement complet, pratiquement inutilisable. On ne consulte
pas un journal d'audit par curiosité — on l'ouvre parce que quelque chose s'est produit, et la
question a toujours la même forme : « qui a fait quoi, sur quoi, entre quand et quand ».

- **Filtres SQL** sur action, acteur, type et identifiant de ressource, et période. Le `WHERE` est
  construit à partir des seuls prédicats demandés plutôt qu'écrit en `(@X IS NULL OR col = @X)`,
  forme qui tient en une constante mais que le planificateur ne sait pas indexer.
- **Un total du jeu filtré**, par `COUNT(*) OVER()` donc dans la même lecture que la page. Sans lui
  la pagination ne peut ni annoncer « 1–50 sur 812 » ni savoir qu'elle est au bout.
- **Des facettes** (`/audit-log/facets`) : les actions, types et acteurs réellement présents, avec
  leur volume. Les actions sont un vocabulaire fermé côté serveur mais nulle part documenté côté
  client ; un champ libre obligerait à en deviner l'orthographe, et une faute de frappe rendrait un
  journal vide qu'on lirait comme « il ne s'est rien passé ».
- **L'acteur est nommé**, joint à `users` côté serveur. La table ne stocke qu'un ULID, que la page
  affichait tel quel. La jointure ignore délibérément `deleted_at` : un compte supprimé est
  justement l'acteur qu'on cherche à identifier.
- **`changes` et `details` sont enfin alimentés.** Les colonnes existaient depuis l'origine et
  personne n'y écrivait : le journal savait dire qu'un compte avait été modifié, jamais en quoi.
  `user.updated` porte le rôle avant/après, `user.created`/`user.deleted` l'adresse et le rôle de la
  cible. Le mot de passe n'y figure que comme « rotated ».
- **Migration 0010** : les index qui rendent ces filtres tenables, tous terminés par
  `created_at DESC` — le seul ordre dans lequel ce journal se lit.
- **Côté IHM** : barre de filtres alimentée par les facettes, pagination située dans le total,
  rebond d'une ligne vers tout ce que son acteur a fait ou vers l'historique complet d'un objet, et
  charges JSONB affichées au dépliement d'une ligne seulement. « Aucun résultat » et « journal
  vide » sont distingués : la première invite à élargir le filtre, la seconde dit qu'il n'y a rien à
  chercher.

22 tests d'intégration contre la vraie base, 29 tests de composant, 12 tests de service, 3 parcours
Playwright. Vérifié en sabotant le prédicat d'organisation, la borne haute de période et la
jointure d'acteur : 16 des 22 tests backend virent au rouge. Idem côté IHM sur la conversion de
borne, la remise à la première page et la barrière de rôle : 5 tests virent au rouge.

**Contenu.**

- **Compléter l'instrumentation côté métier**, au-delà du technique déjà en place : durée de run
  par agent, taux de réussite et d'échec, coût cumulé par projet / agent / utilisateur, temps
  d'attente d'approbation, dérive budgétaire, profondeur de la file d'attente, taux d'erreurs
  d'infrastructure.
- **Endpoints d'agrégation** côté API, scopés à l'organisation comme tout le reste, servant les
  séries dont l'IHM a besoin (fenêtre temporelle paramétrable).
- **Tableau de bord dans l'IHM** : vue d'ensemble organisation (runs sur la période, taux de
  réussite, coût, agents les plus actifs), vue par projet, vue par agent (historique de durée et de
  coût, régressions), et vue budget (consommation contre plafond mensuel, projection de
  dépassement).
- **Alertes visibles dans l'IHM** : budget bientôt épuisé, taux d'échec anormal sur un agent,
  approbations en attente depuis trop longtemps.
- **Le journal d'audit devient consultable et filtrable** correctement (aujourd'hui c'est une
  table brute paginée).

**Fini quand.** Un owner répond depuis l'IHM, sans outil externe : « combien m'ont coûté mes agents
ce mois-ci, lesquels échouent, et vais-je dépasser mon budget ? » — et un mainteneur retrouve « qui
a changé ce rôle, et quand » sans feuilleter.

---

## Lot 4 — Combler la spécification

### 4.1 Déclencheurs entrants ✅ livré

`TriggeredByType` déclarait `webhook`, `cron`, `api` et `chain` ; seuls `manual` et `api`
fonctionnaient. Un agent ne pouvait donc être lancé que par quelqu'un qui clique ou qui appelle
l'API avec un jeton — c'est-à-dire jamais tout seul, ce qui est pourtant la raison d'être d'une
plateforme d'agents autonomes.

**Webhooks entrants.** `POST /api/hooks/{id}`, anonyme par nécessité (c'est une forge qui appelle),
autorisé par la signature. Trois émetteurs, parce qu'on ne choisit pas comment GitHub signe : HMAC
SHA-256 sur le corps **brut** pour GitHub et le format générique, jeton en clair pour GitLab. Le
`sha1` hérité de GitHub est refusé. L'endpoint lit un `byte[]` — re-sérialiser le JSON changerait
espaces, ordre des clés et échappement, et toutes les signatures deviendraient fausses. Corps borné
à 1 Mio, comparaison à temps constant, déduplication des réémissions portée par la clé primaire de
`trigger_deliveries` (une vérification applicative laisserait passer deux réémissions simultanées).
Codes de retour pensés pour l'émetteur : 200 sur doublon et sur livraison filtrée, 422 sur refus
métier, 404 indistinctement pour inconnu / supprimé / désactivé.

**Planification cron.** Analyseur cinq champs écrit dans le dépôt — même arbitrage que pour le TOTP :
le besoin est un sous-ensemble délimité qui se teste exhaustivement, et les bibliothèques du domaine
apportent en prime un moteur de planification et leurs propres décisions sur les fuseaux. Le fuseau
est porté par le déclencheur (« tous les jours à 9 h » n'a pas de sens sans lui), la règle POSIX du
OU entre jour-du-mois et jour-de-semaine est respectée, et une heure locale inexistante (passage à
l'heure d'été) est sautée plutôt qu'inventée. Le planificateur bat toutes les 20 s et réserve les
échéances par un `UPDATE … WHERE next_run_at = @Expected RETURNING` : plusieurs répliques voient la
même échéance au même battement, et seule l'atomicité de cette instruction empêche le double
lancement — aucun verrou distribué, la ligne elle-même est le jeton. Les occurrences manquées ne
sont pas rattrapées : un backend arrêté trois jours ne doit pas relancer soixante-douze fois un
agent horaire.

**IHM.** `pages/projects/project-triggers/` : un formulaire pour les deux natures, le secret affiché
une seule fois avec l'URL à coller chez l'émetteur, l'échéance suivante calculée par le serveur,
activation/désactivation et suppression réservées à `maintainer`.

**Défaut trouvé en cours de vérification.** La première version validait le projet APRÈS l'insertion :
poser un déclencheur sur l'agent d'un autre locataire rendait bien un 404, en laissant derrière lui
un déclencheur parfaitement fonctionnel, avec son secret et son URL. La validation est remontée
avant toute écriture, et un test vérifie qu'il ne reste rien en base — le code de retour ne suffisait
pas à le prouver.

### 4.2 Chaînage de runs ✅ livré

`parent_run_id` et `root_run_id` étaient dans le schéma depuis l'origine et rien ne les écrivait.
Pire, la seule ligne qui touchait `root_run_id` posait `root = parent` : juste à la profondeur 1 et
faux ensuite — le petit-enfant aurait eu pour racine son parent, et l'arbre se serait scindé en deux
moitiés que rien ne relie, chacune ayant l'air correcte séparément.

`POST /api/agent/runs/{id}/chain` (jeton de run, non transitif) et `GET /api/runs/{id}/tree`,
lisible depuis n'importe quel membre de l'arbre — on arrive sur un run parce qu'il a échoué ou coûté
cher, et exiger la racine obligerait à la connaître déjà. Trois limites — profondeur 5, éventail 10,
arbre 50 — qui sont le garde-fou et non un raffinement : un agent qui se chaîne lui-même est une
boucle infinie dont chaque maillon est légitime. Un run chaîné doit viser un agent du même projet.
L'arbre est rendu dans la fiche du run, avec les totaux de la cascade.

### 4.3 Phase P3 — le cache Redis est réellement utilisé ✅ livré

Redis était déployé depuis l'origine et rangé par la spécification dans « cache/sessions », mais la
seule chose qui s'en servait était le backplane SignalR : la ligne de cette feuille de route
désignait exactement cet écart entre une dépendance déployée et une dépendance utile.

`Infrastructure/AggregateCache.cs` met en cache **les quatre agrégats du tableau de bord**, et rien
d'autre. Ils balayent `runs` sur trente à quatre-vingt-dix jours et l'écran porte un bouton
« Actualiser » : c'est le cas d'école du travail refait à l'identique. Ni un run, ni un agent, ni un
secret ne sont mis en cache — un cache sur des données qu'on lit pour agir doit être invalidé, et
une invalidation oubliée coûte bien plus cher que la lecture qu'elle économisait.

Trois décisions à connaître :

- **Expiration seule (30 s), pas d'invalidation.** Un tableau de bord sur trente jours est une aide
  à la décision, pas une console temps réel. Invalider à chaque transition de run rendrait le cache
  inutile précisément quand il sert — sur une organisation active.
- **La clé porte l'organisation ET la fenêtre.** Deux locataires qui partageraient une clé
  partageraient leurs chiffres ; un test existe pour interdire ça.
- **Sans Redis, le comportement est exactement celui d'avant.** `NoAggregateCache` passe tout au
  calcul direct, et toute erreur Redis retombe sur la requête — un cache qui fait échouer la lecture
  qu'il devait accélérer est pire que pas de cache du tout.

6 tests, dont 5 contre un vrai Redis (sautés explicitement, jamais passés en silence, quand aucun ne
répond — même dispositif que `DockerFactAttribute`).

### 4.4 Défaut trouvé en exerçant la vraie sortie `/metrics`

La jauge `agenthost.run.in_flight` — la profondeur de la file, livrée au lot 3 — était **fausse**,
et de deux façons indépendantes :

1. `RunQueued` était défini et appelé **nulle part**. La jauge ne faisait que décroître.
2. Un run refusé avant tout lancement (`Pending → Rejected`) était décompté à la sortie sans avoir
   jamais été compté à l'entrée.

Résultat : `agenthost_run_in_flight -1` après un seul run, et une profondeur de file négative dans
tout tableau de bord d'exploitation. Rien de tout cela ne se voyait à la lecture — les deux côtés du
compteur existaient et étaient correctement étiquetés — et aucun test ne l'attrapait, parce que tous
les tests utilisaient un instrument **sans collecteur** : il s'exécute, il n'écrit nulle part, et
personne ne regarde la somme.

Il a fallu lancer un vrai backend et lire sa sortie `/metrics` pour le voir. Corrigé, et couvert par
trois tests qui branchent un `MeterListener` — le mécanisme même de l'exporteur — de sorte que la
somme soit **observée** et non seulement émise. Vérification finale contre l'exporteur réel : la
série vaut désormais 0 après un run complet.

C'est la raison pour laquelle l'observabilité opérationnelle ci-dessous n'est pas qu'un travail de
configuration : les règles d'alerte se poseront sur ces séries, et une série fausse produit une
alerte fausse ou, pire, silencieuse.

### 4.5 Observabilité opérationnelle ✅ livré

`deploy/observability/alerts.yml` — neuf règles Prometheus, en trois familles : disponibilité,
exécution des agents, dépendances. Distinctes du lot 3 : celui-ci répond à un utilisateur (« combien
m'ont coûté mes agents »), celles-là à un exploitant (« le service tient-il debout »), et n'ont pas
de notion d'organisation.

**Un fichier de configuration mérite des tests**, parce qu'une règle qui interroge une série
inexistante ne produit aucune erreur : elle produit zéro, indéfiniment, donc une alerte qui ne se
déclenche jamais — le pire des deux mondes, l'exploitant se croyant couvert. `OperationalAlertsTests`
confronte le fichier aux instruments **réellement publiés** (obtenus d'un `MeterListener`, pas d'une
liste recopiée qui divergerait au premier ajout), et exige que chaque instrument soit soit alerté,
soit explicitement exclu **avec sa raison**. Vérifié en renommant une série avec un « s » de trop :
deux tests virent au rouge.

Une règle inhabituelle mérite d'être signalée : `AgentHostQueueDepthNegative` surveille une
impossibilité. Elle existe parce que c'est arrivé (§4.4), et parce qu'un tableau de bord affichant
`-3` se lit comme « rien à signaler ».

**Pas de tableau de bord Grafana livré**, et c'est délibéré : un JSON de dashboard n'est vérifiable
par rien ici — il ne compile pas, aucun test ne l'exécute, et une capture d'écran ne prouve pas qu'il
interroge les bonnes séries. `docs/observabilite-operationnelle.md` donne la table des séries à
partir de laquelle en construire un juste.

**Purge de `trigger_deliveries`** : intégrée à `RunDataJanitor` (`Retention:TriggerDeliveryDays`,
30 jours par défaut, 0 = désactivé). C'est la seule table du schéma dont la croissance n'est bornée
par rien — ni par un nombre de runs, ni par un nombre d'utilisateurs, seulement par le trafic
entrant. Le test vérifie surtout qu'une entrée **récente survit** : c'est elle qui empêche une
réémission de relancer l'agent.

### 4.6 Reste à faire dans ce lot

- **Pools de conteneurs pré-chauffés** et **réutilisation de workspaces** entre runs d'un même
  projet : les deux exigent un runtime de conteneurs, que cet environnement n'a pas (voir
  `docs/validation-reelle.md`, même famille que le point 1.3). Écrire ces chemins sans jamais les
  exécuter produirait du code qui compile et dont personne ne sait s'il fonctionne.

---

## Lot 5 — Évolutions naturelles du produit

- **Marketplace d'agents.** Les agents sont déjà versionnés, empreintés en SHA-256 et déclaratifs :
  le partage entre projets, puis entre organisations, est la suite logique.
- **Politiques d'organisation.** Le modèle de permissions est *par agent* ; il manque une couche
  au-dessus : imposer qu'aucun agent ne tourne sans approbation, interdire certaines images
  ou registres, plafonner les budgets par rôle.
- **Mémoire agentique v2.** La détection de motifs actuelle est du pattern-matching sur les 20
  derniers runs. La spec ambitionne une mémoire réellement exploitable par l'agent : recherche
  sémantique, injection de contexte pertinent au lancement.
- **Analyse de coûts avancée** : attribution fine, tendances, prévision, comparaison entre agents
  pour une même tâche.

---

## Dette identifiée, hors lots

Traité :

- ✅ **`audit_log` est append-only** (migration `0008`) : trois triggers refusent `UPDATE`, `DELETE`
  et `TRUNCATE`. Vérifié contre une vraie base, et vérifié que les triggers survivent à une
  restauration de sauvegarde. Limite assumée et documentée : le propriétaire de la table peut
  désactiver un trigger, et l'application est aujourd'hui propriétaire — un WORM réel demande une
  séparation de rôles qui relève du déploiement.
- ✅ **Rotation de la clé de chiffrement des secrets** : `Secrets:PreviousEncryptionKeys` accepte les
  anciennes clés en déchiffrement, `dotnet AgentHost.Api.dll --rekey-secrets` réécrit l'existant.
  Couvre les deux colonnes chiffrées, y compris les seeds TOTP — en oublier une verrouillerait tous
  les comptes à second facteur. Idempotent, et ne détruit jamais une valeur qu'il ne sait pas lire.
- ✅ **Sauvegarde et restauration** : `scripts/backup.sh`, `scripts/restore.sh` et
  [docs/operations.md](docs/operations.md). Les deux scripts ont été exécutés contre une vraie base.
- ✅ **Mailer** : `IEmailSender` avec no-op par défaut et SMTP optionnel, câblé sur la
  réinitialisation de mot de passe et les invitations. L'envoi est hors du chemin de réponse pour ne
  pas rouvrir l'oracle d'énumération que le 202 plat referme.

Reste à traiter :

- ✅ **La file d'envoi de courriels est durable** (migration `0013`, table `email_outbox`). Un échec
  de remise est désormais réessayé avec un recul exponentiel qui survit au redémarrage — c'est le
  gain principal, l'ancienne file n'en offrait rien — et un message qu'on renonce à envoyer reste
  visible en base avec sa dernière erreur, au lieu de disparaître dans une ligne de journal.

  **La persistance a lieu côté consommateur, pas dans la requête**, et ce n'est pas un raccourci :
  écrire en base depuis `POST /api/auth/password-reset/request` rouvrirait l'oracle d'énumération
  par le temps ET par l'échec, que le 202 plat referme. La mise en file reste donc une écriture en
  mémoire, et la fenêtre de perte résiduelle — quelques millisecondes entre la mise en file et
  l'écriture — est assumée et énoncée telle quelle, au lieu de la couvrir d'une garantie qu'elle
  n'a pas.

  **Le corps est chiffré au repos.** Il contient le jeton en clair, et le dépôt ne stocke jamais un
  jeton autrement que par empreinte : persister le corps tel quel aurait défait cette propriété par
  la porte de service, une ligne en attente devenant un lien de réinitialisation utilisable par
  quiconque lit la table. Même clé et même rotation que les secrets applicatifs. Une remise réussie
  **supprime** la ligne — un message acheminé n'a plus de raison de garder un secret en base.

  9 tests de comportement + 8 d'intégration SQL (dont la course entre deux répartiteurs, que seul
  `FOR UPDATE SKIP LOCKED` tranche).
- Le TLS implicite du port 465 n'est pas géré par `SmtpEmailSender` (limite de
  `System.Net.Mail.SmtpClient`) ; un déploiement qui n'a que du 465 doit passer par un relais local
  ou justifier l'ajout de MailKit.
- ✅ **`SmtpEmailSender` parle enfin un vrai SMTP.** Aucun relais n'étant joignable ici, le serveur
  a été amené à soi : `FakeSmtpServer` (≈ 200 lignes) parle EHLO, STARTTLS avec un certificat
  généré en mémoire, AUTH PLAIN et LOGIN, MAIL/RCPT/DATA. Six tests exercent le chemin complet du
  vrai `SmtpClient`.

  **Un défaut est tombé aussitôt** : `SmtpClient.Timeout` est **ignoré par `SendMailAsync`** — il ne
  gouverne que les surcharges synchrones. Un relais qui accepte la connexion puis se tait faisait
  donc pendre l'envoi indéfiniment. Or `EmailDispatcher` est un consommateur unique : **un seul
  envoi bloqué arrêtait toute la remise de courriels de l'installation**, sans erreur et sans trace.
  Mesuré (20 s sans abandon pour un délai configuré à 2 s), pas déduit. Le délai est désormais porté
  par un jeton d'annulation, que `SendMailAsync` respecte, et un dépassement lève une
  `TimeoutException` distincte d'une annulation par l'appelant — confondre les deux rendrait
  l'extinction du processus indiscernable d'un incident.

  Ce que ces tests **ne** prouvent pas : le certificat est auto-signé et sa validation est
  désactivée pendant le test. La négociation TLS est réellement exercée, la vérification de chaîne
  ne l'est pas. Un relais public reste à confronter une fois, au même titre que Podman, S3 et HIBP
  (lot 1.3).
- ✅ Les écrans `/reset-password` et `/accept-invitation` existent : les liens des courriels mènent
  désormais quelque part. La réinitialisation affiche **le même message que l'adresse existe ou
  non**, pour ne pas rouvrir côté IHM l'oracle d'énumération que le 202 plat du serveur referme.
- ✅ **Le comportement sous concurrence est mesuré, et il a livré quatre défauts.** Pas un banc de
  performance : « combien de requêtes par seconde » dépend de la machine et n'a jamais rien empêché.
  La question qui manquait est autre — **les invariants tiennent-ils quand deux appelants arrivent
  ensemble ?** 9 tests d'intégration lancent leurs appels sur la vraie pile HTTP et la vraie base,
  libérés d'un seul coup par un point de rendez-vous. Les quatre défauts trouvés étaient tous
  invisibles sur une requête isolée :

  **La numérotation des runs n'était pas protégée.** Un verrou consultatif entourait le
  `SELECT MAX(number) + 1` puis était relâché *avant* l'insertion : il protégeait la lecture, qui
  n'en avait pas besoin, et laissait nu l'intervalle lecture→écriture, le seul où la course a lieu.
  Douze créations simultanées en refusaient cinq, avec une violation de contrainte remontée en
  erreur 500. Remplacé par un compteur porté par la ligne du projet (migration `0014`),
  `UPDATE … RETURNING`, qui n'a pas d'intervalle.

  **La machine à états écrivait sans condition.** Elle validait la transition sur l'objet lu un
  instant plus tôt, puis écrivait. Une annulation humaine et une expiration du chien de garde
  arrivant ensemble passaient toutes deux : deux salves de webhooks `run.finished` pour un run, deux
  entrées d'historique, et deux décréments de `agenthost.run.in_flight` pour un seul incrément —
  la jauge négative, atteignable par un second chemin. L'écriture porte désormais l'état attendu en
  condition.

  **Les réponses d'approbation se perdaient.** Lire la liste, y ajouter la sienne, réécrire la
  liste : trois approbateurs simultanés écrivaient trois listes d'un élément, dont il ne restait que
  la dernière. Un garde exigeant trois approbations ne pouvait donc **jamais** être satisfait par
  trois personnes cliquant ensemble — précisément la situation pour laquelle il existe. L'ajout se
  fait maintenant par concaténation `jsonb` en base, et la décision est une porte que seul un
  appelant franchit.

  **La rotation des jetons ne tournait pas.** `RefreshAsync` émettait la nouvelle paire *puis*
  révoquait l'ancienne : deux onglets rafraîchissant au même instant obtenaient chacun une paire
  valide — deux familles vivantes issues d'une seule, et la détection de vol de jeton, qui repose
  entièrement sur le rejeu d'un jeton révoqué, rendue inopérante. La révocation est passée avant
  l'émission, et son `UPDATE … WHERE revoked_at IS NULL` n'a qu'un gagnant.

  Un cinquième, plus discret, est corrigé au passage : deux inscriptions simultanées avec la même
  adresse rendaient une erreur 500 (la contrainte d'unicité remontait brute) **et** laissaient une
  organisation vide par tentative refusée — l'organisation étant créée avant l'utilisateur, sans
  transaction commune. Le refus est désormais un 409, comme dans le cas séquentiel, et
  l'organisation orpheline est défaite.

  Ce que ces tests **ne** prouvent pas : rien sur la tenue en charge réelle — nombre de connexions,
  saturation du pool, dégradation à chaud. Le limiteur de débit est neutralisé dans la fabrique de
  test, si bien que le test de rafale vérifie qu'une salve ne met pas en file, pas que le seuil est
  le bon.

  Un point de comportement énoncé plutôt que corrigé : **le plafond mensuel d'un projet reste un
  lire-puis-décider**, donc une rafale de créations peut le franchir d'un run. Sérialiser toutes les
  créations d'un projet derrière un verrou coûterait, sur un projet actif, plus que le dépassement
  — borné par le budget d'un seul run, chaque run ayant ensuite son propre plafond contrôlé. La
  garantie exacte est donc : le plafond arrête un projet, mais la dernière rafale peut le franchir
  d'un run.
- ✅ **Purge RGPD réelle** : `dotnet AgentHost.Api.dll --purge-org <orgId>` efface définitivement
  une organisation et tout ce qui en dépend, en une seule transaction sur une vingtaine de tables.
  La suppression ordinaire posait un `deleted_at` — le bon comportement pour une erreur de
  manipulation, mais un droit à l'effacement auquel on répond par un drapeau est mimé, pas honoré.

  Trois points valent d'être connus. **La purge tourne contre l'application vivante** : elle
  verrouille les lignes parentes (`FOR UPDATE`) avant d'effacer, sinon un run en cours écrit un
  événement pendant l'opération et la clé étrangère fait échouer la transaction — ce n'est pas un
  cas de test, c'est le cas normal. **Le journal d'audit part avec le reste**, ce qui est la seule
  dérogation au WORM du dépôt : la responsabilité qu'il établit s'exerce à l'intérieur d'une
  organisation vivante, et conserver l'activité de ses membres après effacement est exactement la
  conservation que le droit interdit. Le trigger est rétabli même en cas d'échec, et un test le
  vérifie sur une autre organisation. **L'ordre d'effacement est écrit à la main**, pas dérivé des
  clés étrangères : une purge qui découvrirait l'ordre toute seule effacerait aussi ce que personne
  n'a relu.

  6 tests d'intégration, dont un qui confronte la liste des tables purgées au **schéma réel** — une
  table ajoutée sans être purgée y laisserait des données personnelles hors d'atteinte.

  Ce qui reste hors de portée : les fichiers hors base (workspaces, artefacts) relèvent de la
  rétention, et une sauvegarde antérieure ramènerait les données si on la restaurait.
- ✅ **Restauration à un instant précis (PITR)** : archivage WAL, `scripts/basebackup.sh`,
  `scripts/restore-pitr.sh`, overlay `docker-compose.pitr.yml`, et §6 de
  [docs/operations.md](docs/operations.md). Le dump quotidien répondait à « la machine a brûlé » et
  pas à « quelqu'un a lancé la mauvaise commande à 14 h 32 » : il ramène l'état du dump, donc
  jusqu'à 24 h de perte, et ne sait pas viser un instant.

  **Vérifié de bout en bout contre une vraie grappe PostgreSQL 16**, et pas seulement écrit :
  sauvegarde de base, écriture, suppression accidentelle d'une ligne, restauration à un instant
  antérieur. L'instance restaurée portait la ligne supprimée et ignorait l'écriture postérieure à la
  cible, pendant que la production restait inchangée. Trois défauts sont tombés en cours de route,
  tous invisibles à la lecture : `pg_ctl` n'est pas dans le PATH sur Debian et Ubuntu (la
  distribution n'expose que ses enveloppes, qui refusent un répertoire de données arbitraire) ; la
  sauvegarde physique **n'emporte pas la configuration** sur ces mêmes distributions, qui la rangent
  hors du répertoire de données, si bien que l'instance restaurée refusait de démarrer ; et
  `pg_ctl start --wait` rend la main dès que le serveur accepte des connexions — **en lecture
  seule, pendant la reprise** — de sorte qu'un script qui conclut là tient pour restaurée une
  instance encore en train de rejouer.

  Le script génère donc une configuration minimale en lisant les quatre réglages de dimensionnement
  dans le fichier de contrôle de la sauvegarde elle-même — la seule source qui décrive la grappe
  d'origine — et attend la fin de la reprise en interrogeant le serveur.

  La restauration se fait dans un répertoire neuf et sur un port distinct : la grappe d'origine
  n'est jamais touchée. Une procédure qui écrase la production pour être vérifiée n'est pas une
  procédure, c'est un second incident.
- **Pas de réplication ni de bascule automatique.** L'archivage ci-dessus est la moitié du chemin :
  une réplique en flux se monte à partir des mêmes éléments. Ce qui manque n'est pas la sauvegarde
  mais la bascule — détection de panne, adresse virtuelle, protection contre le double primaire —
  qui relève d'un gestionnaire de grappe (Patroni, repmgr) et du déploiement. Voir §7 de
  [docs/operations.md](docs/operations.md).
