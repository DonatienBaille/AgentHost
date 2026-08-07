# Feuille de route — Agent Host

État de référence : branche `claude/specification-implementation-ppg4nn`.
CHIFFRES_A_MESURER

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

## Lot 3 — Monitoring : OTEL en sortie, UI dans l'IHM 🔶 backend fait, IHM à faire

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

**Reste à faire : tout le tableau de bord dans l'IHM**, les alertes visibles, et le journal d'audit
filtrable. Les données sont servies ; rien ne les affiche encore.

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
ce mois-ci, lesquels échouent, et vais-je dépasser mon budget ? »

---

## Lot 4 — Combler la spécification

- **Déclencheurs.** `TriggeredByType` prévoit `webhook`, `cron`, `api` et `chain` ; seuls `manual`
  et `api` fonctionnent. Manquent : les **webhooks entrants** (un push GitHub qui lance un agent,
  avec vérification de signature) et la **planification cron**.
- **Chaînage de runs.** Le schéma porte déjà `parent_run_id` et `root_run_id`, l'enum a `chain`,
  mais rien ne les exploite : un agent qui en déclenche un autre est une capacité à moitié
  modélisée. Inclut la visualisation de l'arbre de runs.
- **Phase P3 — optimisation**, non entamée : cache Redis réellement utilisé (il n'est aujourd'hui
  que backplane SignalR), pools de conteneurs pré-chauffés pour supprimer la latence de démarrage,
  réutilisation de workspaces entre runs d'un même projet.
- **Observabilité opérationnelle** (§14) : tableaux de bord et alertes côté exploitation, distincts
  du lot 3 qui vise l'utilisateur final.

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

- La file d'envoi de courriels est **en mémoire et non persistante** : un arrêt brutal du processus
  perd les messages pas encore acheminés. Acceptable pour un courriel transactionnel qu'on peut
  redemander, mais une table d'attente (« outbox ») serait le vrai correctif — le point d'extension
  est `Services/Email/EmailDispatcher.cs`, sans changement pour les appelants.
- Le TLS implicite du port 465 n'est pas géré par `SmtpEmailSender` (limite de
  `System.Net.Mail.SmtpClient`) ; un déploiement qui n'a que du 465 doit passer par un relais local
  ou justifier l'ajout de MailKit.
- **`SmtpEmailSender` n'a jamais parlé à un vrai serveur SMTP** : aucun relais n'est joignable dans
  l'environnement de développement. Sa sélection par configuration et sa construction sont testées ;
  la poignée de main STARTTLS, l'authentification et le délai d'expiration reposent sur le contrat
  documenté du BCL, pas sur une observation. À confronter au réel une fois, comme Podman, S3 et HIBP
  (lot 1.3).
- ✅ Les écrans `/reset-password` et `/accept-invitation` existent : les liens des courriels mènent
  désormais quelque part. La réinitialisation affiche **le même message que l'adresse existe ou
  non**, pour ne pas rouvrir côté IHM l'oracle d'énumération que le 202 plat du serveur referme.
- Aucun test de charge : le comportement sous concurrence est inconnu.
- Pas de suppression en cascade au-delà du soft-delete organisation/projet (purge RGPD réelle).
- Pas de réplication ni de restauration à un instant précis (PITR) — voir §6 de
  [docs/operations.md](docs/operations.md).
