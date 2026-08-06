import { Injectable, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { MemoryUpdate, ProjectMemory, Run, RunEvent } from '../core/models';
import { readAccessToken } from '../core/utils/auth-storage';

export type ConnectionState = 'connected' | 'connecting' | 'disconnected';

/**
 * A hub connection plus the bookkeeping needed to hand callers a hub that is actually *connected*.
 *
 * `HubConnection.start()` is the only awaitable the SignalR client exposes, and it is legal only
 * from `Disconnected`. A connection sitting in `Connecting` (another caller's start still in
 * flight) or `Reconnecting` (automatic reconnect after a drop) offers nothing to await — so
 * returning it hands the caller a hub whose very next `invoke()` rejects with "Cannot send data if
 * the connection is not in the 'Connected' state". This record closes both gaps: concurrent
 * starters share one promise, and an in-flight automatic reconnect is awaited through the
 * lifecycle callbacks registered once at build time.
 */
class ManagedConnection {
  /** The start we have in flight, shared by every concurrent caller. */
  private starting: Promise<void> | null = null;

  /** Resolvers for callers parked on an automatic reconnect we do not drive. */
  private waiters: (() => void)[] = [];

  constructor(
    readonly hub: signalR.HubConnection,
    /** Optional hook so the RunHub can move its `connectionState` signal around a start. */
    private readonly notify?: (state: ConnectionState) => void,
  ) {}

  get state(): signalR.HubConnectionState {
    return this.hub.state;
  }

  /** Resolves once the hub is connected, whatever state it started from. */
  async ensureConnected(): Promise<signalR.HubConnection> {
    // An automatic reconnect is running. Wait for the callback that ends it (`onreconnected` or
    // `onclose`), then re-examine: the loop is what makes a reconnect that fails and retries safe.
    // The state check and the waiter registration are synchronous, so no settlement can slip
    // between them.
    while (this.hub.state === signalR.HubConnectionState.Reconnecting) {
      await this.settlement();
    }

    if (this.hub.state === signalR.HubConnectionState.Connected) {
      return this.hub;
    }

    this.starting ??= this.start().finally(() => (this.starting = null));
    await this.starting;
    return this.hub;
  }

  /** Releases everyone parked on a reconnect. Called from `onreconnected` and `onclose`. */
  releaseWaiters(): void {
    const waiters = this.waiters;
    this.waiters = [];
    waiters.forEach((resolve) => resolve());
  }

  private settlement(): Promise<void> {
    return new Promise<void>((resolve) => this.waiters.push(resolve));
  }

  private async start(): Promise<void> {
    // Guard rather than assume: `start()` throws outright on a connection that is not
    // Disconnected, and a failed start must leave the signal on 'disconnected', not 'connecting'.
    if (this.hub.state !== signalR.HubConnectionState.Disconnected) return;

    this.notify?.('connecting');
    try {
      await this.hub.start();
    } catch (err) {
      this.notify?.('disconnected');
      throw err;
    }
    this.notify?.('connected');
  }
}

/**
 * Wraps the three SignalR hubs (RunHub, ProjectHub, AgentMemoryHub) described in spec §10.
 * Each hub connection is created lazily and started on first use so pages that only
 * need one hub don't pay for the others.
 */
@Injectable({ providedIn: 'root' })
export class SignalRService {
  private runConnection: ManagedConnection | null = null;
  private projectConnection: ManagedConnection | null = null;
  private memoryConnection: ManagedConnection | null = null;

  /** Reflects the RunHub connection state (the hub used for live run streaming). */
  readonly connectionState = signal<ConnectionState>('disconnected');

  // --- RunHub streams ---
  private readonly runEventSubject = new Subject<RunEvent>();
  private readonly runStateSubject = new Subject<Run>();
  private readonly stepApprovedSubject = new Subject<{ stepId: string; timestamp: string }>();
  private readonly questionAnsweredSubject = new Subject<{ questionId: string; answer: string }>();
  private readonly runErrorSubject = new Subject<string>();

  readonly onRunEvent$ = this.runEventSubject.asObservable();
  readonly onRunState$ = this.runStateSubject.asObservable();
  readonly onStepApproved$ = this.stepApprovedSubject.asObservable();
  readonly onQuestionAnswered$ = this.questionAnsweredSubject.asObservable();
  readonly onRunError$ = this.runErrorSubject.asObservable();

  // --- ProjectHub streams ---
  private readonly projectMemorySubject = new Subject<ProjectMemory>();
  private readonly recentRunsSubject = new Subject<Run[]>();

  readonly onProjectMemory$ = this.projectMemorySubject.asObservable();
  readonly onRecentRuns$ = this.recentRunsSubject.asObservable();

  // --- AgentMemoryHub streams ---
  private readonly memoryLoadedSubject = new Subject<ProjectMemory>();
  private readonly memoryUpdatedSubject = new Subject<MemoryUpdate>();

  readonly onMemoryLoaded$ = this.memoryLoadedSubject.asObservable();
  readonly onMemoryUpdated$ = this.memoryUpdatedSubject.asObservable();

  // ===== RunHub =====

  /** Starts (or reuses) the RunHub connection. */
  async connect(): Promise<void> {
    await this.ensureRunConnection();
  }

  async disconnect(): Promise<void> {
    if (this.runConnection) {
      await this.runConnection.hub.stop();
      this.runConnection = null;
    }
    this.connectionState.set('disconnected');
  }

  async joinRun(runId: string): Promise<void> {
    const conn = await this.ensureRunConnection();
    await conn.invoke('JoinRun', runId);
  }

  async leaveRun(runId: string): Promise<void> {
    if (this.runConnection?.state === signalR.HubConnectionState.Connected) {
      await this.runConnection.hub.invoke('LeaveRun', runId);
    }
  }

  async approveStep(runId: string, stepId: string, note?: string): Promise<void> {
    const conn = await this.ensureRunConnection();
    await conn.invoke('ApproveStep', runId, stepId, note ?? null);
  }

  async answerQuestion(runId: string, questionId: string, answer: string): Promise<void> {
    const conn = await this.ensureRunConnection();
    await conn.invoke('AnswerQuestion', runId, questionId, answer);
  }

  private async ensureRunConnection(): Promise<signalR.HubConnection> {
    if (this.runConnection) {
      return this.runConnection.ensureConnected();
    }

    const conn = this.buildConnection('run');

    conn.on('connected', () => this.connectionState.set('connected'));
    conn.on('runState', (run: Run) => this.runStateSubject.next(run));
    conn.on('RunEvent', (evt: RunEvent) => this.runEventSubject.next(evt));
    conn.on('stepApproved', (data: { stepId: string; timestamp: string }) =>
      this.stepApprovedSubject.next(data),
    );
    conn.on('questionAnswered', (data: { questionId: string; answer: string }) =>
      this.questionAnsweredSubject.next(data),
    );
    conn.on('error', (msg: string) => this.runErrorSubject.next(msg));

    const managed = new ManagedConnection(conn, (state) => this.connectionState.set(state));

    conn.onreconnecting(() => this.connectionState.set('connecting'));
    conn.onreconnected(() => {
      this.connectionState.set('connected');
      managed.releaseWaiters();
    });
    conn.onclose(() => {
      this.connectionState.set('disconnected');
      managed.releaseWaiters();
    });

    this.runConnection = managed;
    return managed.ensureConnected();
  }

  // ===== ProjectHub =====

  async joinProject(projectId: string): Promise<void> {
    const conn = await this.ensureProjectConnection();
    await conn.invoke('JoinProject', projectId);
  }

  async leaveProject(projectId: string): Promise<void> {
    if (this.projectConnection?.state === signalR.HubConnectionState.Connected) {
      await this.projectConnection.hub.invoke('LeaveProject', projectId);
    }
  }

  async disconnectProject(): Promise<void> {
    if (this.projectConnection) {
      await this.projectConnection.hub.stop();
      this.projectConnection = null;
    }
  }

  private async ensureProjectConnection(): Promise<signalR.HubConnection> {
    if (this.projectConnection) {
      return this.projectConnection.ensureConnected();
    }

    const conn = this.buildConnection('project');

    conn.on('projectMemory', (memory: ProjectMemory) => this.projectMemorySubject.next(memory));
    conn.on('recentRuns', (runs: Run[]) => this.recentRunsSubject.next(runs));

    this.projectConnection = this.manage(conn);
    return this.projectConnection.ensureConnected();
  }

  // ===== AgentMemoryHub =====

  async joinProjectMemory(projectId: string): Promise<void> {
    const conn = await this.ensureMemoryConnection();
    await conn.invoke('JoinProjectMemory', projectId);
  }

  /**
   * Leaves a project's memory group. Like `leaveRun`/`leaveProject` this is deliberately silent
   * when there is no live connection: there is no group membership left to give up.
   */
  async leaveProjectMemory(projectId: string): Promise<void> {
    if (this.memoryConnection?.state === signalR.HubConnectionState.Connected) {
      await this.memoryConnection.hub.invoke('LeaveProjectMemory', projectId);
    }
  }

  async updateMemory(projectId: string, update: MemoryUpdate): Promise<void> {
    const conn = await this.ensureMemoryConnection();
    await conn.invoke('UpdateMemory', projectId, update);
  }

  async disconnectMemory(): Promise<void> {
    if (this.memoryConnection) {
      await this.memoryConnection.hub.stop();
      this.memoryConnection = null;
    }
  }

  private async ensureMemoryConnection(): Promise<signalR.HubConnection> {
    if (this.memoryConnection) {
      return this.memoryConnection.ensureConnected();
    }

    const conn = this.buildConnection('memory');

    conn.on('memoryLoaded', (memory: ProjectMemory) => this.memoryLoadedSubject.next(memory));
    conn.on('memoryUpdated', (update: MemoryUpdate) => this.memoryUpdatedSubject.next(update));

    this.memoryConnection = this.manage(conn);
    return this.memoryConnection.ensureConnected();
  }

  // ===== shared plumbing =====

  private buildConnection(hub: 'run' | 'project' | 'memory'): signalR.HubConnection {
    return new signalR.HubConnectionBuilder()
      .withUrl(`${environment.apiUrl}/hubs/${hub}`, {
        // The hubs are behind RequireAuthorization(); WebSocket handshakes can't carry an
        // Authorization header, so the server reads ?access_token= (Program.cs OnMessageReceived).
        // Read it lazily on every (re)connect so a refreshed token is picked up.
        accessTokenFactory: () => readAccessToken() ?? '',
      })
      .withAutomaticReconnect()
      .build();
  }

  /**
   * Wraps a hub that has no state signal of its own, wiring the reconnect callbacks that release
   * callers parked in {@link ManagedConnection.ensureConnected}.
   */
  private manage(conn: signalR.HubConnection): ManagedConnection {
    const managed = new ManagedConnection(conn);
    conn.onreconnected(() => managed.releaseWaiters());
    conn.onclose(() => managed.releaseWaiters());
    return managed;
  }
}
