import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AgentService } from '../../services/agent.service';
import { RunService } from '../../services/run.service';
import { Agent } from '../../core/models';
import { JsonSchemaProperty } from '../../core/models/json-schema.model';

interface FormField {
  key: string;
  schema: JsonSchemaProperty;
  required: boolean;
}

@Component({
  selector: 'app-new-run-form',
  standalone: true,
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './new-run-form.component.html',
  styleUrls: ['./new-run-form.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NewRunFormComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly agentService = inject(AgentService);
  private readonly runService = inject(RunService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly agents = this.agentService.agents;
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);
  readonly projectId = signal<string | null>(null);
  readonly selectedAgentId = signal<string | null>(null);

  readonly projectAgents = computed(() => {
    const pid = this.projectId();
    const all = this.agents();
    return pid ? all.filter((a) => a.projectId === pid) : all;
  });

  readonly selectedAgent = computed<Agent | null>(() => {
    const id = this.selectedAgentId();
    return id ? (this.agents().find((a) => a.id === id) ?? null) : null;
  });

  readonly fields = computed<FormField[]>(() => {
    const agent = this.selectedAgent();
    if (!agent?.inputsSchema?.properties) return [];
    const required = new Set(agent.inputsSchema.required ?? []);
    return Object.entries(agent.inputsSchema.properties).map(([key, schema]) => ({
      key,
      schema,
      required: required.has(key),
    }));
  });

  form: FormGroup = this.fb.group({});

  constructor() {
    // Rebuild the reactive form whenever the selected agent's schema changes.
    effect(() => {
      const fields = this.fields();
      const group: Record<string, unknown> = {};
      for (const field of fields) {
        const validators = field.required ? [Validators.required] : [];
        const defaultValue =
          field.schema.default !== undefined
            ? field.schema.default
            : field.schema.type === 'boolean'
              ? false
              : '';
        group[field.key] = [defaultValue, validators];
      }
      this.form = this.fb.group(group);
    });
  }

  ngOnInit(): void {
    this.route.params.subscribe((params) => {
      if (params['id']) {
        this.projectId.set(params['id']);
      }
    });
    this.agentService.listAgents();
  }

  onAgentChange(agentId: string): void {
    this.selectedAgentId.set(agentId || null);
  }

  isNumberField(schema: JsonSchemaProperty): boolean {
    return schema.type === 'integer' || schema.type === 'number';
  }

  async submit(): Promise<void> {
    const agent = this.selectedAgent();
    if (!agent || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.submitError.set(null);
    try {
      const rawInputs = this.form.value as Record<string, unknown>;
      const inputs: Record<string, unknown> = {};
      for (const field of this.fields()) {
        const value = rawInputs[field.key];
        if (this.isNumberField(field.schema) && value !== '' && value !== null) {
          inputs[field.key] = Number(value);
        } else {
          inputs[field.key] = value;
        }
      }

      const run = await this.runService.createRun({
        agentId: agent.id,
        inputs,
        context: this.projectId() ? { projectId: this.projectId() } : undefined,
      });

      await this.router.navigate(['/runs', run.id]);
    } catch (err) {
      this.submitError.set('Failed to create run');
    } finally {
      this.isSubmitting.set(false);
    }
  }
}
