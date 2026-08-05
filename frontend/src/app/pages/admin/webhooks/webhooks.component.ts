import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { WebhookService } from '../../../services/webhook.service';
import { WEBHOOK_EVENTS, Webhook, WebhookEvent } from '../../../core/models';

@Component({
  selector: 'app-admin-webhooks',
  standalone: true,
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './webhooks.component.html',
  styleUrls: ['./webhooks.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WebhooksComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly webhookService = inject(WebhookService);
  private readonly route = inject(ActivatedRoute);

  readonly webhooks = this.webhookService.webhooks;
  readonly isLoading = this.webhookService.isLoading;
  readonly error = this.webhookService.error;

  readonly events: readonly WebhookEvent[] = WEBHOOK_EVENTS;
  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);
  readonly busyId = signal<string | null>(null);

  readonly projectId = signal<string>('');

  readonly form = this.fb.group({
    url: ['', [Validators.required]],
    secretToken: [''],
    events: this.fb.group(
      Object.fromEntries(WEBHOOK_EVENTS.map((e) => [e, [false]])) as Record<string, [boolean]>,
    ),
  });

  ngOnInit(): void {
    this.route.params.subscribe((params) => {
      const projectId = params['projectId'];
      if (projectId) {
        this.projectId.set(projectId);
        this.webhookService.listWebhooks(projectId);
      }
    });
  }

  toggleForm(): void {
    this.showForm.set(!this.showForm());
  }

  private selectedEvents(): string[] {
    const raw = this.form.getRawValue().events as Record<string, boolean>;
    return Object.entries(raw)
      .filter(([, checked]) => checked)
      .map(([event]) => event);
  }

  async submit(): Promise<void> {
    const projectId = this.projectId();
    const events = this.selectedEvents();
    if (!projectId || !this.form.value.url || events.length === 0) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.submitError.set(null);
    try {
      const { url, secretToken } = this.form.getRawValue();
      await this.webhookService.createWebhook({
        projectId,
        url: url!,
        events,
        secretToken: secretToken || undefined,
      });
      this.form.reset();
      this.showForm.set(false);
    } catch (err) {
      this.submitError.set('adminWebhooks.createError');
    } finally {
      this.isSubmitting.set(false);
    }
  }

  async toggleActive(webhook: Webhook): Promise<void> {
    this.busyId.set(webhook.id);
    try {
      await this.webhookService.toggleActive(webhook);
    } catch {
      // surfaced via webhookService.error already
    } finally {
      this.busyId.set(null);
    }
  }

  async remove(id: string): Promise<void> {
    this.busyId.set(id);
    try {
      await this.webhookService.deleteWebhook(id);
    } catch {
      // surfaced via webhookService.error already
    } finally {
      this.busyId.set(null);
    }
  }
}
