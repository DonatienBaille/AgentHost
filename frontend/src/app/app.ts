import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslateService, TranslatePipe } from '@ngx-translate/core';
import { filter, map, startWith } from 'rxjs';
import { AuthService } from './services/auth.service';

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

  readonly languages = ['fr', 'en'];

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
  }

  useLanguage(lang: string): void {
    this.translate.use(lang);
    localStorage.setItem('agenthost_lang', lang);
  }

  currentLanguage(): string {
    return this.translate.getCurrentLang() || 'fr';
  }

  logout(): void {
    this.authService.logout();
  }
}
