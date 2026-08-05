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

  ngOnInit(): void {
    const orgId = this.authService.currentUser()?.orgId;
    if (orgId) {
      this.auditService.listAuditLog(orgId);
    }
  }
}
