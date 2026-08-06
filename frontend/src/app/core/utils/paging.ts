/**
 * Client-side mirror of the server's `Paging` helper (backend Infrastructure/Paging.cs).
 *
 * The server clamps silently: ask for `take=1000` and you get 200 back with nothing saying so.
 * A caller that then advanced by 1000 would skip 800 rows it never saw. Clamping here keeps the
 * page size the client *thinks* it requested equal to the one it will get, so paging arithmetic
 * stays correct. Both bounds must move together — this file exists to make that obvious.
 */

/** Hard upper bound on any single page, matching `Paging.MaxTake`. */
export const MAX_TAKE = 200;

/** Clamps a requested page size into [1, max]. */
export function clampTake(take: number, max: number = MAX_TAKE): number {
  const ceiling = Math.min(max, MAX_TAKE);
  if (!Number.isFinite(take)) return ceiling;
  const size = Math.floor(take);
  if (size < 1) return 1;
  return size > ceiling ? ceiling : size;
}

/** Floors a requested offset at 0 — a negative OFFSET is a Postgres error. */
export function clampSkip(skip: number): number {
  if (!Number.isFinite(skip)) return 0;
  const offset = Math.floor(skip);
  return offset < 0 ? 0 : offset;
}
