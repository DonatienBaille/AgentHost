import { TestBed } from '@angular/core/testing';
import { ErrorService } from './error.service';

describe('ErrorService', () => {
  let service: ErrorService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ErrorService);
  });

  it('starts with no error', () => {
    expect(service.lastError()).toBeNull();
  });

  it('report replaces the last error rather than accumulating', () => {
    service.report('first');
    expect(service.lastError()).toBe('first');

    service.report('second');
    expect(service.lastError()).toBe('second');
  });

  it('clear resets the signal back to null', () => {
    service.report('boom');
    service.clear();
    expect(service.lastError()).toBeNull();
  });

  it('is a fresh instance per TestBed — no state leaks between tests', () => {
    expect(service.lastError()).toBeNull();
  });
});
