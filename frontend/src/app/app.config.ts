import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideTranslateService } from '@ngx-translate/core';
import { provideTranslateHttpLoader } from '@ngx-translate/http-loader';

import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';

// i18n: French is the default language (spec §4.2 — "FR / EN by default"),
// but respect a previously chosen language stored by the user.
const savedLang =
  (typeof localStorage !== 'undefined' && localStorage.getItem('agenthost_lang')) || 'fr';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor, errorInterceptor])),
    provideTranslateService({ fallbackLang: 'fr', lang: savedLang }),
    provideTranslateHttpLoader({ prefix: '/i18n/', suffix: '.json' }),
  ],
};
