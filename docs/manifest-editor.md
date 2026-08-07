# L'éditeur de manifeste — deux modes, une seule source de vérité

## Le problème

Définir un agent imposait d'écrire un manifeste YAML à la main dans un `<textarea>`. C'est puissant
et versionnable, mais cela réserve la création d'agents à un public technique, alors que la
plateforme vise aussi des utilisateurs métier.

Ajouter un formulaire est facile. Ce qui est difficile, c'est de le faire **cohabiter** avec le
YAML : deux représentations d'une même chose se réconcilient mal, et la mauvaise réponse — stocker
le formulaire à côté du YAML — crée deux vérités qui divergent au premier import.

## Le partage des rôles

Le YAML reste la seule chose persistée et versionnée. Le mode formulaire est une projection, qui
n'existe qu'entre deux conversions et ne survit pas à la fermeture de l'éditeur.

|                | Où                                                        | Pourquoi là                                                                                                    |
| -------------- | --------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| YAML → modèle  | serveur, `POST /api/agents/validate-manifest`              | Le navigateur n'embarque aucun parseur : deux lecteurs voudraient dire deux vérités possibles sur un même fichier. |
| modèle → YAML  | navigateur, `core/manifest/yaml-emitter.ts`                | Un aperçu en direct ne peut pas faire un aller-retour réseau par frappe, et le sous-ensemble à **écrire** est clos. |

L'endpoint de validation fait tourner le vrai `IAgentManifestParser` — celui qui exécutera l'agent.
Il répond `200` dans les deux cas : « ça ne parse pas encore » est la réponse attendue au milieu
d'une frappe, pas une erreur de requête. Quand YamlDotNet sait où l'erreur s'est produite, la ligne
est renvoyée ; ses erreurs à lui (document vide, `spec` absente, `metadata.name` absent) n'ont pas
de position et sont rendues sans ligne plutôt qu'avec une ligne inventée.

## Pourquoi le document brut voyage à côté du manifeste typé

La réponse de validation porte trois choses : le manifeste **typé** (défauts appliqués), les
extensions de permissions (`networkAllowlist`, `writableRootfs`, qui ne sont pas dans
`Domain/AgentManifest`), et le document YAML **brut** projeté en JSON.

Le manifeste typé ne suffit pas, pour deux raisons vérifiées et non supposées :

1. le parseur applique `IgnoreUnmatchedProperties` — il ne peut donc pas dire ce que le manifeste
   contient au-delà de ce qu'il modélise, et c'est exactement la question que pose le mode UI ;
2. il désérialise `spec.inputs` et `spec.outputs` en `object`, ce que YamlDotNet remplit de
   **chaînes** : `default: 200` en ressort `"200"`. Reconstruire le formulaire là-dessus le ferait
   réécrire `default: "200"` — un aller-retour qui n'est pas sans perte.

`Services/YamlDocumentProjection` produit donc un arbre JSON fidèle, en résolvant les scalaires
plains avec le schéma « core » de YAML 1.2. Ce n'est pas un second parseur : il ne valide rien, ne
défaute rien, ne réordonne rien.

## Ce que le formulaire sait éditer

Le constructeur de champs couvre exactement le sous-ensemble de JSON Schema que
`components/new-run-form` sait **rendre** à l'exécution : un objet, des propriétés de type
`string` / `boolean` / `integer` / `number`, avec `title`, `description`, `default`, `enum`, et une
liste `required`. Le choix n'est pas arbitraire : un schéma hors de ce sous-ensemble décrit un
formulaire de lancement que la plateforme ne construit pas non plus. Le constructeur ne prétend pas
couvrir plus que le reste du produit.

## La politique des constructions non représentables

**Préserver et nommer. Jamais tronquer, et refuser seulement quand il n'y a rien à projeter.**

| Situation                                              | Ce qui se passe                                                                             |
| ------------------------------------------------------ | -------------------------------------------------------------------------------------------- |
| Le YAML ne parse pas                                    | La bascule est refusée, on reste en YAML avec l'erreur sur sa ligne.                          |
| Une clé que le formulaire ne modélise pas (`spec.sidecars`) | Conservée telle quelle, réémise à sa place, nommée dans le bandeau.                       |
| Un schéma hors du sous-ensemble éditable                | Gardé **entier**, rendu non éditable, avec le chemin précis de ce qui l'a disqualifié.        |
| Des commentaires YAML                                   | Signalés avec leurs numéros de ligne : c'est la seule perte réelle (voir plus bas).           |

Trois arbitrages méritent d'être défendus.

**Pourquoi préserver plutôt que refuser.** Refuser la bascule punirait l'utilisateur pour la limite
de notre formulaire. Il a écrit un manifeste valide ; qu'une partie sorte de ce que l'IHM sait
éditer ne justifie pas de lui fermer la porte des huit sections qui, elles, sont éditables.

**Pourquoi l'opacité vaut pour le schéma entier et non propriété par propriété.** Un schéma JSON est
une unité de sens. Éditer trois propriétés au formulaire pendant qu'un `oneOf` invisible en contraint
une quatrième, c'est fabriquer des manifestes contradictoires sans que personne ne puisse voir
pourquoi. Le schéma entier passe donc en lecture seule, et le bandeau nomme la propriété fautive.

**Pourquoi le YAML n'est réécrit qu'au premier vrai changement.** Ouvrir l'onglet formulaire pour
regarder ne reformate le fichier de personne. L'aperçu montre en permanence ce que le formulaire
écrirait : la différence est visible **avant** d'être appliquée, jamais après.

## Ce qui se perd quand même, et ce qui est dit

- **Les commentaires.** Ils n'existent dans aucun document analysé — ni pour le parseur, ni pour la
  projection JSON. Dès qu'une modification au formulaire réécrit le fichier, ils disparaissent. Le
  bandeau les signale avec leurs numéros de ligne. Le repérage se fait sur le texte source, avec une
  heuristique prudente : une ligne entièrement commentée, ou un `#` précédé d'un blanc sur une ligne
  sans apostrophe ni guillemet. Un `#` dans une chaîne non citée (`description: rapport #12`) est
  donc compté à tort. Signaler un commentaire qui n'existe pas fait regarder deux fois ; en manquer
  un fait perdre du texte sans le dire.
- **Les ancres et alias YAML** sont développés par la projection. Un fragment préservé qui en
  contenait est réémis développé : même sens, texte différent. Non signalé aujourd'hui.
- **L'ordre des clés** devient l'ordre canonique dès la première modification au formulaire.
- **Les clés dupliquées** : la dernière gagne, comme dans le parseur.

## Comment c'est vérifié

L'aller-retour sans perte est testé sur un manifeste non trivial — six entrées de quatre types,
sortie, allowlist réseau, secrets, docker, rootfs inscriptible, fournisseur externe avec
configuration, porte d'approbation à deux voix.

Le point intéressant est le **contrat entre les deux langages**. Le test frontend compare le YAML
émis à un texte attendu, puis le relit avec une fabrique de test qui imite le parseur — et une
imitation dérive en silence. Le même texte, à l'octet près, est donc rejoué contre le vrai parseur
par `backend/tests/.../CanonicalManifestContractTests.cs`, qui vérifie que le serveur en tire bien ce
que le frontend suppose. Si l'une des deux moitiés bouge, l'une des deux suites tombe.

## Ce qui n'est pas fait

- Pas de coloration syntaxique dans le mode YAML : c'est un `<textarea>`, sans dépendance
  d'éditeur.
- Pas d'autocomplétion ni de repli hors ligne : la validation est un aller-retour serveur, avec un
  anti-rebond de 400 ms. Serveur injoignable ⇒ « validation indisponible », pas « manifeste
  invalide ».
- Le constructeur ne crée pas de schémas imbriqués, de tableaux ni de compositions ; il les
  préserve.
- Les ancres YAML ne sont pas signalées comme perdues alors qu'elles sont développées.
