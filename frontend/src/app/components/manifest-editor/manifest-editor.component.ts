import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { AgentService } from '../../services/agent.service';
import {
  AGENT_TYPES,
  APPROVAL_ROLES,
  MANIFEST_FIELD_TYPES,
  ManifestField,
  ManifestFieldType,
  ManifestFormModel,
  NETWORK_PERMISSIONS,
  RUNTIME_PROFILES,
  UnsupportedConstruct,
  VCS_PERMISSIONS,
  emptyManifestModel,
  findCommentLines,
  formatScalarText,
  importManifest,
  manifestModelToYaml,
  parseScalarText,
} from '../../core/manifest';
import { AgentType, ManifestValidationError } from '../../core/models';

export type ManifestEditorMode = 'form' | 'yaml';

/** Attente avant de valider une frappe. Assez long pour ne pas marteler l'API, assez court pour que l'erreur suive le curseur. */
export const VALIDATION_DEBOUNCE_MS = 400;

/**
 * Éditeur de manifeste à deux modes.
 *
 * Le YAML est la seule source de vérité : c'est lui qui sort du composant, lui qui est persisté et
 * versionné. Le mode formulaire est une projection — rien de son modèle n'est stocké, il ne survit
 * pas à la fermeture de l'éditeur, et il se reconstruit à partir du YAML à chaque bascule.
 *
 * Ce que le formulaire ne sait pas éditer n'est pas perdu pour autant : les fragments inconnus sont
 * conservés et réémis à leur place, les schémas hors sous-ensemble sont gardés entiers et rendus
 * non éditables, et un bandeau les nomme un par un. La bascule n'est refusée que dans le seul cas
 * où il n'y a rien à projeter : un YAML qui ne parse pas.
 */
@Component({
  selector: 'app-manifest-editor',
  standalone: true,
  imports: [TranslatePipe],
  templateUrl: './manifest-editor.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ManifestEditorComponent implements OnDestroy {
  private readonly agentService = inject(AgentService);

  /** YAML de départ. Un changement de cette entrée réamorce l'éditeur ; sa propre sortie ne le réamorce pas. */
  readonly initialYaml = input<string>('');
  readonly yamlChange = output<string>();

  readonly mode = signal<ManifestEditorMode>('form');
  readonly yaml = signal<string>('');
  readonly model = signal<ManifestFormModel>(emptyManifestModel());
  readonly unsupported = signal<UnsupportedConstruct[]>([]);

  readonly isValidating = signal(false);
  readonly validationError = signal<ManifestValidationError | null>(null);
  readonly isValid = signal(false);
  /** Le serveur n'a pas répondu : on le dit, et on laisse l'utilisateur continuer en YAML. */
  readonly validationUnavailable = signal(false);
  /** Renseigné quand une bascule vers le formulaire a été refusée faute de manifeste lisible. */
  readonly switchBlocked = signal(false);

  /** Aperçu en direct : ce que le formulaire écrirait, tel quel. */
  readonly preview = computed(() => manifestModelToYaml(this.model()));

  readonly fieldTypes = MANIFEST_FIELD_TYPES;
  readonly agentTypes = computed(() => withCurrent(AGENT_TYPES, this.model().type));
  readonly vcsOptions = computed(() => withCurrent(VCS_PERMISSIONS, this.model().vcs));
  readonly networkOptions = computed(() => withCurrent(NETWORK_PERMISSIONS, this.model().network));
  readonly profileOptions = computed(() => withCurrent(RUNTIME_PROFILES, this.model().runtimeProfile));
  readonly roleOptions = computed(() => withCurrent(APPROVAL_ROLES, this.model().approvalRole));

  readonly secretsText = computed(() => this.model().secrets.join(', '));
  readonly allowlistText = computed(() => this.model().networkAllowlist.join(', '));

  private debounceHandle: ReturnType<typeof setTimeout> | null = null;
  /** Numéro de la dernière validation demandée : une réponse en retard ne doit pas écraser une plus récente. */
  private validationSeq = 0;
  private lastEmitted = '';

  constructor() {
    effect(() => {
      const seed = this.initialYaml();
      // Notre propre valeur qui revient par le parent : rien à réamorcer.
      if (seed === this.lastEmitted) return;
      untracked(() => void this.seed(seed));
    });
  }

  ngOnDestroy(): void {
    if (this.debounceHandle !== null) clearTimeout(this.debounceHandle);
  }

  // ---------------------------------------------------------------- bascule

  async setMode(mode: ManifestEditorMode): Promise<void> {
    if (mode === this.mode()) return;
    if (mode === 'yaml') {
      this.switchBlocked.set(false);
      this.mode.set('yaml');
      return;
    }
    await this.enterFormMode();
  }

  /**
   * YAML → formulaire. La seule bascule qui peut échouer, et pour une seule raison : un manifeste
   * illisible n'a pas de projection. On reste alors en YAML avec l'erreur située — refuser en
   * expliquant vaut mieux qu'ouvrir un formulaire vide sur le travail de quelqu'un.
   */
  private async enterFormMode(): Promise<void> {
    const loaded = await this.loadIntoForm();
    if (loaded) {
      this.switchBlocked.set(false);
      this.mode.set('form');
    } else {
      this.switchBlocked.set(true);
    }
  }

  private async loadIntoForm(): Promise<boolean> {
    const source = this.yaml();
    if (!source.trim()) {
      // Rien à projeter et rien à perdre : on part du modèle vide.
      this.model.set(emptyManifestModel());
      this.unsupported.set([]);
      this.validationError.set(null);
      this.isValid.set(false);
      return true;
    }

    const validation = await this.runValidation(source);
    if (!validation || !validation.valid) return false;

    const imported = importManifest(validation);
    this.model.set(imported.model);

    // Les commentaires ne sont dans aucun document analysé : c'est la seule perte que le
    // convertisseur ne peut pas voir, elle se repère sur le texte source.
    const commentLines = findCommentLines(source);
    this.unsupported.set(
      commentLines.length === 0
        ? imported.unsupported
        : [...imported.unsupported, { path: `# ${commentLines.join(', ')}`, reason: 'comments' }],
    );
    return true;
  }

  // ------------------------------------------------------------- mode YAML

  onYamlInput(text: string): void {
    this.yaml.set(text);
    this.emit(text);
    this.switchBlocked.set(false);
    this.scheduleValidation(text);
  }

  private scheduleValidation(text: string): void {
    if (this.debounceHandle !== null) clearTimeout(this.debounceHandle);
    this.debounceHandle = setTimeout(() => {
      this.debounceHandle = null;
      void this.runValidation(text);
    }, VALIDATION_DEBOUNCE_MS);
  }

  private async runValidation(text: string) {
    const seq = ++this.validationSeq;
    this.isValidating.set(true);
    try {
      const validation = await this.agentService.validateManifest(text);
      // Une réponse arrivée après une frappe plus récente ne dit plus rien du texte affiché.
      if (seq !== this.validationSeq) return validation;
      this.validationUnavailable.set(false);
      this.isValid.set(validation.valid);
      this.validationError.set(validation.valid ? null : validation.error);
      return validation;
    } catch {
      if (seq === this.validationSeq) {
        // Le serveur est injoignable : ne pas déclarer le manifeste invalide, on n'en sait rien.
        this.validationUnavailable.set(true);
        this.isValid.set(false);
        this.validationError.set(null);
      }
      return null;
    } finally {
      if (seq === this.validationSeq) this.isValidating.set(false);
    }
  }

  /** La ligne fautive, découpée pour être montrée en contexte. */
  readonly errorContext = computed(() => {
    const error = this.validationError();
    if (!error?.line) return null;
    const lines = this.yaml().split('\n');
    const index = error.line - 1;
    if (index < 0 || index >= lines.length) return null;
    return { line: error.line, text: lines[index] };
  });

  // ---------------------------------------------------------- mode formulaire

  patch(patch: Partial<ManifestFormModel>): void {
    this.model.update((model) => ({ ...model, ...patch }));
    this.onModelChanged();
  }

  patchNumber(key: 'cpu' | 'maxDurationSeconds' | 'approvalCount' | 'defaultMaxUsd' | 'hardMaxUsd', text: string): void {
    const parsed = Number(text);
    this.patch({ [key]: Number.isFinite(parsed) ? parsed : 0 } as Partial<ManifestFormModel>);
  }

  setType(type: string): void {
    this.patch({ type: type as AgentType });
  }

  /** Les listes de noms se saisissent séparées par des virgules ou des retours à la ligne. */
  setSecrets(text: string): void {
    this.patch({ secrets: splitList(text) });
  }

  setAllowlist(text: string): void {
    this.patch({ networkAllowlist: splitList(text) });
  }

  // -- constructeur de champs

  addField(section: 'inputs' | 'outputs'): void {
    this.updateSection(section, (fields) => [
      ...fields,
      { name: '', type: 'string' as ManifestFieldType, title: '', description: '', required: false, defaultValue: null, enumValues: null },
    ]);
  }

  removeField(section: 'inputs' | 'outputs', index: number): void {
    this.updateSection(section, (fields) => fields.filter((_, i) => i !== index));
  }

  patchField(section: 'inputs' | 'outputs', index: number, patch: Partial<ManifestField>): void {
    this.updateSection(section, (fields) =>
      fields.map((field, i) => (i === index ? { ...field, ...patch } : field)),
    );
  }

  setFieldDefault(section: 'inputs' | 'outputs', index: number, text: string): void {
    // Vide veut dire « pas de défaut » : la clé disparaît du schéma plutôt que d'y valoir "".
    this.patchField(section, index, { defaultValue: text === '' ? null : parseScalarText(text) });
  }

  setFieldEnum(section: 'inputs' | 'outputs', index: number, text: string): void {
    const values = splitList(text).map(parseScalarText);
    this.patchField(section, index, { enumValues: values.length === 0 ? null : values });
  }

  fieldDefaultText(field: ManifestField): string {
    return field.defaultValue === null ? '' : formatScalarText(field.defaultValue as string | number | boolean | null);
  }

  fieldEnumText(field: ManifestField): string {
    return (field.enumValues ?? []).map((v) => formatScalarText(v as string | number | boolean | null)).join(', ');
  }

  toggleOutputs(present: boolean): void {
    this.model.update((model) => ({ ...model, outputs: { ...model.outputs, present } }));
    this.onModelChanged();
  }

  private updateSection(section: 'inputs' | 'outputs', update: (fields: ManifestField[]) => ManifestField[]): void {
    this.model.update((model) => ({
      ...model,
      [section]: { ...model[section], present: true, fields: update(model[section].fields) },
    }));
    this.onModelChanged();
  }

  // -- configuration du fournisseur externe

  addConfigEntry(): void {
    this.patch({ externalConfig: [...this.model().externalConfig, { key: '', value: '' }] });
  }

  removeConfigEntry(index: number): void {
    this.patch({ externalConfig: this.model().externalConfig.filter((_, i) => i !== index) });
  }

  patchConfigEntry(index: number, patch: Partial<{ key: string; value: string }>): void {
    this.patch({
      externalConfig: this.model().externalConfig.map((entry, i) =>
        i === index ? { ...entry, ...patch } : entry,
      ),
    });
  }

  // ------------------------------------------------------------------ sortie

  /**
   * Toute modification du formulaire réécrit le YAML.
   *
   * Tant que rien n'a bougé, le YAML d'origine reste intact : ouvrir l'onglet formulaire pour
   * regarder ne reformate le fichier de personne. L'aperçu, lui, montre en permanence ce que le
   * formulaire écrirait — la différence est donc visible avant d'être appliquée.
   */
  private onModelChanged(): void {
    const yaml = manifestModelToYaml(this.model());
    this.yaml.set(yaml);
    this.emit(yaml);
    this.isValid.set(true);
    this.validationError.set(null);
  }

  private emit(yaml: string): void {
    this.lastEmitted = yaml;
    this.yamlChange.emit(yaml);
  }

  private async seed(text: string): Promise<void> {
    this.yaml.set(text);
    this.lastEmitted = text;
    this.unsupported.set([]);
    this.validationError.set(null);
    this.switchBlocked.set(false);
    if (!text.trim()) {
      this.model.set(emptyManifestModel());
      return;
    }
    // Un manifeste existant qui ne se projette pas s'ouvre en YAML plutôt qu'en formulaire vide.
    const loaded = await this.loadIntoForm();
    if (!loaded) this.mode.set('yaml');
  }
}

/**
 * Ajoute la valeur courante à une liste d'options quand elle n'y figure pas : un manifeste écrit à
 * la main peut porter un profil ou un type que l'IHM ne connaît pas, et un `<select>` qui ne
 * propose pas la valeur affichée la remplace au premier changement de n'importe quel autre champ.
 */
function withCurrent(options: readonly string[], current: string): string[] {
  return options.includes(current) || !current ? [...options] : [...options, current];
}

function splitList(text: string): string[] {
  return text
    .split(/[,\n]/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
}
