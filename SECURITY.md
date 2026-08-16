# Politique de sécurité

## Signaler une vulnérabilité

**N'ouvrez pas d'issue publique** pour une faille de sécurité : une issue est indexée avant d'être
corrigée.

Utilisez l'onglet **Security → Report a vulnerability** du dépôt GitHub (avis de sécurité privé), ou
à défaut contactez directement le propriétaire du dépôt.

Un signalement utile contient : la version ou le commit concerné, les étapes de reproduction,
l'impact constaté, et — si vous en avez une — la configuration dans laquelle la faille se manifeste.

Merci de laisser un délai raisonnable pour la correction avant toute divulgation publique.

## Versions concernées

Le projet n'a pas encore de version publiée. Seule la branche par défaut est maintenue.

## Ce qui n'est pas une vulnérabilité

Les limites suivantes sont **connues, documentées et assumées**. Les signaler ne déclenchera pas de
correctif ; elles sont détaillées dans [docs/security.md](docs/security.md) §6.

- **L'accès au socket du runtime de conteneurs donne root sur l'hôte.** Le proxy filtrant est une
  atténuation explicite, pas une élimination.
- **`network: allowlist` ne contraint que les clients qui respectent `HTTP_PROXY`.** Le confinement
  réel demande un réseau `--internal`.
- **`audit_log` est contournable par le propriétaire de la table**, et l'application l'est
  aujourd'hui. Un WORM réel demande une séparation de rôles qui relève du déploiement.
- **Une sauvegarde antérieure ramène les données effacées par la purge RGPD.**
- **Le jeton d'accès reste valide jusqu'à son expiration après déconnexion** — il n'y a pas de liste
  de révocation ; la durée courte est l'atténuation, et le jeton de rafraîchissement est la moitié
  révocable.

En revanche, tout ce qui **contredit une garantie énoncée** dans [docs/security.md](docs/security.md)
— une fuite entre locataires, un secret rendu en clair par l'API, un secret exposé dans
l'environnement d'un conteneur, un jeton stocké autrement que par empreinte — nous intéresse
directement.
