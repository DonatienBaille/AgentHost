import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuditEntryView, AuditFacets, AuditFilters, AuditPage } from '../core/models';
import { clampSkip, clampTake } from '../core/utils/paging';

/** Taille de page. Un journal se lit, il ne se télécharge pas. */
export const AUDIT_PAGE_SIZE = 50;

const DAY_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

/**
 * Consultation du journal d'audit (feuille de route, lot 3).
 *
 * <b>Les filtres partent au serveur, pas au navigateur.</b> Filtrer côté client supposerait d'avoir
 * chargé le journal entier, ce qui n'a de sens que sur un journal qu'on n'a pas besoin de filtrer.
 * Le total renvoyé porte donc sur le jeu filtré, et non sur ce que la page contient.
 */
@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);

  readonly entries = signal<AuditEntryView[]>([]);
  readonly total = signal(0);
  readonly skip = signal(0);
  readonly take = signal(AUDIT_PAGE_SIZE);

  readonly facets = signal<AuditFacets | null>(null);

  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  /** Y a-t-il quelque chose après cette page ? Question à laquelle seul le total répond. */
  readonly hasMore = computed(() => this.skip() + this.entries().length < this.total());

  async search(orgId: string, filters: AuditFilters = {}, skip = 0): Promise<void> {
    this.isLoading.set(true);
    try {
      const page = await firstValueFrom(
        this.http.get<AuditPage>(`${environment.apiUrl}/api/organizations/${orgId}/audit-log`, {
          params: AuditService.toParams(filters, skip),
        }),
      );

      this.entries.set(page?.items ?? []);
      this.total.set(page?.total ?? 0);
      // On adopte la pagination que le serveur a réellement appliquée : c'est lui qui la borne.
      this.skip.set(page?.skip ?? 0);
      this.take.set(page?.take ?? AUDIT_PAGE_SIZE);
      this.error.set(null);
    } catch {
      this.error.set('errors.loadAuditLog');
    } finally {
      this.isLoading.set(false);
    }
  }

  /**
   * Les valeurs présentes dans le journal, pour peupler les listes de filtres.
   *
   * Un échec ici n'est pas un échec de la page : sans facettes, les listes déroulantes sont vides
   * mais le journal reste consultable. On ne remonte donc pas d'erreur — la faire apparaître
   * laisserait croire que le journal n'a pas pu être lu, alors qu'il est à l'écran.
   */
  async loadFacets(orgId: string): Promise<void> {
    try {
      const facets = await firstValueFrom(
        this.http.get<AuditFacets>(`${environment.apiUrl}/api/organizations/${orgId}/audit-log/facets`),
      );
      this.facets.set(facets ?? null);
    } catch {
      this.facets.set(null);
    }
  }

  private static toParams(filters: AuditFilters, skip: number): HttpParams {
    let params = new HttpParams()
      .set('skip', clampSkip(skip))
      .set('take', clampTake(AUDIT_PAGE_SIZE));

    for (const key of ['action', 'actorUserId', 'resourceType', 'resourceId'] as const) {
      const value = filters[key];
      if (value) params = params.set(key, value);
    }

    // Les deux bornes de l'IHM sont inclusives — « du 3 au 5 » contient le 5 — alors que l'API
    // exclut la sienne. La conversion se fait ici, une fois, plutôt que dans la tête de
    // l'utilisateur : une borne haute demandée au 5 part en 6 à minuit UTC.
    const from = AuditService.dayStart(filters.from);
    if (from) params = params.set('from', from);

    const to = AuditService.dayAfter(filters.to);
    if (to) params = params.set('to', to);

    return params;
  }

  /** `2031-03-03` → `2031-03-03T00:00:00Z`. */
  private static dayStart(day?: string): string | null {
    return day && DAY_PATTERN.test(day) ? `${day}T00:00:00Z` : null;
  }

  /**
   * `2031-03-03` → `2031-03-04T00:00:00Z`, la borne haute exclue qui rend le 3 inclus.
   *
   * Construite via `Date.UTC`, jamais via `new Date('2031-03-03')` suivi d'une arithmétique locale :
   * dans un fuseau à l'ouest de Greenwich, la seconde forme décale le résultat d'un jour et fait
   * disparaître les entrées de la dernière journée demandée.
   */
  private static dayAfter(day?: string): string | null {
    if (!day || !DAY_PATTERN.test(day)) return null;
    const [year, month, dayOfMonth] = day.split('-').map(Number);
    // Date.UTC normalise le débordement : le 32 mars devient le 1er avril.
    const next = new Date(Date.UTC(year, month - 1, dayOfMonth + 1));
    return `${next.toISOString().slice(0, 10)}T00:00:00Z`;
  }
}
