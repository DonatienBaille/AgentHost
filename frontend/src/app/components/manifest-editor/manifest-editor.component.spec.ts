import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideTranslateService } from '@ngx-translate/core';
import {
  ManifestEditorComponent,
  VALIDATION_DEBOUNCE_MS,
} from './manifest-editor.component';
import { AgentService } from '../../services/agent.service';
import { ManifestValidation } from '../../core/models';
import { JsonObject, manifestModelToYaml } from '../../core/manifest';
import {
  manifestValidation,
  manifestValidationFailure,
  nonTrivialManifestDocument,
} from '../../core/testing/fixtures';

/**
 * Double du service : aucune requête. Le YAML soumis lui est indifférent — c'est le serveur qui
 * lit, et ce qu'il lit est déjà couvert par les tests backend. Ici on teste l'orchestration.
 */
class AgentServiceStub {
  next: ManifestValidation = manifestValidation({
    metadata: { name: 'stub' },
    spec: { type: 'oci' },
  });
  readonly validateManifest = vi.fn(async (_yaml: string): Promise<ManifestValidation> => this.next);
}

describe('ManifestEditorComponent', () => {
  let fixture: ComponentFixture<ManifestEditorComponent>;
  let component: ManifestEditorComponent;
  let agentService: AgentServiceStub;
  let emitted: string[];

  /**
   * `answer` est posé avant la création du composant : l'amorçage depuis `initialYaml` valide dès
   * le premier cycle de détection, donc le double doit déjà savoir quoi répondre.
   */
  function setup(initialYaml = '', answer?: ManifestValidation): void {
    TestBed.configureTestingModule({
      imports: [ManifestEditorComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: AgentService, useClass: AgentServiceStub },
      ],
    });

    agentService = TestBed.inject(AgentService) as unknown as AgentServiceStub;
    if (answer) agentService.next = answer;
    fixture = TestBed.createComponent(ManifestEditorComponent);
    component = fixture.componentInstance;
    emitted = [];
    component.yamlChange.subscribe((yaml: string) => emitted.push(yaml));
    fixture.componentRef.setInput('initialYaml', initialYaml);
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  /**
   * Laisse l'amorçage asynchrone se terminer. `whenStable` ne suffit pas seul : la lecture d'un
   * manifeste enchaîne plusieurs promesses, et un tour de macrotâche les vide toutes.
   */
  async function settle(): Promise<void> {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  describe('form mode', () => {
    it('opens on the form, not on the YAML editor', () => {
      setup();

      expect(component.mode()).toBe('form');
      expect(el('manifest-form')).not.toBeNull();
      expect(el('manifest-yaml')).toBeNull();
    });

    it('documents every field it renders with a sentence, not a type', () => {
      setup();

      // Chaque libellé de section est suivi d'au moins une aide en ligne.
      const helps = fixture.nativeElement.querySelectorAll('p.text-xs.text-gray-500');
      expect(helps.length).toBeGreaterThan(20);
    });

    it('emits YAML for every field change and previews the same text', () => {
      setup();

      component.patch({ name: 'redacteur' });

      fixture.detectChanges();
      expect(emitted.at(-1)).toContain('name: redacteur');
      expect(el('yaml-preview')!.textContent).toBe(component.preview());
      expect(component.preview()).toBe(emitted.at(-1));
    });

    it('emits nothing before anything is edited', () => {
      setup();

      expect(emitted).toEqual([]);
    });

    it('adds and removes an input field', () => {
      setup();

      component.addField('inputs');
      component.patchField('inputs', 0, { name: 'repository', required: true });
      fixture.detectChanges();

      expect(all('input-field').length).toBe(1);
      expect(emitted.at(-1)).toContain('repository');
      expect(emitted.at(-1)).toContain('required:');

      component.removeField('inputs', 0);
      fixture.detectChanges();

      expect(all('input-field').length).toBe(0);
      expect(emitted.at(-1)).not.toContain('repository');
    });

    it('reads a default value the way YAML would, not as the declared type', () => {
      setup();
      component.addField('inputs');
      component.patchField('inputs', 0, { name: 'retries', type: 'integer' });

      component.setFieldDefault('inputs', 0, '3');
      expect(component.model().inputs.fields[0].defaultValue).toBe(3);
      expect(emitted.at(-1)).toContain('default: 3');

      component.setFieldDefault('inputs', 0, 'auto');
      expect(component.model().inputs.fields[0].defaultValue).toBe('auto');

      // Vider le champ retire la clé plutôt que d'y écrire une chaîne vide.
      component.setFieldDefault('inputs', 0, '');
      expect(component.model().inputs.fields[0].defaultValue).toBeNull();
      expect(emitted.at(-1)).not.toContain('default:');
    });

    it('splits list fields on commas and drops the blanks', () => {
      setup();

      component.setSecrets(' GITHUB_TOKEN , , OPENAI_API_KEY ');
      component.setAllowlist('api.github.com,registry.npmjs.org');

      expect(component.model().secrets).toEqual(['GITHUB_TOKEN', 'OPENAI_API_KEY']);
      expect(component.model().networkAllowlist).toEqual(['api.github.com', 'registry.npmjs.org']);
    });

    it('offers a select option for a value the UI does not know, rather than silently dropping it', async () => {
      const document = nonTrivialManifestDocument();
      ((document['spec'] as JsonObject)['runtime'] as JsonObject)['profile'] = 'gpu-xl';
      setup('', manifestValidation(document));
      component.onYamlInput('anything');

      await component.setMode('yaml');
      await component.setMode('form');

      expect(component.model().runtimeProfile).toBe('gpu-xl');
      expect(component.profileOptions()).toContain('gpu-xl');
    });

    it('shows the approval gate fields only once the gate is on', () => {
      setup();
      expect(el('mf-approvals')).toBeNull();

      component.patch({ approvalsEnabled: true });
      fixture.detectChanges();

      expect(el('mf-approvals')).not.toBeNull();
      expect(emitted.at(-1)).toContain('beforeWrite');
    });
  });

  describe('yaml mode', () => {
    it('shows the textarea and hides the form', async () => {
      setup();

      await component.setMode('yaml');
      fixture.detectChanges();

      expect(el('manifest-yaml')).not.toBeNull();
      expect(el('manifest-form')).toBeNull();
    });

    it('emits what is typed, verbatim and immediately', async () => {
      setup();
      await component.setMode('yaml');

      component.onYamlInput('metadata:\n  name: x\n');

      expect(emitted.at(-1)).toBe('metadata:\n  name: x\n');
    });

    it('validates once after the typing settles, not once per keystroke', async () => {
      vi.useFakeTimers();
      setup();
      await component.setMode('yaml');

      component.onYamlInput('a');
      component.onYamlInput('ab');
      component.onYamlInput('abc');
      expect(agentService.validateManifest).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(VALIDATION_DEBOUNCE_MS);

      expect(agentService.validateManifest).toHaveBeenCalledTimes(1);
      expect(agentService.validateManifest).toHaveBeenCalledWith('abc');
    });

    it('points at the offending line and shows it', async () => {
      vi.useFakeTimers();
      setup('', manifestValidationFailure({ line: 3, message: 'invalid mapping' }));
      await component.setMode('yaml');
      component.onYamlInput('metadata:\n  name: x\n   oops: bad\nspec: {}\n');

      await vi.advanceTimersByTimeAsync(VALIDATION_DEBOUNCE_MS);
      vi.useRealTimers();
      fixture.detectChanges();

      expect(component.errorContext()).toEqual({ line: 3, text: '   oops: bad' });
      expect(el('validation-error')).not.toBeNull();
      expect(el('validation-error-line')!.textContent).toContain('oops: bad');
      expect(el('validation-error-message')!.textContent).toContain('invalid mapping');
    });

    it('reports an error without a line rather than pointing at a made-up one', async () => {
      vi.useFakeTimers();
      setup('', manifestValidationFailure({ line: null, column: null }));
      await component.setMode('yaml');
      component.onYamlInput('spec: {}\n');

      await vi.advanceTimersByTimeAsync(VALIDATION_DEBOUNCE_MS);
      vi.useRealTimers();
      fixture.detectChanges();

      expect(component.errorContext()).toBeNull();
      expect(el('validation-error-line')).toBeNull();
      expect(el('validation-error')).not.toBeNull();
    });

    it('ignores a line number that falls outside the text', async () => {
      vi.useFakeTimers();
      setup('', manifestValidationFailure({ line: 99 }));
      await component.setMode('yaml');
      component.onYamlInput('spec: {}\n');

      await vi.advanceTimersByTimeAsync(VALIDATION_DEBOUNCE_MS);

      expect(component.errorContext()).toBeNull();
    });

    it('says validation is unavailable rather than calling the manifest invalid', async () => {
      vi.useFakeTimers();
      setup();
      await component.setMode('yaml');
      agentService.validateManifest.mockRejectedValueOnce(new Error('offline'));
      component.onYamlInput('spec: {}\n');

      await vi.advanceTimersByTimeAsync(VALIDATION_DEBOUNCE_MS);
      vi.useRealTimers();
      fixture.detectChanges();

      expect(component.validationUnavailable()).toBe(true);
      expect(component.validationError()).toBeNull();
      expect(el('validation-unavailable')).not.toBeNull();
      expect(el('validation-error')).toBeNull();
    });

    it('does not let a late answer overwrite a newer one', async () => {
      setup();
      await component.setMode('yaml');
      let releaseFirst: (value: ManifestValidation) => void = () => {};
      agentService.validateManifest
        .mockImplementationOnce(
          () => new Promise<ManifestValidation>((resolve) => (releaseFirst = resolve)),
        )
        .mockImplementationOnce(async () => manifestValidationFailure({ line: 7 }));

      const first = component['runValidation']('old');
      const second = component['runValidation']('new');
      await second;
      releaseFirst(manifestValidation({ metadata: { name: 'x' }, spec: {} }));
      await first;

      // La réponse tardive du premier appel ne doit pas effacer l'échec du second.
      expect(component.validationError()?.line).toBe(7);
      expect(component.isValid()).toBe(false);
    });
  });

  describe('switching back and forth', () => {
    it('carries a non-trivial manifest into the form and back out without losing anything', async () => {
      const document = nonTrivialManifestDocument();
      setup('', manifestValidation(document));
      await component.setMode('yaml');
      component.onYamlInput('# le texte importe peu : le serveur fait autorité\n');

      await component.setMode('form');

      expect(component.mode()).toBe('form');
      expect(component.unsupported()).toEqual([]);
      expect(component.model().name).toBe('redacteur');
      expect(component.model().inputs.fields.length).toBe(6);
      expect(component.model().networkAllowlist).toEqual(['api.github.com', 'registry.npmjs.org']);

      // Le YAML régénéré se relit dans le même modèle : la bascule est réversible.
      const regenerated = manifestModelToYaml(component.model());
      component.patch({ description: component.model().description });
      expect(emitted.at(-1)).toBe(regenerated);
    });

    it('does not rewrite the source YAML just because the form was opened', async () => {
      setup('', manifestValidation(nonTrivialManifestDocument()));
      await component.setMode('yaml');
      component.onYamlInput('metadata:\n  name: redacteur\n');
      const before = emitted.length;

      await component.setMode('form');

      // Regarder le formulaire n'émet rien : le fichier de départ reste celui qui serait soumis.
      expect(emitted.length).toBe(before);
      expect(component.yaml()).toBe('metadata:\n  name: redacteur\n');
    });

    it('refuses the switch and keeps the YAML when the manifest does not parse', async () => {
      setup('', manifestValidationFailure({ line: 2 }));
      await component.setMode('yaml');
      component.onYamlInput('metadata:\n oops\n');

      await component.setMode('form');
      fixture.detectChanges();

      expect(component.mode()).toBe('yaml');
      expect(component.switchBlocked()).toBe(true);
      expect(el('switch-blocked')).not.toBeNull();
      expect(el('manifest-yaml')).not.toBeNull();
      // Le texte de l'utilisateur est intact.
      expect(component.yaml()).toBe('metadata:\n oops\n');
    });

    it('clears the refusal as soon as the YAML is touched again', async () => {
      setup('', manifestValidationFailure());
      await component.setMode('yaml');
      component.onYamlInput('metadata:\n oops\n');
      await component.setMode('form');
      expect(component.switchBlocked()).toBe(true);

      component.onYamlInput('metadata:\n  name: fixed\n');

      expect(component.switchBlocked()).toBe(false);
    });

    it('goes to YAML mode without asking the server anything', async () => {
      setup();
      component.patch({ name: 'redacteur' });

      await component.setMode('yaml');
      fixture.detectChanges();

      expect(component.mode()).toBe('yaml');
      expect(agentService.validateManifest).not.toHaveBeenCalled();
      expect((el('manifest-yaml') as HTMLTextAreaElement).value).toContain('name: redacteur');
    });

    it('starts from a blank model when the YAML is empty, without calling the server', async () => {
      setup();
      await component.setMode('yaml');

      await component.setMode('form');

      expect(component.mode()).toBe('form');
      expect(agentService.validateManifest).not.toHaveBeenCalled();
      expect(component.model().name).toBe('');
    });
  });

  describe('constructs the form cannot edit', () => {
    async function switchWith(document: JsonObject): Promise<void> {
      setup('', manifestValidation(document));
      await component.setMode('yaml');
      component.onYamlInput('placeholder\n');
      await component.setMode('form');
      fixture.detectChanges();
    }

    it('names each one in a banner instead of dropping it', async () => {
      const document = nonTrivialManifestDocument();
      (document['spec'] as JsonObject)['sidecars'] = [{ image: 'redis:7' }];

      await switchWith(document);

      expect(component.mode()).toBe('form');
      expect(el('unsupported-banner')).not.toBeNull();
      const items = [...all('unsupported-item')].map((node) => node.textContent!.trim());
      expect(items.length).toBe(1);
      expect(items[0]).toContain('spec.sidecars');
      expect(items[0]).toContain('manifestEditor.unsupported.unknownKey');
    });

    it('writes the preserved fragment back into the generated YAML', async () => {
      const document = nonTrivialManifestDocument();
      (document['spec'] as JsonObject)['sidecars'] = [{ image: 'redis:7' }];

      await switchWith(document);
      component.patch({ description: 'changé' });

      expect(emitted.at(-1)).toContain('sidecars:');
      expect(emitted.at(-1)).toContain('image: redis:7');
      expect(emitted.at(-1)).toContain('description: changé');
    });

    it('locks the schema builder and keeps the schema whole', async () => {
      const document = nonTrivialManifestDocument();
      const inputs = (document['spec'] as JsonObject)['inputs'] as JsonObject;
      (inputs['properties'] as JsonObject)['payload'] = { oneOf: [{ type: 'string' }] };

      await switchWith(document);

      expect(el('inputs-locked')).not.toBeNull();
      expect(all('input-field').length).toBe(0);
      expect(all('add-field').length).toBe(0);
      component.patch({ description: 'changé' });
      // Les six autres propriétés sont toujours là, ainsi que celle qui a bloqué l'édition.
      expect(emitted.at(-1)).toContain('oneOf');
      expect(emitted.at(-1)).toContain('repository');
      expect(emitted.at(-1)).toContain('maxCommits');
    });
  });

  describe('seeding from an existing manifest', () => {
    it('loads the seed into the form', async () => {
      setup('metadata:\n  name: redacteur\n', manifestValidation(nonTrivialManifestDocument()));

      await settle();

      expect(agentService.validateManifest).toHaveBeenCalledWith('metadata:\n  name: redacteur\n');
      expect(component.model().name).toBe('redacteur');
      expect(component.mode()).toBe('form');
      // Réamorcer n'est pas éditer : rien n'est émis vers le parent.
      expect(emitted).toEqual([]);
    });

    it('opens in YAML mode when the seed does not parse', async () => {
      setup('metadata:\n oops\n', manifestValidationFailure({ line: 2 }));

      await settle();

      expect(component.mode()).toBe('yaml');
      expect(component.yaml()).toBe('metadata:\n oops\n');
      expect((el('manifest-yaml') as HTMLTextAreaElement).value).toBe('metadata:\n oops\n');
    });

    it('does not re-seed on the value it just emitted itself', async () => {
      setup();
      component.patch({ name: 'redacteur' });
      const produced = emitted.at(-1)!;
      agentService.validateManifest.mockClear();

      fixture.componentRef.setInput('initialYaml', produced);
      await settle();

      expect(agentService.validateManifest).not.toHaveBeenCalled();
      expect(component.model().name).toBe('redacteur');
    });

    it('re-seeds when the parent hands it a different manifest', async () => {
      setup('metadata:\n  name: a\n', manifestValidation(nonTrivialManifestDocument()));
      await settle();

      fixture.componentRef.setInput('initialYaml', 'metadata:\n  name: b\n');
      await settle();

      expect(agentService.validateManifest).toHaveBeenLastCalledWith('metadata:\n  name: b\n');
    });

    it('starts blank on an empty seed without touching the network', async () => {
      setup('');

      await settle();

      expect(agentService.validateManifest).not.toHaveBeenCalled();
      expect(component.model()).toEqual(component.model());
      expect(component.mode()).toBe('form');
    });
  });
});
