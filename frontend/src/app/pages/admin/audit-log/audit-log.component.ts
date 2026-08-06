import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { TranslatePipe } from '@ngx-translate/core';
import { AuditService } from '../../../services/audit.service';
import { AuthService } from '../../../services/auth.service';

@Component({
  selector: 'app-admin-audit-log',
  standalone: true,
  imports: [DatePipe, TranslatePipe],
  templateUrl: './audit-log.component.html',
  styleUrls: ['./audit-log.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuditLogComponent implements OnInit {
  private readonly auditService = inject(AuditService);
  private readonly authService = inject(AuthService);

  readonly entries = this.auditService.entries;
  readonly isLoading = this.auditService.isLoading;
  readonly error = this.auditService.error;

  /**
   * Le serveur exige `maintainer` sur GET /api/organizations/{orgId}/audit-log
   * (backend Endpoints/AuditEndpoints.cs). La page était rendue à l'identique aux quatre rôles :
   * un `developer` ou un `viewer` déclenchait un appel voué au 403, affiché comme une erreur de
   * chargement générique — « ça n'a pas marché » là où la vraie réponse est « ce n'est pas pour
   * vous ». On ne demande donc pas ce qu'on n'a pas le droit de lire.
   */
  readonly canView = this.authService.isMaintainerOrAbove;

  ngOnInit(): void {
    if (!this.canView()) return;

    const orgId = this.authService.currentUser()?.orgId;
    if (orgId) {
      this.auditService.listAuditLog(orgId);
    }
  }
}
