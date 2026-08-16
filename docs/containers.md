# Runtime de conteneurs : Docker ou Podman

Détection du socket, chemins de bind mount, durcissement, et confinement réseau. Les réglages sont
listés dans [configuration.md](configuration.md) ; ce document explique **pourquoi** ils existent.

---

## 1. Un seul chemin de code pour les deux moteurs

Podman expose l'API REST de Docker, et l'orchestrateur n'émet que des appels servis à l'identique par
les deux : pull d'image, create/start/wait/logs/remove, listing par étiquette. **Il n'y a donc
volontairement aucun réglage « runtime » à positionner.** Ce qui diffère réellement se limite à deux
choses, et les deux sont configurables.

## 2. L'emplacement du socket

`Docker:Host` reste prioritaire. **Laissé vide, le socket est détecté** dans cet ordre — les chemins
rootless d'abord dès que le processus ne tourne pas en uid 0, parce que les sockets rootful sont
typiquement en `0660 root:root` :

| Chemin | Runtime |
| --- | --- |
| `/var/run/docker.sock` | Docker rootful |
| `/run/podman/podman.sock` | Podman rootful (`systemctl enable --now podman.socket`) |
| `$XDG_RUNTIME_DIR/podman/podman.sock` (à défaut `/run/user/<uid>/podman/podman.sock`) | Podman rootless (`podman system service --time=0`) |
| `$XDG_RUNTIME_DIR/docker.sock` | Docker rootless |

Sans rien trouver, la valeur historique `unix:///var/run/docker.sock` est conservée. Le runtime
déduit du chemin est **journalisé au démarrage** mais n'est utilisé par aucune décision.

`tecnativa/docker-socket-proxy` filtre un socket Podman exactement comme un socket Docker : c'est un
filtre HTTP devant un socket qui parle l'API Docker. `docker-compose.podman.yml` s'en sert tel quel.

```bash
# Rootful (recommandé — sémantique d'uid identique à Docker)
sudo systemctl enable --now podman.socket
docker compose -f docker-compose.yml -f docker-compose.podman.yml up --build
```

## 3. La propriété des fichiers montés (rootless)

En **rootless**, le backend et les conteneurs d'agents ne voient pas les mêmes uid pour un même
fichier. `chown 64198` sur l'hôte n'est pas ce que voit le conteneur — il faut
`podman unshare chown -R 64198:64198 runs artifacts`, ou le suffixe de montage `:U` — et un agent
tournant en uid 0 dans son propre namespace retombe sur *votre* uid hôte, pas sur 64198, donc il ne
peut pas lire des fichiers de secrets en 0400.

`Docker:BindMountOptions` (liste séparée par des virgules, ajoutée à **tous** les binds des
conteneurs d'agents) traite ces cas : `z` pour le relabel SELinux (famille Fedora/RHEL), `U` pour que
Podman chowne l'arborescence montée vers l'utilisateur du conteneur.

**Le compromis, sans enjolivement** : `U` réécrit la propriété du répertoire que le backend doit
ensuite supprimer en fin de run (le nettoyage de `/run/secrets`). Si des erreurs de suppression de
secrets apparaissent dans les journaux en rootless, c'est cela — la réponse est le service
**rootful**, ou faire tourner le backend lui-même avec `--userns=keep-id` pour que les deux côtés
partagent un uid. C'est une propriété des user namespaces rootless, pas quelque chose qu'Agent Host
peut masquer.

## 4. Ce qui a été corrigé pour Podman

- `HostConfig.CPUCount` est un champ **Windows** : Docker Linux **et** Podman l'ignorent, donc
  `spec.runtime.cpu` ne limitait strictement rien. Remplacé par `NanoCPUs` (cœurs × 1e9), le
  mécanisme Linux portable.
- `CapDrop` utilise désormais `ALL` (l'orthographe normalisée par les deux moteurs) au lieu de `all`.
- `Docker:SecurityOpt` est configurable (défaut `no-new-privileges=true`) : l'orthographe acceptée a
  varié selon les moteurs et les versions ; un Podman ancien qui refuse cette forme se règle sans
  toucher au code.

---

## 5. Chemins de bind mount (backend conteneurisé)

La source d'un bind mount est résolue par le **démon**, jamais par le processus qui émet l'appel API.
Quand le backend tourne lui-même dans un conteneur, les deux ne désignent pas la même chose : le
backend écrit dans `/var/agenthost/runs` (son propre point de montage), le démon doit monter
`/chemin/hôte/runs`.

Envoyer le premier au démon ne produit **aucune erreur** — il crée un répertoire vide — donc l'agent
démarre avec un `/workspace` vide et surtout un `/run/secrets` vide. C'est le genre de panne qui ne
se voit pas dans les journaux.

| Clé | Signification |
| --- | --- |
| `Docker:WorkspacePath` | Chemin que **ce processus** lit et écrit. |
| `Docker:HostWorkspacePath` | Chemin auquel **le démon** voit le même répertoire. Vide = identique (bare-metal). |

Un chemin situé hors de `Docker:WorkspacePath` est **refusé** plutôt que deviné : un bind mount
erroné est silencieux, une exception ne l'est pas.

`docker-compose.yml` positionne `Docker__HostWorkspacePath: ${PWD}/runs`, ce qui rend la stack compose
réellement capable de lancer des agents. Surcharger avec `AGENTHOST_HOST_RUNS_PATH` si le démon est
distant ou voit le dépôt ailleurs.

**En Kubernetes** : `docker.hostWorkspacePath` dans le chart. Ce réglage n'a de sens que si le
**nœud** possède réellement ce chemin — c'est-à-dire avec un volume `runs` de type hostPath monté au
même chemin absolu. Avec un PVC réseau, le démon du nœud n'a aucun chemin vers ce système de fichiers
et les bind mounts d'agents ne peuvent pas fonctionner. C'est une conséquence de confier un chemin
d'un autre système de fichiers au démon d'un nœud, pas quelque chose que le chart peut contourner —
la réponse est le [tier runner](runner.md).

---

## 6. Isolation des agents (spec §13.2)

Chaque run s'exécute dans un conteneur avec `no-new-privileges`, toutes les capabilities supprimées,
mémoire plafonnée sans swap, **CPU réellement plafonné** (`NanoCPUs`), **rootfs en lecture seule**
(`/workspace` bind-monté en écriture + tmpfs sur `/tmp`), et DNS configurable.

Un agent qui a réellement besoin d'un rootfs inscriptible doit le demander explicitement :

```yaml
spec:
  permissions:
    writableRootfs: true
```

**Secrets** : livrés **uniquement** sous forme de fichiers `/run/secrets/<NOM>` (0400, répertoire
0700), jamais en variables d'environnement — `docker inspect` et `/proc/1/environ` les exposeraient.
Le répertoire est supprimé dès la sortie du conteneur ; un balayage périodique
(`Retention:SecretsGraceMinutes`) nettoie ceux qu'un crash aurait laissés.

Ces trois propriétés — flags acceptés par le démon, secret lisible dans le conteneur au bon contenu
et absent de son environnement, répertoire en clair effacé après la sortie — ne sont pas seulement
documentées : elles sont **assertées sur un vrai conteneur** (voir [testing.md](testing.md)).

---

## 7. `network: allowlist` — ce qui est réellement appliqué

`permissions.network` accepte `none`, `allowlist` et `full`.

| Valeur | Effet |
| --- | --- |
| `none` | `NetworkMode=none` : aucune interface réseau. |
| `full` | `NetworkMode=bridge` : egress non filtré. |
| `allowlist` | Réseau + `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` pointant sur `Docker:EgressProxy`. Les hôtes de `permissions.networkAllowlist` sont transmis à la couche proxy via l'étiquette `agenthost.network_allowlist`. |

```yaml
spec:
  permissions:
    network: allowlist
    networkAllowlist: ["api.github.com", "registry.npmjs.org"]
```

**Ce qui est garanti** : sans `Docker:EgressProxy` configuré (ou avec une allowlist vide), le run
démarre **sans réseau du tout** — fail closed, jamais de dégradation silencieuse vers `full`.

**Ce qui ne l'est pas** : un proxy HTTP ne contraint que les clients qui le respectent. Un processus
qui ouvre une socket TCP directe vers une IP ignore `HTTP_PROXY`. Pour une application réelle, il
faut que le réseau du conteneur ne route nulle part ailleurs que vers le proxy : créer un réseau
`--internal` où seul le proxy est joignable et le désigner via `Docker:AllowlistNetwork`, ou filtrer
le sous-réseau bridge en amont. L'orchestrateur ne peut pas le faire par conteneur sans sidecar
dédié.

---

## 8. Accès au socket du runtime

L'API pilote un démon de conteneurs : **quiconque atteint ce socket est root sur l'hôte** — y compris
un socket Podman *rootful*, qui donne exactement les mêmes pouvoirs. Deux mesures :

1. Le conteneur backend tourne en non-root (uid 64198).
2. `docker-compose.yml` ne bind-monte plus `/var/run/docker.sock` dans le backend. Un
   `tecnativa/docker-socket-proxy` s'intercale et ne laisse passer que les endpoints
   containers/images réellement utilisés ; `Docker__Host` pointe dessus. Le même proxy fronte un
   socket Podman sans modification.

**Risque résiduel, honnêtement** : c'est une atténuation, pas une élimination. « Créer et démarrer un
conteneur » suffit encore à s'évader vers l'hôte — rien n'empêche une requête demandant un conteneur
privilégié ou un bind mount de `/`. Une isolation réelle demande un démon dédié par locataire, une
VM, ou un runtime type gVisor/Kata (`Docker:Runtime`). Dans le chart Helm, `docker.mountSocket` monte
le socket du nœud par défaut ; préférer un proxy filtrant déployé séparément et `docker.host`.
