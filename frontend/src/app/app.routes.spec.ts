import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { routes } from './app.routes';
import { LoginComponent } from './components/login/login.component';

describe('routing', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
      ],
    });
  });

  afterEach(() => localStorage.clear());

  it('renders /login', async () => {
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/login', LoginComponent);

    expect(component).toBeInstanceOf(LoginComponent);
    expect(harness.routeNativeElement?.querySelector('form')).toBeTruthy();
  });

  it('bounces an unauthenticated user off a guarded route to /login with a returnUrl', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/runs/abc');

    expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fruns%2Fabc');
  });
});
