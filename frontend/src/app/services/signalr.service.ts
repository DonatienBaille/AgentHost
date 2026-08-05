import { Injectable, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { MemoryUpdate, ProjectMemory, Run, RunEvent } from '../core/models';
import { readAccessToken } from '../core/utils/auth-storage';

export type ConnectionState = 'connected' | 'connecting' | 'disconnected';

/**
 * Wraps the three SignalR hubs (RunHub, ProjectHub, AgentMemoryHub) described in spec §10.
 * Each hub connection is created lazily and started on first use so pages that only
 * need one hub don't pay for the others.
 */
@Injectable({ providedIn: 'root' })
export class SignalRService {
  private runConnection: signalR.HubConnection | null = null;
  private projectConnection: signalR.HubConnection | null = null;
  private memoryConnection: signalR.HubConnection | null = null;

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
      await this.runConnection.stop();
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
      await this.runConnection.invoke('LeaveRun', runId);
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
      if (this.runConnection.state === signalR.HubConnectionState.Connected) {
        return this.runConnection;
      }
      if (this.runConnection.state === signalR.HubConnectionState.Disconnected) {
        this.connectionState.set('connecting');
        await this.runConnection.start();
        this.connectionState.set('connected');
      }
      return this.runConnection;
    }

    const conn = new signalR.HubConnectionBuilder()
      .withUrl(`${environment.apiUrl}/hubs/run`, {
        // The hubs are behind RequireAuthorization(); WebSocket handshakes can't carry an
        // Authorization header, so the server reads ?access_token= (Program.cs OnMessageReceived).
        // Read it lazily on every (re)connect so a refreshed token is picked up.
        accessTokenFactory: () => readAccessToken() ?? '',
      })
      .withAutomaticReconnect()
      .build();

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

    conn.onreconnecting(() => this.connectionState.set('connecting'));
    conn.onreconnected(() => this.connectionState.set('connected'));
    conn.onclose(() => this.connectionState.set('disconnected'));

    this.runConnection = conn;
    this.connectionState.set('connecting');
    await conn.start();
    this.connectionState.set('connected');
    return conn;
  }

  // ===== ProjectHub =====

  async joinProject(projectId: string): Promise<void> {
    const conn = await this.ensureProjectConnection();
    await conn.invoke('JoinProject', projectId);
  }

  async leaveProject(projectId: string): Promise<void> {
    if (this.projectConnection?.state === signalR.HubConnectionState.Connected) {
      await this.projectConnection.invoke('LeaveProject', projectId);
    }
  }

  async disconnectProject(): Promise<void> {
    if (this.projectConnection) {
      await this.projectConnection.stop();
      this.projectConnection = null;
    }
  }

  private async ensureProjectConnection(): Promise<signalR.HubConnection> {
    if (this.projectConnection) {
      if (this.projectConnection.state === signalR.HubConnectionState.Disconnected) {
        await this.projectConnection.start();
      }
      return this.projectConnection;
    }

    const conn = new signalR.HubConnectionBuilder()
      .withUrl(`${environment.apiUrl}/hubs/project`, {
        // The hubs are behind RequireAuthorization(); WebSocket handshakes can't carry an
        // Authorization header, so the server reads ?access_token= (Program.cs OnMessageReceived).
        // Read it lazily on every (re)connect so a refreshed token is picked up.
        accessTokenFactory: () => readAccessToken() ?? '',
      })
      .withAutomaticReconnect()
      .build();

    conn.on('projectMemory', (memory: ProjectMemory) => this.projectMemorySubject.next(memory));
    conn.on('recentRuns', (runs: Run[]) => this.recentRunsSubject.next(runs));

    this.projectConnection = conn;
    await conn.start();
    return conn;
  }

  // ===== AgentMemoryHub =====

  async joinProjectMemory(projectId: string): Promise<void> {
    const conn = await this.ensureMemoryConnection();
    await conn.invoke('JoinProjectMemory', projectId);
  }

  async updateMemory(projectId: string, update: MemoryUpdate): Promise<void> {
    const conn = await this.ensureMemoryConnection();
    await conn.invoke('UpdateMemory', projectId, update);
  }

  async disconnectMemory(): Promise<void> {
    if (this.memoryConnection) {
      await this.memoryConnection.stop();
      this.memoryConnection = null;
    }
  }

  private async ensureMemoryConnection(): Promise<signalR.HubConnection> {
    if (this.memoryConnection) {
      if (this.memoryConnection.state === signalR.HubConnectionState.Disconnected) {
        await this.memoryConnection.start();
      }
      return this.memoryConnection;
    }

    const conn = new signalR.HubConnectionBuilder()
      .withUrl(`${environment.apiUrl}/hubs/memory`, {
        // The hubs are behind RequireAuthorization(); WebSocket handshakes can't carry an
        // Authorization header, so the server reads ?access_token= (Program.cs OnMessageReceived).
        // Read it lazily on every (re)connect so a refreshed token is picked up.
        accessTokenFactory: () => readAccessToken() ?? '',
      })
      .withAutomaticReconnect()
      .build();

    conn.on('memoryLoaded', (memory: ProjectMemory) => this.memoryLoadedSubject.next(memory));
    conn.on('memoryUpdated', (update: MemoryUpdate) => this.memoryUpdatedSubject.next(update));

    this.memoryConnection = conn;
    await conn.start();
    return conn;
  }
}
