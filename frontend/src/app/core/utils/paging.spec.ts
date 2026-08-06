import { MAX_TAKE, clampSkip, clampTake } from './paging';

/**
 * These bounds must stay identical to the server's `Paging` helper. If MAX_TAKE ever diverges from
 * `Paging.MaxTake`, the client resumes over-asking and paging arithmetic silently skips rows.
 */
describe('paging', () => {
  it('mirrors the server cap', () => {
    expect(MAX_TAKE).toBe(200);
  });

  describe('clampTake', () => {
    it('passes a page size inside the bounds through untouched', () => {
      expect(clampTake(1)).toBe(1);
      expect(clampTake(50)).toBe(50);
      expect(clampTake(200)).toBe(200);
    });

    it('caps anything above the maximum', () => {
      expect(clampTake(201)).toBe(200);
      expect(clampTake(1000)).toBe(200);
      expect(clampTake(Number.MAX_SAFE_INTEGER)).toBe(200);
    });

    it('raises a non-positive page size to 1 — an empty page is never what was meant', () => {
      expect(clampTake(0)).toBe(1);
      expect(clampTake(-1)).toBe(1);
    });

    it('honours a caller-supplied maximum, but never above the hard cap', () => {
      expect(clampTake(50, 25)).toBe(25);
      expect(clampTake(1000, 500)).toBe(200);
    });

    it('floors fractional sizes rather than sending them over the wire', () => {
      expect(clampTake(10.9)).toBe(10);
      expect(clampTake(0.5)).toBe(1);
    });

    it('falls back to the ceiling on a non-finite size', () => {
      expect(clampTake(Number.NaN)).toBe(200);
      expect(clampTake(Number.POSITIVE_INFINITY)).toBe(200);
    });
  });

  describe('clampSkip', () => {
    it('passes a non-negative offset through', () => {
      expect(clampSkip(0)).toBe(0);
      expect(clampSkip(100)).toBe(100);
    });

    it('floors a negative offset at 0 — Postgres rejects a negative OFFSET', () => {
      expect(clampSkip(-1)).toBe(0);
      expect(clampSkip(-1000)).toBe(0);
    });

    it('floors fractions and non-finite values', () => {
      expect(clampSkip(10.9)).toBe(10);
      expect(clampSkip(Number.NaN)).toBe(0);
    });
  });
});
