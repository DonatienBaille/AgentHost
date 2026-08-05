import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslateService, TranslatePipe } from '@ngx-translate/core';

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

  readonly languages = ['fr', 'en'];

  useLanguage(lang: string): void {
    this.translate.use(lang);
    localStorage.setItem('agenthost_lang', lang);
  }

  currentLanguage(): string {
    return this.translate.getCurrentLang() || 'fr';
  }
}
