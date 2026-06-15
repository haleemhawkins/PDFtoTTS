// Shared data shapes (design §7.1). Enum values are PascalCase to match the
// backend's JsonStringEnumConverter output ("Pdf", "Ready", "Complete", ...).

export type DocumentType = "Pdf" | "Epub";
export type DocumentStatus = "Queued" | "Extracting" | "Ready" | "Error";
export type SessionStatus =
  | "Queued" | "Processing" | "Streaming" | "Complete" | "Error";

export interface BoundingBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface WordData {
  index: number; // global document word index
  text: string;
  startMs: number;
  endMs: number;
  page: number | null; // 1-based for PDF, null for EPUB
  bbox: BoundingBox | null; // PDF user space; null for EPUB
  confidence?: number;
}

/** A source document word (no timing) from GET /api/documents/{id}/words. */
export interface SourceWordData {
  index: number;
  text: string;
  page: number | null;
  bbox: BoundingBox | null;
}

export interface ProcessedChunk {
  chunkIndex: number;
  audioUrl: string;
  durationMs: number;
  words: WordData[];
  degraded?: boolean;
}

export interface DocumentInfo {
  id: string;
  filename: string;
  type: DocumentType;
  pageCount: number;
  wordCount: number;
  status: DocumentStatus;
  /** Extraction/OCR progress in [0,1] while status is "Extracting" (0 when not OCR'ing). */
  progress?: number;
}

export interface TtsSession {
  id: string;
  documentId: string;
  voice: string;
  speed: number;
  language: string;
  status: SessionStatus;
  progress: number;
}

export interface Voice {
  id: string;
  label: string;
  language: string;
  gender: string;
}
