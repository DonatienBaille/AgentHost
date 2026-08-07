import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../services/auth.service';

/**
 * Écran de réinitialisation de mot de passe, cible du lien envoyé par courriel
 * (`Email:ResetPasswordPath`, jeton en `?token=`).
 *
 * Deux usages dans un seul écran, distingués par la présence du jeton :
 *  - **sans jeton** : le formulaire de demande. Sa réponse est volontairement identique que le
 *    compte existe ou non — le serveur répond 202 dans les deux cas pour ne pas révéler quelles
 *    adresses sont enregistrées, et l'IHM ne doit surtout pas défaire cette propriété en
 *    distinguant les deux cas à l'écran ;
 *  - **avec jeton** : le choix du nouveau mot de passe.
 *
 * Le succès ne connecte pas : le serveur ne renvoie pas de jetons ici. Quiconque intercepte le
 * lien pourrait sinon obtenir une session sans jamais prouver qu'il connaît le compte autrement.
 */
@Component({
  selector: 'app-reset-password',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './reset-password.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetPasswordComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  /** Jeton lu dans l'URL. Null = mode « demander un lien ». */
  readonly token = signal<string | null>(null);

  readonly isSubmitting = signal(false);
  readonly errorKey = signal<string | null>(null);

  /** Demande envoyée : le message est le même que l'adresse existe ou non. */
  readonly requestSent = signal(false);
  readonly resetDone = signal(false);

  readonly requestForm = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
  });

  readonly resetForm = this.fb.group({
    password: ['', [Validators.required, Validators.minLength(8)]],
  });

  ngOnInit(): void {
    const token = this.route.snapshot.queryParamMap.get('token');
    this.token.set(token && token.trim().length > 0 ? token : null);
  }

  async submitRequest(): Promise<void> {
    if (this.requestForm.invalid) {
      this.requestForm.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorKey.set(null);
    try {
      await this.authService.requestPasswordReset(this.requestForm.getRawValue().email!);
    } catch {
      // Même un échec réseau ne doit rien apprendre sur l'existence du compte : on affiche le
      // message d'accusé de réception dans tous les cas. Une adresse mal formée est déjà refusée
      // par la validation locale, donc il ne reste ici que des causes sans rapport avec le compte.
    } finally {
      this.isSubmitting.set(false);
      this.requestSent.set(true);
    }
  }

  async submitReset(): Promise<void> {
    const token = this.token();
    if (!token || this.resetForm.invalid) {
      this.resetForm.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorKey.set(null);
    try {
      await this.authService.confirmPasswordReset(token, this.resetForm.getRawValue().password!);
      this.resetDone.set(true);
    } catch {
      // Un jeton mort et un mot de passe refusé par la politique sont deux causes distinctes, mais
      // le serveur ne les distingue pas dans sa réponse ; annoncer l'une des deux serait deviner.
      this.errorKey.set('resetPassword.failed');
    } finally {
      this.isSubmitting.set(false);
    }
  }

  goToLogin(): void {
    void this.router.navigate(['/login']);
  }
}
