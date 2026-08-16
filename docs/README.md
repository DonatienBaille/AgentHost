# Documentation

## Par où commencer

| Vous voulez… | Lisez |
| --- | --- |
| Comprendre comment c'est fait, et pourquoi | [architecture.md](architecture.md) |
| Déployer et régler | [configuration.md](configuration.md) |
| Exploiter au quotidien | [operations.md](operations.md) |
| Écrire un agent | [agent-protocol.md](agent-protocol.md) |
| Contribuer au code | [../CONTRIBUTING.md](../CONTRIBUTING.md) |

## Toute la documentation

### Conception

- **[architecture.md](architecture.md)** — Découpage, cycle de vie d'un run, isolation
  multi-locataire, invariants de concurrence, et ce que l'architecture ne fait pas.
- **[security.md](security.md)** — Modèle de menace, garanties, et risques résiduels.
- **[runner.md](runner.md)** — Le tier d'exécution, et ce qui débloque plusieurs répliques.

### Exploitation

- **[configuration.md](configuration.md)** — Toutes les clés, leurs défauts, leurs effets.
- **[operations.md](operations.md)** — Sauvegarde, restauration, PITR, rotation de la clé de
  chiffrement, immuabilité du journal d'audit, purge RGPD.
- **[containers.md](containers.md)** — Docker/Podman, détection de socket, bind mounts,
  durcissement, confinement réseau.
- **[observabilite-operationnelle.md](observabilite-operationnelle.md)** — Métriques, règles
  d'alerte, tableaux de bord.

### Fonctionnalités

- **[agent-protocol.md](agent-protocol.md)** — Le contrat entre un agent et l'hôte.
- **[auth.md](auth.md)** — Authentification, MFA, invitations, réinitialisation de mot de passe.
- **[triggers.md](triggers.md)** — Webhooks entrants et planification cron.
- **[manifest-editor.md](manifest-editor.md)** — Édition d'agents en double mode YAML / IHM.

### Qualité

- **[testing.md](testing.md)** — Ce qui est exécuté, ce qui est raisonné, et la méthode de
  vérification par sabotage.
- **[validation-reelle.md](validation-reelle.md)** — Ce qui a été confronté à une vraie dépendance,
  et ce qui reste à confronter.

---

## Une convention de ces documents

Chaque page dit **ce qu'elle ne prouve pas**. Une documentation qui tait ses angles morts est plus
dangereuse qu'une documentation absente : elle transforme une incertitude connue en certitude
fausse. Les sections « ce qui n'est pas vérifié », « risques résiduels » et « ce que ça ne couvre
pas » sont donc à lire comme le reste, pas comme des réserves de forme.
