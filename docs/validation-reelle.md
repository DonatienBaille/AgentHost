# Surfaces jamais confrontées au réel

Quatre parties de ce dépôt ont été écrites, raisonnées et testées **contre des doubles**, jamais
contre le vrai service qu'elles pilotent. Elles compilent, leurs tests passent, et leur
comportement en production reste une hypothèse.

Ce document existe parce que la distinction se perd vite : un test vert donne le même sentiment de
sécurité qu'une observation, et rien dans le code ne dit lequel des deux on a. C'est le volet **1.3
du lot 1** de [ROADMAP.md](../ROADMAP.md) — une session de validation, une fois, suffit à lever le
doute sur les quatre.

## Pourquoi ce n'est pas fait

Aucune des quatre n'est vérifiable dans l'environnement de développement utilisé jusqu'ici :

| Besoin | État constaté |
| --- | --- |
| Démon de conteneurs | Client Docker présent, **aucun démon** — `/var/run/docker.sock` n'existe pas |
| Podman | **Pas installé** |
| gVisor / Kata / sysbox | **Aucun runtime isolé installé** |
| Stockage S3 (MinIO) | Impossible sans démon de conteneurs |
| `api.pwnedpasswords.com` | **Bloqué par la politique réseau** (403 sur le CONNECT du proxy) |
| Relais SMTP | Aucun joignable |

En intégration continue, en revanche, le démon existe : `ContainerLifecycleTests` s'y exécute
réellement, et le job échoue si ce test se contente d'être *skipped*. Les surfaces ci-dessous n'ont
pas d'équivalent.

---

## 1. Podman

Le code ne branche jamais sur « Docker ou Podman » : les deux parlent la même API REST, seuls le
socket et les options de montage diffèrent. Ce sont donc des hypothèses sur le comportement de
Podman qu'il faut confirmer :

- l'acceptation de `no-new-privileges=true` — certaines versions veulent la forme nue
  `no-new-privileges`, d'où `Docker:SecurityOpt` qui permet de la surcharger ;
- `CapDrop: ["ALL"]` en majuscules — le chemin de compatibilité de Podman compare le mot-clé de
  façon sensible à la casse par endroits ;
- la traduction de `NanoCPUs` en limites cgroup, qui doit produire la même contrainte que sous
  Docker ;
- les suffixes de montage `:z` (SELinux) et `:U` (Podman rootless, qui reloge l'arborescence dans
  l'espace de noms utilisateur du conteneur) ;
- la détection automatique du socket, y compris `$XDG_RUNTIME_DIR/podman/podman.sock` en rootless.

**Comment valider** : une machine avec Podman, `podman system service` activé, puis la suite
d'intégration complète avec `Docker__Host` pointé sur son socket. `ContainerLifecycleTests` est le
test qui compte — il lance un vrai conteneur, livre un secret, reçoit le rappel de l'agent et
vérifie le nettoyage.

## 2. Entrées/sorties S3

Toute la couche réseau de `S3ArtifactStorage` : téléversement, téléchargement, URL présignées,
style de chemin (`ForcePathStyle`, indispensable pour MinIO et Ceph qui n'ont pas de DNS par
compartiment), et la chaîne d'identification ambiante (IRSA, profil d'instance, variables `AWS_*`).

**Comment valider** : un MinIO local, `Artifacts__Provider=s3` et `Artifacts__S3__ServiceUrl` vers
lui, puis un run qui produit un artefact et son téléchargement depuis l'IHM.

## 3. `api.pwnedpasswords.com` (mots de passe compromis)

Le contrôle est désactivé par défaut (`Auth:BreachedPasswordCheck:Enabled`) et **échoue ouvert** :
toute erreur réseau, expiration ou réponse non-200 traite le mot de passe comme non compromis et
journalise un avertissement, pour qu'un tiers injoignable ne puisse jamais bloquer une inscription.
Cette propriété-là est testée. Ce qui ne l'est pas :

- le format réel des réponses de l'API de plage k-anonymat ;
- l'en-tête `Add-Padding`, qui fait renvoyer des lignes factices pour empêcher l'analyse de taille ;
- les limites de débit réelles.

**Comment valider** : activer le contrôle depuis un réseau qui atteint l'API et tenter une
inscription avec un mot de passe notoirement compromis (`Password123!` par exemple) — l'inscription
doit être refusée.

## 4. Envoi SMTP

`SmtpEmailSender.SendAsync` n'a jamais été exécuté contre un socket. Ce qui est testé, c'est sa
sélection par configuration et sa construction ; la poignée de main STARTTLS, le mappage de
`EnableSsl`, l'authentification et le délai d'expiration reposent sur le contrat documenté du BCL.

Rappel : le TLS implicite du port 465 n'est **pas** géré, limite assumée de
`System.Net.Mail.SmtpClient`.

**Comment valider** : un MailHog ou un Mailpit local (`Email__Provider=smtp`,
`Email__Smtp__Host` vers lui, `Security=none`), puis une demande de réinitialisation de mot de
passe — le message doit apparaître, et le lien qu'il contient doit réinitialiser le mot de passe.

---

## 5. Runtime isolé (gVisor, Kata, sysbox)

`Docker:Runtime` est transmis tel quel au démon dans `HostConfig.Runtime`. C'est une ligne de
configuration, pas un comportement observé : aucun runtime isolé n'est installé ici.

**Comment valider** : installer gVisor, l'enregistrer auprès du démon
(`/etc/docker/daemon.json`, clé `runtimes`), poser `Docker__Runtime=runsc`, puis lancer un run et
vérifier depuis l'intérieur du conteneur que le noyau vu n'est pas celui de l'hôte
(`dmesg | head` sous gVisor annonce son propre noyau).

---

## Ce qu'il faut retenir

Aucune de ces cinq surfaces n'est cassée à notre connaissance, et aucune n'est prouvée. La
différence compte surtout pour Podman et S3 : ce sont des chemins de production entiers dont le
premier exercice réel serait un incident.
