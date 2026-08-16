# Tests : ce qui est exécuté, ce qui est raisonné

La distinction que ce document tient à jour n'est pas cosmétique. **Un test sauté se présente comme
un succès**, et c'est exactement l'illusion que cette suite existe pour supprimer.

---

## 1. Lancer les suites

```bash
# Backend — nécessite PostgreSQL (les tests d'intégration démarrent le vrai pipeline Program)
cd backend && dotnet test

# Frontend — Vitest via @angular/build:unit-test
cd frontend && npm test

# Bout en bout — Playwright, pile complète
cd frontend && npm run e2e
```

Sans démon de conteneurs, `ContainerLifecycleTests` se déclare **SKIPPED** avec la raison et le
socket sondé — jamais « passé ». Avec un démon joignable, comptez ~20 s de plus, un pull d'`alpine`
et une image locale jetable supprimée en fin de test.

**La CI échoue si ces tests se contentent de skipper.**

---

## 2. La méthode : vérification par sabotage

Un test vert ne prouve rien tant qu'on n'a pas vu ce qui le fait rougir. Chaque mécanisme central de
ce dépôt a été vérifié ainsi : **défaire délibérément le correctif, constater que le bon test passe
au rouge, restaurer, reconstater le vert.**

Cette discipline a rapporté plus que les tests eux-mêmes. Trois exemples :

- Un test d'approbation concurrente restait vert alors qu'on avait défait l'écriture conditionnelle
  de la machine à états — parce qu'un *autre* garde le protégeait. Il a fallu écrire un test dédié
  pour couvrir réellement ce mécanisme.
- Le premier couple de transitions choisi pour une course (`running → succeeded`) n'était pas une
  transition valide : le second appel était écarté avant de courir, et le test ne testait rien.
- Un test de jauge passait isolément et échouait dans la suite complète : `MeterListener` est global
  au processus, et le filtre portait sur le *nom* du compteur au lieu de son **identité**.

---

## 3. Réellement exécuté contre un vrai conteneur

Le chemin cœur du produit — lancer un agent dans un conteneur — a longtemps été le moins testé : les
tests de cycle de vie écrivaient en base l'état qu'un conteneur *aurait* produit. Un défaut
d'encodage a pu, dans ce dépôt, faire renvoyer un dictionnaire de secrets **vide** pendant toute la
vie du projet sans qu'aucun test ne le remarque.

`Integration/ContainerLifecycleTests.cs` exécute ce chemin pour de bon, contre un vrai démon. L'agent
est `alpine` plus un script shell, commité à la volée dans un tag local jetable — aucun Dockerfile,
aucune image de fixture, aucun registre. Le conteneur joint l'API par la **gateway du bridge**, lue
sur le démon et non codée en dur.

| Étape | Vérification |
| --- | --- |
| Création du conteneur | Flags §13.2 **tels que le démon les a enregistrés** : `ReadonlyRootfs`, `CapDrop: ALL`, `CapAdd: NET_BIND_SERVICE`, `SecurityOpt`, tmpfs `/tmp`, `NetworkMode` |
| Limites du manifeste | `NanoCPUs`, `Memory`/`MemorySwap` (sans swap) comparés au profil résolu du run |
| Livraison des secrets | Le conteneur lit `/run/secrets/<NOM>` et compare la valeur **octet pour octet** à celle stockée par `POST /api/secrets` |
| Secrets hors environnement | Aucune variable `SECRET_*` — vérifié dans le conteneur *et* dans la config vue par `docker inspect` |
| Protocole agent | `POST /api/agent/runs/{id}/events` avec `AGENTHOST_RUN_TOKEN` **depuis le conteneur**, relu via `GET /api/runs/{id}/events` |
| `/workspace` | Bind mount inscriptible : le fichier écrit par l'agent est relu sur l'hôte |
| Fin de run | Sortie 0 → `succeeded`, `exit_code`, `started_at`/`finished_at` |
| Nettoyage | Conteneur supprimé, **répertoire de secrets en clair effacé** |

---

## 4. Réellement exécuté contre une vraie dépendance

| Dépendance | Ce qui est exercé |
| --- | --- |
| **PostgreSQL 16** | Toute la suite d'intégration démarre le vrai pipeline `Program` sur une vraie base. |
| **Serveur SMTP** | `FakeSmtpServer` (≈ 200 lignes) parle EHLO, STARTTLS **et TLS implicite** avec un certificat généré en mémoire, AUTH PLAIN et LOGIN, MAIL/RCPT/DATA. Le vrai client MailKit fait le trajet complet. |
| **PITR PostgreSQL** | Sauvegarde de base, incident simulé, restauration à un instant antérieur — exécutée à la main contre une vraie grappe (voir [operations.md](operations.md) §6). |

Ces confrontations ont trouvé des défauts qu'aucune relecture n'avait vus : `SmtpClient.Timeout`
**ignoré** par `SendMailAsync` (un envoi bloqué arrêtait toute la remise de l'installation), `pg_ctl`
absent du PATH sur Debian, et une sauvegarde physique qui **n'emporte pas la configuration** sur ces
mêmes distributions.

---

## 5. Concurrence

`Integration/ConcurrencyTests.cs` lance ses appels **réellement en parallèle**, libérés d'un seul
coup par un point de rendez-vous. Les sérialiser les rendrait verts sans rien prouver.

Ce n'est **pas** un banc de performance : « combien de requêtes par seconde » dépend de la machine,
ne se compare à rien, et n'a jamais rien empêché. La question posée est celle des invariants —
numéro de run unique, une seule fin par run, aucune réponse d'approbation perdue, un seul successeur
par jeton. Quatre défauts en sont sortis, listés dans [architecture.md](architecture.md) §6.

---

## 6. Non exécuté, raisonné seulement

Énoncé pour ne pas être pris pour de la couverture.

| Sujet | Pourquoi |
| --- | --- |
| **Podman** | La CI n'a que Docker. Le code ne branche sur aucun des deux, mais l'acceptation des mêmes options par une version donnée de Podman reste un raisonnement. |
| **Suffixes de montage `:z` / `:U`** | Demandent SELinux ou un Podman rootless réel. |
| **`Docker:HostWorkspacePath` avec un démon distant** | Demande deux machines. |
| **Egress `allowlist` via proxy** | Demande le proxy et un réseau `--internal`. |
| **Entrées/sorties S3 réelles** | Aucun magasin objet joignable ici ; un test contre un faux endpoint vérifierait le SDK AWS, pas notre code. Sont couverts : l'implémentation locale de bout en bout, la dérivation des clés, et la validation de configuration. |
| **Vérification de chaîne TLS SMTP** | Le certificat de test est auto-signé et sa validation est neutralisée. La négociation est réellement exercée ; la vérification de chaîne ne l'est pas. |
| **Tenue en charge** | Nombre de connexions, saturation du pool, dégradation à chaud : non mesurés. |
| **Seuil de limitation de débit** | Le limiteur est neutralisé dans la fabrique de test ; ce qui est vérifié est qu'une rafale ne se met pas en file, pas que le seuil est bien réglé. |

---

## 7. Conventions

- **Les noms de tests sont des phrases en anglais** décrivant le comportement attendu, pas des noms
  de méthodes : `Concurrent_run_creations_never_collide_on_a_run_number`.
- **Les commentaires expliquent le *pourquoi*, en français** — en particulier ce qu'un test attrape
  et ce qu'il n'attrape pas.
- **Un double ne remplace pas une dépendance dont le comportement est l'objet du test.** Les tests du
  répartiteur de courriels utilisent une file en mémoire (leur objet est le répartiteur) ; les tests
  d'envoi utilisent un vrai serveur SMTP (leur objet est le dialogue).
