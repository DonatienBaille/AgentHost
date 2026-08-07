import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { AuditService } from '../../../services/audit.service';
import { AuthService } from '../../../services/auth.service';
import { AuditEntryView, AuditFilters } from '../../../core/models';

/**
 * Le journal d'audit, consultable (feuille de route, lot 3).
 *
 * <b>Ce qu'il était.</b> Une table brute paginée : cent lignes, quatre colonnes, un ULID en guise
 * d'acteur, aucun filtre et aucun total. Au-delà de quelques centaines d'entrées, répondre à « qui
 * a changé le rôle de cette personne, et quand » revenait à feuilleter. Un journal d'audit qu'on ne
 * sait pas interroger ne remplit pas la fonction pour laquelle il existe — on ne l'ouvre pas par
 * curiosité, on l'ouvre parce que quelque chose s'est produit.
 *
 * <b>Les filtres viennent du serveur, pas d'une liste écrite en dur.</b> Les actions forment un
 * vocabulaire fermé côté backend et nulle part documenté ici ; une liste recopiée à la main
 * divergerait au premier ajout, silencieusement. Les facettes disent ce que CE journal contient,
 * avec le volume derrière chaque valeur.
 *
 * <b>Les charges JSONB ne sont pas dans le tableau.</b> `changes` et `details` s'affichent au
 * déploiement d'une ligne : le tableau reste lisible, et rien de leur contenu n'est projeté dans le
 * DOM tant que personne ne l'a demandé.
 */
@Component({
  selector: 'app-admin-audit-log',
  standalone: true,
  imports: [DatePipe, ReactiveFormsModule, TranslatePipe],
  templateUrl: './audit-log.component.html',
  styleUrls: ['./audit-log.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuditLogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly auditService = inject(AuditService);
  private readonly authService = inject(AuthService);

  readonly entries = this.auditService.entries;
  readonly total = this.auditService.total;
  readonly skip = this.auditService.skip;
  readonly take = this.auditService.take;
  readonly facets = this.auditService.facets;
  readonly isLoading = this.auditService.isLoading;
  readonly error = this.auditService.error;
  readonly hasMore = this.auditService.hasMore;

  /**
   * Le serveur exige `maintainer` sur GET /api/organizations/{orgId}/audit-log et sur ses facettes
   * (backend Endpoints/AuditEndpoints.cs). La page était rendue à l'identique aux quatre rôles :
   * un `developer` ou un `viewer` déclenchait un appel voué au 403, affiché comme une erreur de
   * chargement générique — « ça n'a pas marché » là où la vraie réponse est « ce n'est pas pour
   * vous ». On ne demande donc pas ce qu'on n'a pas le droit de lire.
   */
  readonly canView = this.authService.isMaintainerOrAbove;

  readonly filterForm = this.fb.group({
    action: [''],
    actorUserId: [''],
    resourceType: [''],
    from: [''],
    to: [''],
  });

  /**
   * Les filtres réellement appliqués, distincts de ce que le formulaire affiche.
   *
   * Sans cette séparation, changer de page rejouerait des critères saisis mais pas validés : on
   * demanderait la page 2 d'une autre requête que celle qu'on est en train de lire.
   */
  private readonly appliedFilters = signal<AuditFilters>({});

  /** Ligne dépliée, s'il y en a une. Une seule à la fois : deux JSON côte à côte ne se lisent pas. */
  readonly expandedId = signal<string | null>(null);

  readonly hasFilters = computed(() => Object.keys(this.appliedFilters()).length > 0);

  /** Rang de la première et de la dernière entrée affichées, pour situer la page dans le total. */
  readonly rangeStart = computed(() => (this.entries().length === 0 ? 0 : this.skip() + 1));
  readonly rangeEnd = computed(() => this.skip() + this.entries().length);

  private orgId(): string | null {
    return this.authService.currentUser()?.orgId || null;
  }

  ngOnInit(): void {
    if (!this.canView()) return;

    const orgId = this.orgId();
    if (!orgId) return;

    // Les deux en parallèle : les facettes ne conditionnent pas l'affichage du journal, elles ne
    // font que peupler les listes de filtres.
    void this.auditService.loadFacets(orgId);
    void this.auditService.search(orgId, {});
  }

  /** Applique le formulaire et repart de la première page : la page 3 d'un autre filtre n'existe pas. */
  async applyFilters(): Promise<void> {
    const filters = AuditLogComponent.toFilters(this.filterForm.getRawValue());
    this.appliedFilters.set(filters);
    this.expandedId.set(null);
    await this.reload(0);
  }

  async resetFilters(): Promise<void> {
    this.filterForm.reset({ action: '', actorUserId: '', resourceType: '', from: '', to: '' });
    await this.applyFilters();
  }

  /** Rebondir d'une entrée vers tout ce que son acteur a fait — c'est ça, consulter un journal. */
  async filterByActor(entry: AuditEntryView): Promise<void> {
    if (!entry.actorUserId) return;
    this.filterForm.patchValue({ actorUserId: entry.actorUserId });
    await this.applyFilters();
  }

  /** Idem vers l'historique complet d'un objet, type et identifiant ensemble. */
  async filterByResource(entry: AuditEntryView): Promise<void> {
    if (!entry.resourceType) return;
    this.filterForm.patchValue({ resourceType: entry.resourceType });
    const filters = {
      ...AuditLogComponent.toFilters(this.filterForm.getRawValue()),
      // `resourceId` n'a pas de champ dans la barre de filtres : aucune liste ne peut l'énumérer,
      // et le saisir à la main n'aurait aucun sens. Il ne s'obtient donc que par ce rebond.
      ...(entry.resourceId ? { resourceId: entry.resourceId } : {}),
    };
    this.appliedFilters.set(filters);
    this.expandedId.set(null);
    await this.reload(0);
  }

  async nextPage(): Promise<void> {
    if (!this.hasMore()) return;
    await this.reload(this.skip() + this.take());
  }

  async previousPage(): Promise<void> {
    if (this.skip() === 0) return;
    await this.reload(Math.max(0, this.skip() - this.take()));
  }

  async refresh(): Promise<void> {
    await this.reload(this.skip());
  }

  toggleDetail(entry: AuditEntryView): void {
    this.expandedId.set(this.expandedId() === entry.id ? null : entry.id);
  }

  hasPayload(entry: AuditEntryView): boolean {
    return entry.changes !== null || entry.details !== null;
  }

  /** Le nom qu'on affiche pour un acteur, du plus parlant au moins parlant. */
  actorLabel(entry: AuditEntryView): string {
    return entry.actorDisplayName || entry.actorEmail || entry.actorUserId || '—';
  }

  /** Une charge JSONB rendue lisible. Deux espaces : la profondeur dépasse rarement trois niveaux. */
  pretty(value: unknown): string {
    return JSON.stringify(value, null, 2);
  }

  /** Étiquette d'un acteur dans la liste de filtres : la même règle, plus son volume. */
  actorFacetLabel(actor: { displayName: string | null; email: string | null; userId: string }): string {
    return actor.displayName || actor.email || actor.userId;
  }

  private async reload(skip: number): Promise<void> {
    const orgId = this.orgId();
    if (!orgId) return;
    await this.auditService.search(orgId, this.appliedFilters(), skip);
  }

  /**
   * Ne retient que les critères renseignés.
   *
   * Un champ vide n'est pas un filtre sur la chaîne vide : le laisser passer demanderait au serveur
   * les entrées dont l'action est `''`, c'est-à-dire aucune.
   */
  private static toFilters(value: Record<string, string | null>): AuditFilters {
    const filters: AuditFilters = {};
    for (const [key, raw] of Object.entries(value)) {
      const trimmed = raw?.trim();
      if (trimmed) filters[key as keyof AuditFilters] = trimmed;
    }
    return filters;
  }
}
