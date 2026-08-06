import {
  REFRESH_TOKEN_KEY,
  TOKEN_KEY,
  USER_KEY,
  clearSessionStorage,
  readAccessToken,
  readRefreshToken,
  readUser,
  writeSession,
} from './auth-storage';
import { User } from '../models';

function user(): User {
  return {
    id: 'u1',
    orgId: 'o1',
    email: 'a@b.c',
    displayName: 'A',
    avatarUrl: null,
    role: 'owner',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('auth-storage', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => localStorage.clear());

  // Several files (auth service, both interceptors, SignalR) depend on these exact key names;
  // renaming one silently logs every existing session out.
  it('pins the localStorage key names', () => {
    expect(TOKEN_KEY).toBe('agenthost_token');
    expect(REFRESH_TOKEN_KEY).toBe('agenthost_refresh_token');
    expect(USER_KEY).toBe('agenthost_user');
  });

  it('returns null for every reader when nothing is stored', () => {
    expect(readAccessToken()).toBeNull();
    expect(readRefreshToken()).toBeNull();
    expect(readUser()).toBeNull();
  });

  it('writeSession stores token, refresh token and the serialized user under those keys', () => {
    writeSession('jwt', 'refresh', user());

    expect(localStorage.getItem(TOKEN_KEY)).toBe('jwt');
    expect(localStorage.getItem(REFRESH_TOKEN_KEY)).toBe('refresh');
    expect(JSON.parse(localStorage.getItem(USER_KEY) as string).id).toBe('u1');

    expect(readAccessToken()).toBe('jwt');
    expect(readRefreshToken()).toBe('refresh');
    expect(readUser()).toEqual(user());
  });

  it('writeSession with a null refresh token removes any previously stored one', () => {
    writeSession('jwt', 'refresh', user());
    writeSession('jwt2', null, user());

    expect(readAccessToken()).toBe('jwt2');
    expect(readRefreshToken()).toBeNull();
    expect(localStorage.getItem(REFRESH_TOKEN_KEY)).toBeNull();
  });

  it('readUser returns null on corrupt JSON rather than throwing', () => {
    localStorage.setItem(USER_KEY, '{not json');
    expect(() => readUser()).not.toThrow();
    expect(readUser()).toBeNull();
  });

  it('clearSessionStorage removes all three keys and leaves unrelated ones alone', () => {
    writeSession('jwt', 'refresh', user());
    localStorage.setItem('unrelated', 'keep-me');

    clearSessionStorage();

    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
    expect(localStorage.getItem(REFRESH_TOKEN_KEY)).toBeNull();
    expect(localStorage.getItem(USER_KEY)).toBeNull();
    expect(localStorage.getItem('unrelated')).toBe('keep-me');
  });

  it('round-trips a user with null optional fields', () => {
    const u = { ...user(), displayName: null, avatarUrl: null };
    writeSession('jwt', null, u);
    expect(readUser()).toEqual(u);
  });
});
