import { defineConfig } from 'vitest/config';

/**
 * Surcharge de la configuration Vitest fournie par `@angular/build:unit-test`.
 *
 * Ce builder pose `isolate: false` par défaut, explicitement « pour rester proche de l'expérience
 * Karma/Jasmine » (node_modules/@angular/build/.../vitest/plugins.js). Tous les fichiers de test
 * partagent alors un seul registre de modules, ce qui rend `vi.mock()` dépendant de l'ordre de
 * chargement : `signalr.service.spec.ts` simule `@microsoft/signalr`, mais plusieurs specs de
 * composants importent `SignalRService` — donc transitivement le vrai module — et le premier
 * chargement gagne. La suite passait environ une fois sur deux, avec 27 échecs qui tentaient de
 * négocier une vraie connexion WebSocket.
 *
 * Un registre de modules par fichier supprime la course. C'est le comportement par défaut de
 * Vitest ; c'est le builder qui s'en écarte.
 */
export default defineConfig({
  test: {
    isolate: true,
  },
});
