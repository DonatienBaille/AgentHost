import { TestBed } from '@angular/core/testing';
import { SignalRService } from './signalr.service';
import { TOKEN_KEY } from '../core/utils/auth-storage';
import { environment } from '../../environments/environment';

/**
 * The hubs are mocked wholesale: nothing here talks to a live server. What is worth pinning at
 * this level is the wiring — which hub URL is built, that the access token is supplied lazily,
 * how connections are reused, which hub methods join/leave invoke, and how the connectionState
 * signal moves. Behaviour that only appears against a real hub (transport negotiation, automatic
 * reconnect timing, server-side group semantics) is left to the E2E wave.
 */
const hoisted = vi.hoisted(() => ({ connections: [] as FakeConnection[] }));

interface FakeConnection {
  url: string;
  options: { accessTokenFactory?: () => string };
  state: string;
  startCount: number;
  stopCount: number;
  invocations: { method: string; args: unknown[] }[];
  emit(event: string, payload?: unknown): void;
  fireReconnecting(): void;
  fireReconnected(): void;
  fireClose(): void;
}

vi.mock('@microsoft/signalr', () => {
  const HubConnectionState = {
    Disconnected: 'Disconnected',
    Connecting: 'Connecting',
    Connected: 'Connected',
    Disconnecting: 'Disconnecting',
    Reconnecting: 'Reconnecting',
  };

  class Fake {
    state = HubConnectionState.Disconnected;
    startCount = 0;
    stopCount = 0;
    invocations: { method: string; args: unknown[] }[] = [];
    private handlers = new Map<string, ((payload?: unknown) => void)[]>();
    private reconnecting: (() => void)[] = [];
    private reconnected: (() => void)[] = [];
    private closed: (() => void)[] = [];

    constructor(
      public url: string,
      public options: { accessTokenFactory?: () => string },
    ) {
      hoisted.connections.push(this as unknown as FakeConnection);
    }

    on(event: string, cb: (payload?: unknown) => void): void {
      this.handlers.set(event, [...(this.handlers.get(event) ?? []), cb]);
    }
    onreconnecting(cb: () => void): void {
      this.reconnecting.push(cb);
    }
    onreconnected(cb: () => void): void {
      this.reconnected.push(cb);
    }
    onclose(cb: () => void): void {
      this.closed.push(cb);
    }
    async start(): Promise<void> {
      this.startCount++;
      this.state = HubConnectionState.Connected;
    }
    async stop(): Promise<void> {
      this.stopCount++;
      this.state = HubConnectionState.Disconnected;
    }
    async invoke(method: string, ...args: unknown[]): Promise<void> {
      this.invocations.push({ method, args });
    }

    emit(event: string, payload?: unknown): void {
      (this.handlers.get(event) ?? []).forEach((cb) => cb(payload));
    }
    fireReconnecting(): void {
      this.state = HubConnectionState.Reconnecting;
      this.reconnecting.forEach((cb) => cb());
    }
    fireReconnected(): void {
      this.state = HubConnectionState.Connected;
      this.reconnected.forEach((cb) => cb());
    }
    fireClose(): void {
      this.state = HubConnectionState.Disconnected;
      this.closed.forEach((cb) => cb());
    }
  }

  class HubConnectionBuilder {
    private url = '';
    private options: { accessTokenFactory?: () => string } = {};
    withUrl(url: string, options: { accessTokenFactory?: () => string }): this {
      this.url = url;
      this.options = options;
      return this;
    }
    withAutomaticReconnect(): this {
      return this;
    }
    build(): Fake {
      return new Fake(this.url, this.options);
    }
  }

  return { HubConnectionBuilder, HubConnectionState };
});

function conns(): FakeConnection[] {
  return hoisted.connections;
}

describe('SignalRService', () => {
  let service: SignalRService;

  beforeEach(() => {
    hoisted.connections.length = 0;
    localStorage.clear();
    TestBed.configureTestingModule({});
    service = TestBed.inject(SignalRService);
  });

  afterEach(() => {
    hoisted.connections.length = 0;
    localStorage.clear();
  });

  describe('RunHub', () => {
    it('starts disconnected and reaches connected after connect()', async () => {
      expect(service.connectionState()).toBe('disconnected');

      await service.connect();

      expect(conns().length).toBe(1);
      expect(conns()[0].url).toBe(`${environment.apiUrl}/hubs/run`);
      expect(conns()[0].startCount).toBe(1);
      expect(service.connectionState()).toBe('connected');
    });

    it('supplies the stored access token lazily, re-reading it on each call', async () => {
      localStorage.setItem(TOKEN_KEY, 'jwt-1');
      await service.connect();

      const factory = conns()[0].options.accessTokenFactory as () => string;
      expect(factory()).toBe('jwt-1');

      // A refreshed token must be picked up without rebuilding the connection.
      localStorage.setItem(TOKEN_KEY, 'jwt-2');
      expect(factory()).toBe('jwt-2');
    });

    it('supplies an empty string rather than null when no token is stored', async () => {
      await service.connect();
      const factory = conns()[0].options.accessTokenFactory as () => string;
      expect(factory()).toBe('');
    });

    it('reuses the existing connection instead of building a second one', async () => {
      await service.connect();
      await service.connect();
      await service.joinRun('r1');

      expect(conns().length).toBe(1);
      expect(conns()[0].startCount).toBe(1);
    });

    it('joinRun connects on demand and invokes JoinRun', async () => {
      await service.joinRun('r1');

      expect(conns().length).toBe(1);
      expect(conns()[0].invocations).toEqual([{ method: 'JoinRun', args: ['r1'] }]);
      expect(service.connectionState()).toBe('connected');
    });

    it('leaveRun invokes LeaveRun only when connected', async () => {
      // Never connected: no connection is built and nothing is invoked.
      await service.leaveRun('r1');
      expect(conns().length).toBe(0);

      await service.joinRun('r1');
      await service.leaveRun('r1');
      expect(conns()[0].invocations.map((i) => i.method)).toEqual(['JoinRun', 'LeaveRun']);
    });

    it('does not invoke LeaveRun after the connection has been stopped', async () => {
      await service.joinRun('r1');
      await service.disconnect();
      await service.leaveRun('r1');

      expect(conns()[0].invocations.map((i) => i.method)).toEqual(['JoinRun']);
    });

    it('approveStep and answerQuestion invoke the hub methods with their arguments', async () => {
      await service.approveStep('r1', 's1');
      await service.approveStep('r1', 's2', 'looks good');
      await service.answerQuestion('r1', 'q1', '42');

      expect(conns()[0].invocations).toEqual([
        { method: 'ApproveStep', args: ['r1', 's1', null] },
        { method: 'ApproveStep', args: ['r1', 's2', 'looks good'] },
        { method: 'AnswerQuestion', args: ['r1', 'q1', '42'] },
      ]);
    });

    it('disconnect stops the hub, drops it and resets the state signal', async () => {
      await service.connect();
      await service.disconnect();

      expect(conns()[0].stopCount).toBe(1);
      expect(service.connectionState()).toBe('disconnected');

      // The next connect builds a fresh connection.
      await service.connect();
      expect(conns().length).toBe(2);
    });

    it('disconnect is a no-op when never connected', async () => {
      await service.disconnect();
      expect(conns().length).toBe(0);
      expect(service.connectionState()).toBe('disconnected');
    });

    it('tracks reconnecting / reconnected / close on the state signal', async () => {
      await service.connect();
      const conn = conns()[0];

      conn.fireReconnecting();
      expect(service.connectionState()).toBe('connecting');

      conn.fireReconnected();
      expect(service.connectionState()).toBe('connected');

      conn.fireClose();
      expect(service.connectionState()).toBe('disconnected');
    });

    it('restarts a connection the server had closed', async () => {
      await service.connect();
      const conn = conns()[0];
      conn.fireClose();

      await service.joinRun('r1');

      expect(conns().length).toBe(1);
      expect(conn.startCount).toBe(2);
      expect(service.connectionState()).toBe('connected');
    });

    it('forwards hub callbacks onto the run streams', async () => {
      const events: unknown[] = [];
      const states: unknown[] = [];
      const approved: unknown[] = [];
      const answered: unknown[] = [];
      const errors: unknown[] = [];
      service.onRunEvent$.subscribe((e) => events.push(e));
      service.onRunState$.subscribe((s) => states.push(s));
      service.onStepApproved$.subscribe((a) => approved.push(a));
      service.onQuestionAnswered$.subscribe((a) => answered.push(a));
      service.onRunError$.subscribe((e) => errors.push(e));

      await service.connect();
      const conn = conns()[0];
      conn.emit('RunEvent', { seq: 1 });
      conn.emit('runState', { id: 'r1' });
      conn.emit('stepApproved', { stepId: 's1', timestamp: 't' });
      conn.emit('questionAnswered', { questionId: 'q1', answer: '42' });
      conn.emit('error', 'boom');

      expect(events).toEqual([{ seq: 1 }]);
      expect(states).toEqual([{ id: 'r1' }]);
      expect(approved).toEqual([{ stepId: 's1', timestamp: 't' }]);
      expect(answered).toEqual([{ questionId: 'q1', answer: '42' }]);
      expect(errors).toEqual(['boom']);
    });

    it('the connected hub callback also sets the state signal', async () => {
      await service.connect();
      const conn = conns()[0];
      conn.fireClose();
      expect(service.connectionState()).toBe('disconnected');

      conn.emit('connected');
      expect(service.connectionState()).toBe('connected');
    });
  });

  describe('ProjectHub', () => {
    it('builds its own connection on the project hub URL and joins', async () => {
      await service.joinProject('p1');

      expect(conns().length).toBe(1);
      expect(conns()[0].url).toBe(`${environment.apiUrl}/hubs/project`);
      expect(conns()[0].invocations).toEqual([{ method: 'JoinProject', args: ['p1'] }]);
    });

    it('is independent of the RunHub connection and its state signal', async () => {
      await service.joinProject('p1');
      // The project hub must not move the RunHub-scoped connectionState signal.
      expect(service.connectionState()).toBe('disconnected');

      await service.connect();
      expect(conns().length).toBe(2);
      expect(service.connectionState()).toBe('connected');
    });

    it('leaveProject invokes only when connected, and disconnectProject stops the hub', async () => {
      await service.leaveProject('p1');
      expect(conns().length).toBe(0);

      await service.joinProject('p1');
      await service.leaveProject('p1');
      expect(conns()[0].invocations.map((i) => i.method)).toEqual(['JoinProject', 'LeaveProject']);

      await service.disconnectProject();
      expect(conns()[0].stopCount).toBe(1);

      await service.joinProject('p1');
      expect(conns().length).toBe(2);
    });

    it('forwards projectMemory and recentRuns onto their streams', async () => {
      const memories: unknown[] = [];
      const runs: unknown[] = [];
      service.onProjectMemory$.subscribe((m) => memories.push(m));
      service.onRecentRuns$.subscribe((r) => runs.push(r));

      await service.joinProject('p1');
      conns()[0].emit('projectMemory', { projectId: 'p1' });
      conns()[0].emit('recentRuns', [{ id: 'r1' }]);

      expect(memories).toEqual([{ projectId: 'p1' }]);
      expect(runs).toEqual([[{ id: 'r1' }]]);
    });
  });

  describe('AgentMemoryHub', () => {
    it('builds its own connection on the memory hub URL and joins', async () => {
      await service.joinProjectMemory('p1');

      expect(conns().length).toBe(1);
      expect(conns()[0].url).toBe(`${environment.apiUrl}/hubs/memory`);
      expect(conns()[0].invocations).toEqual([{ method: 'JoinProjectMemory', args: ['p1'] }]);
    });

    it('updateMemory invokes UpdateMemory with the project id and payload', async () => {
      await service.joinProjectMemory('p1');
      await service.updateMemory('p1', { note: { text: 'hello' } });

      expect(conns()[0].invocations[1]).toEqual({
        method: 'UpdateMemory',
        args: ['p1', { note: { text: 'hello' } }],
      });
    });

    it('reuses one memory connection across joins and reconnects it after disconnect', async () => {
      await service.joinProjectMemory('p1');
      await service.joinProjectMemory('p2');
      expect(conns().length).toBe(1);
      expect(conns()[0].startCount).toBe(1);

      await service.disconnectMemory();
      expect(conns()[0].stopCount).toBe(1);

      await service.joinProjectMemory('p1');
      expect(conns().length).toBe(2);
    });

    it('forwards memoryLoaded and memoryUpdated onto their streams', async () => {
      const loaded: unknown[] = [];
      const updated: unknown[] = [];
      service.onMemoryLoaded$.subscribe((m) => loaded.push(m));
      service.onMemoryUpdated$.subscribe((u) => updated.push(u));

      await service.joinProjectMemory('p1');
      conns()[0].emit('memoryLoaded', { projectId: 'p1' });
      conns()[0].emit('memoryUpdated', { note: { text: 'x' } });

      expect(loaded).toEqual([{ projectId: 'p1' }]);
      expect(updated).toEqual([{ note: { text: 'x' } }]);
    });
  });
});
