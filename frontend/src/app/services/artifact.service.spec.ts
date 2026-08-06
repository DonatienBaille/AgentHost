import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ArtifactService } from './artifact.service';
import { environment } from '../../environments/environment';
import { Artifact } from '../core/models';

const RUNS = `${environment.apiUrl}/api/runs`;
const ARTIFACTS = `${environment.apiUrl}/api/artifacts`;

function artifact(id: string, name = 'report.txt'): Artifact {
  return {
    id,
    runId: 'r1',
    name,
    artifactType: 'text/plain',
    sizeBytes: 12,
    createdAt: '2026-01-01T00:00:00Z',
  };
}

describe('ArtifactService', () => {
  let service: ArtifactService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ArtifactService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('lists a run’s artifacts from the run sub-resource', async () => {
    const pending = service.listArtifacts('r1');
    expect(service.isLoading()).toBe(true);

    const req = httpMock.expectOne(`${RUNS}/r1/artifacts`);
    expect(req.request.method).toBe('GET');
    req.flush([artifact('f1'), artifact('f2', 'log.txt')]);

    await pending;
    expect(service.artifacts().length).toBe(2);
    expect(service.error()).toBeNull();
    expect(service.isLoading()).toBe(false);
  });

  it('treats a null body as an empty list', async () => {
    const pending = service.listArtifacts('r1');
    httpMock.expectOne(`${RUNS}/r1/artifacts`).flush(null);
    await pending;
    expect(service.artifacts()).toEqual([]);
  });

  it('sets the error signal and stops loading on failure', async () => {
    const pending = service.listArtifacts('r1');
    httpMock.expectOne(`${RUNS}/r1/artifacts`).flush(null, { status: 500, statusText: 'Err' });
    await pending;

    expect(service.error()).toBe('errors.loadArtifacts');
    expect(service.isLoading()).toBe(false);
  });

  describe('download', () => {
    // jsdom has no object-URL implementation; stub it per test so nothing leaks between them.
    let createObjectURL: ReturnType<typeof vi.fn>;
    let revokeObjectURL: ReturnType<typeof vi.fn>;
    let originalCreate: unknown;
    let originalRevoke: unknown;

    beforeEach(() => {
      originalCreate = URL.createObjectURL;
      originalRevoke = URL.revokeObjectURL;
      createObjectURL = vi.fn(() => 'blob:fake');
      revokeObjectURL = vi.fn();
      URL.createObjectURL = createObjectURL as unknown as typeof URL.createObjectURL;
      URL.revokeObjectURL = revokeObjectURL as unknown as typeof URL.revokeObjectURL;
    });

    afterEach(() => {
      URL.createObjectURL = originalCreate as typeof URL.createObjectURL;
      URL.revokeObjectURL = originalRevoke as typeof URL.revokeObjectURL;
    });

    it('fetches the blob through HttpClient so the auth interceptor can sign it', async () => {
      const pending = service.download(artifact('f1'));

      const req = httpMock.expectOne(`${ARTIFACTS}/f1/download`);
      expect(req.request.method).toBe('GET');
      expect(req.request.responseType).toBe('blob');
      req.flush(new Blob(['hello']));

      await pending;
      expect(createObjectURL).toHaveBeenCalledTimes(1);
      expect(revokeObjectURL).toHaveBeenCalledWith('blob:fake');
      // The temporary anchor must not be left behind in the document.
      expect(document.querySelectorAll('a[download]').length).toBe(0);
    });

    it('propagates a download failure instead of swallowing it', async () => {
      const pending = service.download(artifact('f1'));
      httpMock.expectOne(`${ARTIFACTS}/f1/download`).flush(null, {
        status: 404,
        statusText: 'Not Found',
      });

      await expect(pending).rejects.toBeTruthy();
      expect(createObjectURL).not.toHaveBeenCalled();
    });
  });
});
