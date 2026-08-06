import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WebhookService } from './webhook.service';
import { environment } from '../../environments/environment';
import { Webhook } from '../core/models';

const URL = `${environment.apiUrl}/api/webhooks`;

function webhook(id: string, isActive = true): Webhook {
  return {
    id,
    projectId: 'p1',
    url: 'https://example.test/hook',
    events: ['run.succeeded'],
    secretToken: null,
    isActive,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('WebhookService', () => {
  let service: WebhookService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WebhookService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  async function seed(...hooks: Webhook[]): Promise<void> {
    const pending = service.listWebhooks('p1');
    httpMock.expectOne(`${URL}?projectId=p1`).flush(hooks);
    await pending;
  }

  it('lists scoped by a url-encoded projectId', async () => {
    const pending = service.listWebhooks('p 1/x');
    const req = httpMock.expectOne(`${URL}?projectId=${encodeURIComponent('p 1/x')}`);
    expect(req.request.method).toBe('GET');
    req.flush([webhook('w1')]);

    await pending;
    expect(service.webhooks().length).toBe(1);
    expect(service.error()).toBeNull();
    expect(service.isLoading()).toBe(false);
  });

  it('treats a null body as an empty list', async () => {
    const pending = service.listWebhooks('p1');
    httpMock.expectOne(`${URL}?projectId=p1`).flush(null);
    await pending;
    expect(service.webhooks()).toEqual([]);
  });

  it('sets the error signal and stops loading on failure', async () => {
    const pending = service.listWebhooks('p1');
    httpMock.expectOne(`${URL}?projectId=p1`).flush(null, { status: 500, statusText: 'Err' });
    await pending;

    expect(service.error()).toBe('Failed to load webhooks');
    expect(service.isLoading()).toBe(false);
  });

  it('creates with exactly the supplied fields — no orgId, no identity field', async () => {
    const pending = service.createWebhook({
      projectId: 'p1',
      url: 'https://example.test/hook',
      events: ['run.succeeded'],
    });

    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('POST');
    const keys = Object.keys(req.request.body as object);
    expect(keys).not.toContain('orgId');
    expect(keys).not.toContain('organizationId');
    expect(keys).not.toContain('createdByUserId');
    expect(req.request.body).toEqual({
      projectId: 'p1',
      url: 'https://example.test/hook',
      events: ['run.succeeded'],
    });
    req.flush(webhook('w1'));

    await pending;
    expect(service.webhooks().length).toBe(1);
  });

  it('updates through PUT and replaces the row in place', async () => {
    await seed(webhook('w1'), webhook('w2'));

    const pending = service.updateWebhook('w1', { url: 'https://new.test/hook' });
    const req = httpMock.expectOne(`${URL}/w1`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ url: 'https://new.test/hook' });
    req.flush({ ...webhook('w1'), url: 'https://new.test/hook' });
    await pending;

    expect(service.webhooks().length).toBe(2);
    expect(service.webhooks()[0].url).toBe('https://new.test/hook');
  });

  it('toggleActive sends the inverted flag alone', async () => {
    await seed(webhook('w1', true));

    const pending = service.toggleActive(webhook('w1', true));
    const req = httpMock.expectOne(`${URL}/w1`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ isActive: false });
    req.flush(webhook('w1', false));
    await pending;

    expect(service.webhooks()[0].isActive).toBe(false);
  });

  it('deletes and drops the row locally', async () => {
    await seed(webhook('w1'), webhook('w2'));

    const pending = service.deleteWebhook('w1');
    const req = httpMock.expectOne(`${URL}/w1`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
    await pending;

    expect(service.webhooks().map((w) => w.id)).toEqual(['w2']);
  });

  it('propagates a delete failure and leaves local state untouched', async () => {
    await seed(webhook('w1'));

    const pending = service.deleteWebhook('w1');
    httpMock.expectOne(`${URL}/w1`).flush(null, { status: 404, statusText: 'Not Found' });

    await expect(pending).rejects.toBeTruthy();
    expect(service.webhooks().map((w) => w.id)).toEqual(['w1']);
  });
});
