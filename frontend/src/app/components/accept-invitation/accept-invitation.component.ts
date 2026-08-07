import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../services/auth.service';

/**
 * Écran d'acceptation d'invitation, cible du lien envoyé par courriel
 * (`Email:AcceptInvitationPath`, jeton en `?token=`).
 *
 * L'invité choisit son mot de passe : ni l'organisation ni celui qui invite ne le connaissent
 * jamais. Contrairement à la réinitialisation, l'acceptation **ouvre la session** — le jeton prouve
 * qu'un membre a délibérément ouvert cet accès, et faire repasser l'invité par l'écran de connexion
 * n'ajouterait aucune garantie.
 *
 * Sans jeton dans l'URL, l'écran ne montre pas de formulaire : il n'y a rien à accepter, et offrir
 * un champ de mot de passe laisserait croire qu'un compte va être créé.
 */
@Component({
  selector: 'app-accept-invitation',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './accept-invitation.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AcceptInvitationComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly token = signal<string | null>(null);
  readonly isSubmitting = signal(false);
  readonly errorKey = signal<string | null>(null);

  readonly form = this.fb.group({
    password: ['', [Validators.required, Validators.minLength(8)]],
    displayName: [''],
  });

  ngOnInit(): void {
    const token = this.route.snapshot.queryParamMap.get('token');
    this.token.set(token && token.trim().length > 0 ? token : null);
  }

  async submit(): Promise<void> {
    const token = this.token();
    if (!token || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.errorKey.set(null);
    try {
      const { password, displayName } = this.form.getRawValue();
      await this.authService.acceptInvitation(token, password!, displayName || undefined);
      await this.router.navigateByUrl('/');
    } catch {
      // Jeton expiré, déjà utilisé, révoqué, ou mot de passe refusé par la politique : le serveur
      // ne les distingue pas, donc l'IHM n'invente pas de diagnostic.
      this.errorKey.set('acceptInvitation.failed');
    } finally {
      this.isSubmitting.set(false);
    }
  }
}
