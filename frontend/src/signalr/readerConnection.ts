import * as signalR from "@microsoft/signalr";
import type { ProcessedChunk, SessionStatus } from "../api/types";

export interface ReaderEvents {
  onChunk: (chunk: ProcessedChunk) => void;
  onProgress?: (p: { completedChunks: number; totalChunks: number; progress: number }) => void;
  onStatus?: (status: SessionStatus) => void;
  onError?: (e: { code: string; message: string }) => void;
  onReconnecting?: () => void;
  onReconnected?: () => void;
}

/**
 * Wraps the SignalR reader hub (design §6.4/§6.8): subscribes to a session,
 * surfaces ChunkReady/Progress/SessionStatus/Error, and on reconnect re-subscribes
 * so the hub backfills any chunks missed while disconnected.
 */
export class ReaderConnection {
  private readonly conn: signalR.HubConnection;
  private readonly sessionId: string;
  private readonly events: ReaderEvents;

  constructor(sessionId: string, events: ReaderEvents) {
    this.sessionId = sessionId;
    this.events = events;
    this.conn = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/reader")
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.conn.on("ChunkReady", (chunk: ProcessedChunk) => this.events.onChunk(chunk));
    this.conn.on("Progress", (p: { completedChunks: number; totalChunks: number; progress: number }) =>
      this.events.onProgress?.(p),
    );
    this.conn.on("SessionStatus", (s: { status: SessionStatus }) => this.events.onStatus?.(s.status));
    this.conn.on("Error", (e: { code: string; message: string }) => this.events.onError?.(e));

    this.conn.onreconnecting(() => this.events.onReconnecting?.());
    this.conn.onreconnected(async () => {
      await this.subscribe();
      this.events.onReconnected?.();
    });
  }

  async start(): Promise<void> {
    await this.conn.start();
    await this.subscribe();
  }

  private subscribe(): Promise<void> {
    return this.conn.invoke("Subscribe", this.sessionId);
  }

  async stop(): Promise<void> {
    try {
      await this.conn.stop();
    } catch {
      /* already stopped */
    }
  }
}
