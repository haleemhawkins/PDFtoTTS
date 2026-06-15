import type { DocumentInfo, ProcessedChunk, SourceWordData, TtsSession, Voice } from "./types";

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body?.message) detail = body.message;
    } catch {
      /* ignore */
    }
    throw new Error(detail);
  }
  return (await res.json()) as T;
}

export async function uploadDocument(file: File): Promise<DocumentInfo> {
  const form = new FormData();
  form.append("file", file);
  return asJson(await fetch("/api/documents", { method: "POST", body: form }));
}

export async function getDocument(id: string): Promise<DocumentInfo> {
  return asJson(await fetch(`/api/documents/${id}`));
}

/** Poll the document until extraction (and any OCR) finishes. Resolves on Ready,
 *  throws on Error or timeout. Scanned PDFs are OCR'd server-side, which can take
 *  minutes, so this waits patiently. */
export async function waitForDocumentReady(
  id: string,
  onTick?: (doc: DocumentInfo) => void,
  { intervalMs = 1500, timeoutMs = 30 * 60 * 1000 } = {},
): Promise<DocumentInfo> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const doc = await getDocument(id);
    onTick?.(doc);
    if (doc.status === "Ready") return doc;
    if (doc.status === "Error")
      throw new Error("Couldn't read this document — no selectable text found (a scanned PDF that OCR couldn't recover).");
    if (Date.now() > deadline) throw new Error("Timed out preparing this document.");
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}

export async function createSession(
  documentId: string,
  voice: string,
  speed: number,
  language = "en",
  startWordIndex = 0,
): Promise<TtsSession> {
  return asJson(
    await fetch(`/api/documents/${documentId}/sessions`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ voice, speed, language, startWordIndex }),
    }),
  );
}

export async function getSession(id: string): Promise<TtsSession> {
  return asJson(await fetch(`/api/sessions/${id}`));
}

export async function getChunks(id: string): Promise<ProcessedChunk[]> {
  return asJson(await fetch(`/api/sessions/${id}/chunks`));
}

export async function getWords(documentId: string): Promise<SourceWordData[]> {
  return asJson(await fetch(`/api/documents/${documentId}/words`));
}

export async function getVoices(): Promise<Voice[]> {
  try {
    return await asJson(await fetch("/api/voices"));
  } catch {
    return [];
  }
}
