import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslateService, TranslatePipe } from '@ngx-translate/core';
import { filter, map, startWith } from 'rxjs';
import { AuthService } from './services/auth.service';
import { ErrorService } from './services/error.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe],
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {
  private readonly translate = inject(TranslateService);
  private readonly router = inject(Router);
  readonly authService = inject(AuthService);
  private readonly errorService = inject(ErrorService);

  readonly languages = ['fr', 'en'];

  /** Last HTTP failure reported by the error interceptor, shown as a dismissible banner. */
  readonly lastError = this.errorService.lastError;

  /** Hides the main nav shell on the standalone /login page. */
  readonly isLoginRoute = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects.startsWith('/login')),
      startWith(this.router.url.startsWith('/login')),
    ),
    { initialValue: this.router.url.startsWith('/login') },
  );

  constructor() {
    this.authService.loadFromStorage();

    // Don't drag a stale failure banner across the whole app once the user has moved on.
    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe(() => this.errorService.clear());
  }

  useLanguage(lang: string): void {
    this.translate.use(lang);
    localStorage.setItem('agenthost_lang', lang);
  }

  currentLanguage(): string {
    return this.translate.getCurrentLang() || 'fr';
  }

  dismissError(): void {
    this.errorService.clear();
  }

  async logout(): Promise<void> {
    await this.authService.logout();
  }
}
