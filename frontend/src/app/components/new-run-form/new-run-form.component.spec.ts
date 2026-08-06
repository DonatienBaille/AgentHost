import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { NewRunFormComponent } from './new-run-form.component';
import { AgentService } from '../../services/agent.service';
import { RunService } from '../../services/run.service';
import { Agent, CreateRunRequest, Run } from '../../core/models';
import { JsonSchema } from '../../core/models/json-schema.model';
import { agent, run } from '../../core/testing/fixtures';

class AgentServiceStub {
  readonly agents = signal<Agent[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listAgents = vi.fn(async (_projectId?: string) => {});
}

class RunServiceStub {
  readonly createRun = vi.fn(async (req: CreateRunRequest): Promise<Run> =>
    run('pending', { id: 'r-new', agentId: req.agentId, inputs: req.inputs }),
  );
}

/** Schéma d'entrées couvrant les trois types de champ que le gabarit sait rendre. */
const MIXED_SCHEMA: JsonSchema = {
  type: 'object',
  required: ['topic'],
  properties: {
    topic: { type: 'string', title: 'Sujet', description: 'Le sujet à traiter' },
    wordCount: { type: 'integer', default: 500 },
    ratio: { type: 'number' },
    publish: { type: 'boolean' },
  },
};

describe('NewRunFormComponent', () => {
  let fixture: ComponentFixture<NewRunFormComponent>;
  let component: NewRunFormComponent;
  let agentService: AgentServiceStub;
  let runService: RunServiceStub;
  let navigate: ReturnType<typeof vi.spyOn>;

  function setup(routeParams: Record<string, string> = {}): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [NewRunFormComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: AgentService, useClass: AgentServiceStub },
        { provide: RunService, useClass: RunServiceStub },
        { provide: ActivatedRoute, useValue: { params: of(routeParams) } },
      ],
    });

    agentService = TestBed.inject(AgentService) as unknown as AgentServiceStub;
    runService = TestBed.inject(RunService) as unknown as RunServiceStub;
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    fixture = TestBed.createComponent(NewRunFormComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  /** Sélectionne un agent puis laisse l'effet reconstruire le formulaire. */
  function select(agentId: string): void {
    component.onAgentChange(agentId);
    fixture.detectChanges();
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('agent selection', () => {
    it('loads the agents on init', () => {
      setup();

      expect(agentService.listAgents).toHaveBeenCalledTimes(1);
    });

    it('shows the pick-an-agent hint and no form before a choice is made', () => {
      setup();
      agentService.agents.set([agent()]);
      fixture.detectChanges();

      expect(el('pick-agent-first')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('form')).toBeNull();
    });

    it('lists every agent of the route project and nothing else', () => {
      setup({ id: 'p1' });
      agentService.agents.set([
        agent({ id: 'ag1', projectId: 'p1', name: 'Rédacteur' }),
        agent({ id: 'ag2', projectId: 'p2', name: 'Ailleurs' }),
      ]);
      fixture.detectChanges();

      const options = fixture.nativeElement.querySelectorAll(
        '#agent-select option',
      ) as NodeListOf<HTMLOptionElement>;
      // La première option est le libellé « choisir un agent ».
      expect([...options].map((o) => o.value)).toEqual(['', 'ag1']);
    });

    it('lists all agents when the route carries no project id', () => {
      setup();
      agentService.agents.set([
        agent({ id: 'ag1', projectId: 'p1' }),
        agent({ id: 'ag2', projectId: 'p2' }),
      ]);
      fixture.detectChanges();

      expect(component.projectAgents().map((a) => a.id)).toEqual(['ag1', 'ag2']);
    });

    it('goes back to the hint when the choice is cleared', () => {
      setup();
      agentService.agents.set([agent({ id: 'ag1' })]);
      select('ag1');
      expect(fixture.nativeElement.querySelector('form')).not.toBeNull();

      select('');

      expect(component.selectedAgentId()).toBeNull();
      expect(el('pick-agent-first')).not.toBeNull();
    });
  });

  describe('dynamic form building', () => {
    beforeEach(() => {
      setup({ id: 'p1' });
      agentService.agents.set([agent({ id: 'ag1', inputsSchema: MIXED_SCHEMA })]);
      fixture.detectChanges();
      select('ag1');
    });

    it('renders one field per schema property, in schema order', () => {
      expect(all('form-field').length).toBe(4);
      expect(component.fields().map((f) => f.key)).toEqual([
        'topic',
        'wordCount',
        'ratio',
        'publish',
      ]);
    });

    it('maps string to a text input, integer and number to number, boolean to a checkbox', () => {
      const types = [...all('form-field')].map((d) => d.querySelector('input')!.type);
      expect(types).toEqual(['text', 'number', 'number', 'checkbox']);
    });

    it('labels a field with its title, falling back to the raw key', () => {
      const labels = [...all('form-field')].map((d) => d.querySelector('label')!.textContent!.trim());
      expect(labels[0]).toContain('Sujet');
      expect(labels[1]).toContain('wordCount');
    });

    it('marks only the required fields with an asterisk and a required validator', () => {
      const stars = [...all('form-field')].map((d) => d.querySelector('span.text-red-400') !== null);
      expect(stars).toEqual([true, false, false, false]);
      expect(component.form.controls['topic'].hasError('required')).toBe(true);
      expect(component.form.controls['ratio'].hasError('required')).toBe(false);
    });

    it('renders the description only for the fields that carry one', () => {
      expect(all('field-description').length).toBe(1);
      expect(el('field-description')!.textContent).toContain('Le sujet à traiter');
    });

    it('seeds defaults from the schema, false for booleans and empty string otherwise', () => {
      expect(component.form.value).toEqual({
        topic: '',
        wordCount: 500,
        ratio: '',
        publish: false,
      });
    });

    it('rebuilds the whole form when another agent is selected', () => {
      agentService.agents.set([
        agent({ id: 'ag1', inputsSchema: MIXED_SCHEMA }),
        agent({
          id: 'ag2',
          inputsSchema: { type: 'object', properties: { other: { type: 'string' } } },
        }),
      ]);
      fixture.detectChanges();

      select('ag2');

      expect(Object.keys(component.form.controls)).toEqual(['other']);
      expect(all('form-field').length).toBe(1);
    });
  });

  describe('agent without inputs', () => {
    it('shows the no-inputs message and submits an empty inputs object', async () => {
      setup({ id: 'p1' });
      agentService.agents.set([
        agent({ id: 'ag-bare', inputsSchema: { type: 'object', properties: {} } }),
      ]);
      fixture.detectChanges();
      select('ag-bare');

      expect(el('no-inputs')).not.toBeNull();
      expect(all('form-field').length).toBe(0);
      // Un formulaire vide est valide : le bouton reste actionnable.
      expect((el('submit-run') as HTMLButtonElement).disabled).toBe(false);

      await component.submit();

      expect(runService.createRun).toHaveBeenCalledWith({
        agentId: 'ag-bare',
        inputs: {},
        context: { projectId: 'p1' },
      });
    });

    it('treats a null inputsSchema the same way', () => {
      setup();
      agentService.agents.set([
        agent({ id: 'ag-null', inputsSchema: null as unknown as JsonSchema }),
      ]);
      fixture.detectChanges();
      select('ag-null');

      expect(component.fields()).toEqual([]);
      expect(el('no-inputs')).not.toBeNull();
    });
  });

  describe('validation', () => {
    beforeEach(() => {
      setup({ id: 'p1' });
      agentService.agents.set([agent({ id: 'ag1', inputsSchema: MIXED_SCHEMA })]);
      fixture.detectChanges();
      select('ag1');
    });

    it('disables the submit button while a required field is empty', () => {
      expect(component.form.invalid).toBe(true);
      expect((el('submit-run') as HTMLButtonElement).disabled).toBe(true);
    });

    it('does not call the service and marks everything touched on an invalid submit', async () => {
      await component.submit();

      expect(runService.createRun).not.toHaveBeenCalled();
      expect(component.form.touched).toBe(true);
    });

    it('enables submit once the required field is filled', () => {
      component.form.patchValue({ topic: 'Angular' });
      fixture.detectChanges();

      expect(component.form.valid).toBe(true);
      expect((el('submit-run') as HTMLButtonElement).disabled).toBe(false);
    });

    it('refuses to submit when no agent is selected at all', async () => {
      select('');

      await component.submit();

      expect(runService.createRun).not.toHaveBeenCalled();
    });
  });

  describe('CreateRunRequest payload', () => {
    beforeEach(() => {
      setup({ id: 'p1' });
      agentService.agents.set([agent({ id: 'ag1', inputsSchema: MIXED_SCHEMA })]);
      fixture.detectChanges();
      select('ag1');
    });

    it('sends agentId, inputs and context — and nothing else', async () => {
      component.form.patchValue({ topic: 'Angular', wordCount: 800, ratio: 1.5, publish: true });

      await component.submit();

      expect(runService.createRun).toHaveBeenCalledTimes(1);
      const sent = runService.createRun.mock.calls[0][0];
      expect(Object.keys(sent).sort()).toEqual(['agentId', 'context', 'inputs']);
      // Ni orgId ni triggeredByUserId : le serveur les déduit du JWT (CreateRunRequest).
      expect(sent).toEqual({
        agentId: 'ag1',
        inputs: { topic: 'Angular', wordCount: 800, ratio: 1.5, publish: true },
        context: { projectId: 'p1' },
      });
    });

    it('coerces numeric fields typed as strings by the DOM into real numbers', async () => {
      component.form.patchValue({ topic: 'x', wordCount: '800', ratio: '1.5' });

      await component.submit();

      const inputs = runService.createRun.mock.calls[0][0].inputs;
      expect(inputs['wordCount']).toBe(800);
      expect(inputs['ratio']).toBe(1.5);
      expect(typeof inputs['topic']).toBe('string');
      expect(inputs['publish']).toBe(false);
    });

    it('leaves an empty optional number as an empty string rather than coercing it to 0', async () => {
      // Comportement ACTUEL épinglé : la coercition saute la chaîne vide, donc `ratio: ''` part
      // tel quel dans le corps. Le serveur reçoit une chaîne là où le schéma annonce un nombre.
      component.form.patchValue({ topic: 'x' });

      await component.submit();

      expect(runService.createRun.mock.calls[0][0].inputs['ratio']).toBe('');
    });

    it('sends only the schema keys, ignoring stray controls added to the form', async () => {
      component.form.addControl('sneaky', component.form.controls['topic']);
      component.form.patchValue({ topic: 'x' });

      await component.submit();

      expect(Object.keys(runService.createRun.mock.calls[0][0].inputs).sort()).toEqual([
        'publish',
        'ratio',
        'topic',
        'wordCount',
      ]);
    });
  });

  describe('payload without a project in the route', () => {
    it('leaves context undefined rather than inventing one', async () => {
      setup();
      agentService.agents.set([agent({ id: 'ag1', inputsSchema: MIXED_SCHEMA })]);
      fixture.detectChanges();
      select('ag1');
      component.form.patchValue({ topic: 'x' });

      await component.submit();

      const sent = runService.createRun.mock.calls[0][0];
      expect(sent.context).toBeUndefined();
      // La clé reste présente avec une valeur `undefined` : elle disparaît à la sérialisation JSON.
      expect(Object.keys(sent).sort()).toEqual(['agentId', 'context', 'inputs']);
      expect(JSON.parse(JSON.stringify(sent))).toEqual({
        agentId: 'ag1',
        inputs: { topic: 'x', wordCount: 500, ratio: '', publish: false },
      });
    });
  });

  describe('submission outcome', () => {
    beforeEach(() => {
      setup({ id: 'p1' });
      agentService.agents.set([agent({ id: 'ag1', inputsSchema: MIXED_SCHEMA })]);
      fixture.detectChanges();
      select('ag1');
      component.form.patchValue({ topic: 'Angular' });
    });

    it('navigates to the freshly created run', async () => {
      await component.submit();

      expect(navigate).toHaveBeenCalledWith(['/runs', 'r-new']);
      expect(component.isSubmitting()).toBe(false);
      expect(component.submitError()).toBeNull();
    });

    it('shows the error banner and stays on the form when creation fails', async () => {
      runService.createRun.mockRejectedValueOnce(new Error('boom'));

      await component.submit();
      fixture.detectChanges();

      expect(el('submit-error')!.textContent).toContain('errors.createRun');
      expect(navigate).not.toHaveBeenCalled();
      expect(component.isSubmitting()).toBe(false);
    });

    it('disables the submit button while the request is in flight', async () => {
      let release!: (r: Run) => void;
      runService.createRun.mockReturnValueOnce(
        new Promise<Run>((resolve) => {
          release = resolve;
        }),
      );

      const pending = component.submit();
      fixture.detectChanges();
      expect(component.isSubmitting()).toBe(true);
      expect((el('submit-run') as HTMLButtonElement).disabled).toBe(true);

      release(run('pending', { id: 'r-new' }));
      await pending;
      fixture.detectChanges();
      expect((el('submit-run') as HTMLButtonElement).disabled).toBe(false);
    });

    it('clears a previous error on the next attempt', async () => {
      runService.createRun.mockRejectedValueOnce(new Error('boom'));
      await component.submit();
      expect(component.submitError()).not.toBeNull();

      await component.submit();

      expect(component.submitError()).toBeNull();
    });
  });
});
