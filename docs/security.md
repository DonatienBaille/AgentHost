# Sécurité

Ce que la plateforme garantit, ce qu'elle **ne** garantit pas, et pourquoi. Pour signaler une
vulnérabilité, voir [SECURITY.md](../SECURITY.md).

---

## 1. Le modèle de menace

Agent Host exécute **du code tiers** pour le compte de **plusieurs organisations** sur une même
installation. Deux adversaires en découlent, et ils ne se défendent pas de la même façon.

| Adversaire | Ce qu'il cherche | Défense principale |
| --- | --- | --- |
| **L'agent** — le code exécuté, hostile par hypothèse | Sortir du conteneur, lire les secrets d'autrui, atteindre le réseau | Durcissement du conteneur, secrets par fichier, jeton de run scopé |
| **Le locataire** — un compte légitime d'une autre organisation | Lire ou modifier les données d'un voisin | Isolation portée par le SQL, 404 systématique |

Un troisième adversaire — celui qui atteint le socket du runtime — n'est pas contenu par cette
architecture, et c'est dit en §6.

---

## 2. Isolation entre locataires

**La règle : l'isolation est dans le SQL, jamais dans une vérification que quelqu'un peut oublier
d'écrire.** Chaque lecture d'un endpoint porte `org_id` dans sa clause `WHERE`.

**Une ressource d'un autre locataire répond 404, jamais 403.** Un 403 confirmerait son existence, et
l'énumération d'identifiants deviendrait une source d'information. Les deux réponses ont l'air
équivalentes ; elles ne le sont pas.

Les tables sans `org_id` sont scopées **par jointure** vers le run ou le projet qui les porte, et le
chemin de jointure est le même dans les lectures et dans la purge — pour que ce que la purge efface
soit exactement ce que l'application sait atteindre.

---

## 3. Secrets

- **Chiffrés en AES-256-GCM** au repos (`secrets.encrypted_value`, `user_mfa.secret_encrypted`).
- **Jamais rendus en clair par l'API.** Aucun endpoint ne restitue la valeur d'un secret.
- **Livrés aux agents par fichier** : `/run/secrets/<NOM>` en 0400, répertoire en 0700, effacé à la
  sortie du conteneur. Jamais par variable d'environnement — `docker inspect` et `/proc/1/environ`
  les exposeraient à tout processus du conteneur.
- **Rotatifs** : `Secrets:PreviousEncryptionKeys` accepte les anciennes clés en lecture, et
  `--rekey-secrets` réécrit l'existant. La commande est idempotente et **ne détruit jamais une valeur
  qu'elle n'a pas su déchiffrer**.

**La clé ne se sauvegarde pas au même endroit que les dumps.** Les stocker ensemble revient à ne pas
chiffrer. Et l'inverse est tout aussi fatal : un dump restauré sans sa clé ne produit **aucune
erreur** — les secrets échouent silencieusement à la première utilisation, et les comptes à second
facteur sont verrouillés.

---

## 4. Identité et jetons

- **Jeton d'accès** JWT court (15 min par défaut), **stateless** : il reste valide jusqu'à son
  expiration, y compris après déconnexion. La durée courte est l'atténuation ; le jeton de
  rafraîchissement est la moitié révocable.
- **Tous les jetons ne sont stockés que par empreinte SHA-256** — rafraîchissement, réinitialisation,
  invitation. Le dépôt ne peut pas restituer un jeton, seulement en vérifier un.
- **La rotation du rafraîchissement révoque avant d'émettre.** Dans l'autre ordre, deux
  rafraîchissements simultanés obtenaient chacun une paire valide : deux familles vivantes issues
  d'une seule, et la détection de vol — qui repose entièrement sur le rejeu d'un jeton révoqué —
  rendue inopérante.
- **Rejouer un jeton révoqué tue toute la famille.** C'est la signature classique d'un vol : le
  client légitime a tourné et le voleur rejoue l'ancienne valeur, ou l'inverse.
- **MFA TOTP** disponible ; le mot de passe seul ne donne alors ni jeton d'accès ni jeton de
  rafraîchissement, seulement un défi court à échanger.
- **Mots de passe compromis** : vérification k-anonyme facultative contre Have I Been Pwned, avec un
  délai court — le service indisponible ne doit pas bloquer une inscription.

---

## 5. Traçabilité

`audit_log` est **append-only** depuis la migration `0008` : trois triggers refusent `UPDATE`,
`DELETE` et `TRUNCATE`. Le `TRUNCATE` a son propre trigger d'instruction, parce qu'un trigger de
niveau ligne ne le voit jamais — c'est précisément le raccourci qu'emprunterait quelqu'un voulant
vider le journal d'un coup. Une entrée erronée **ne se corrige pas** : on en ajoute une qui décrit la
correction.

**Ce que cela ne protège pas**, et il faut le savoir :

- un rôle capable de `ALTER TABLE … DISABLE TRIGGER` contourne tout, et **l'application est
  aujourd'hui propriétaire de la table**. Un WORM réel demande une séparation de rôles qui relève du
  déploiement. Le trigger transforme une modification silencieuse en acte délibéré, ce qui est déjà
  l'essentiel ;
- une restauration de sauvegarde, qui recrée la table à partir du dump.

**La seule dérogation du dépôt** est la purge RGPD (`--purge-org`), hors ligne, par organisation,
avec le trigger rétabli même en cas d'échec — et un test qui le vérifie sur une *autre* organisation.

---

## 6. Risques résiduels

Énoncés ici pour ne pas être découverts en production.

**Le socket du runtime de conteneurs est un pouvoir de root sur l'hôte.** Y compris un socket Podman
*rootful*. Le proxy filtrant (`docker-socket-proxy`) et le backend non-root sont des atténuations,
pas des éliminations : « créer et démarrer un conteneur » suffit encore à s'évader — rien n'empêche
une requête demandant un conteneur privilégié ou un bind mount de `/`. Une isolation réelle demande
un démon dédié par locataire, une VM, ou un runtime type gVisor/Kata (`Docker:Runtime`).

**`runc` partage le noyau de l'hôte.** Pour une plateforme qui exécute du code tiers, c'est
l'atténuation structurelle manquante ; `Docker:Runtime` existe pour cela, et son défaut vide est un
compromis d'installabilité, pas une recommandation.

**`network: allowlist` ne contraint que les clients coopératifs.** Un processus qui ouvre une socket
TCP directe ignore `HTTP_PROXY`. Le confinement réel demande un réseau `--internal` où seul le proxy
est joignable (`Docker:AllowlistNetwork`).

**Le TLS SMTP n'est vérifié qu'en négociation.** Les tests utilisent un certificat auto-signé dont la
validation est neutralisée : la négociation est réellement exercée, la vérification de chaîne ne l'est
pas.

**Une sauvegarde antérieure ramène les données effacées.** La purge RGPD n'affecte pas les dumps
existants — c'est une conséquence du mécanisme de sauvegarde, à prendre en compte dans la réponse à
une demande d'effacement.

---

## 7. Ce qui est prouvé plutôt que déclaré

Trois garanties de cette page sont assertées **sur un vrai conteneur**, pas seulement documentées :

- les flags de durcissement, tels que le démon les a enregistrés ;
- le secret lisible dans le conteneur, au bon contenu **et absent de son environnement** ;
- le répertoire de secrets en clair effacé après la sortie.

Voir [testing.md](testing.md).
