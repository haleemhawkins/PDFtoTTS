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

export async function listDocuments(): Promise<DocumentInfo[]> {
  return asJson(await fetch("/api/documents"));
}

export async function getDocument(id: string): Promise<DocumentInfo> {
  return asJson(await fetch(`/api/documents/${id}`));
}

export async function renameDocument(id: string, name: string): Promise<DocumentInfo> {
  return asJson(
    await fetch(`/api/documents/${id}`, {
      method: "PATCH",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ name }),
    }),
  );
}

/** Save the reading position server-side so it resumes on any device. Best-effort:
 *  a failed save must never disrupt reading. `keepalive` lets it complete even when
 *  fired during page unload (tab close / navigation). */
export async function savePosition(
  id: string,
  pos: { page: number; word: number; voice: string; speed: number; updatedAtMs: number },
): Promise<void> {
  try {
    await fetch(`/api/documents/${id}/position`, {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(pos),
      keepalive: true,
    });
  } catch {
    /* offline / server down — the local cache still has it */
  }
}

export async function deleteDocument(id: string): Promise<void> {
  const res = await fetch(`/api/documents/${id}`, { method: "DELETE" });
  if (!res.ok && res.status !== 404) throw new Error(`${res.status} ${res.statusText}`);
}

/** Fetch a stored original back as a File so the PDF/EPUB renderers can open it
 *  without the user re-selecting the document. */
export async function getOriginal(doc: DocumentInfo): Promise<File> {
  const res = await fetch(`/api/documents/${doc.id}/original`);
  if (!res.ok) throw new Error(`Couldn't load document (${res.status}).`);
  const blob = await res.blob();
  return new File([blob], doc.filename, {
    type: doc.type === "Epub" ? "application/epub+zip" : "application/pdf",
  });
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
      throw new Error(
        doc.type === "Epub"
          ? "Couldn't read this EPUB — no readable text was found (it may be image-only, e.g. a comic or art book)."
          : "Couldn't read this document — no selectable text found (a scanned PDF that OCR couldn't recover).",
      );
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

/** URL of a session's HLS playlist — the source for the media-element playback
 *  engine on iOS. Safari plays HLS natively (AVPlayer), which is what keeps audio
 *  going while the screen is locked and drives the lock-screen controls. */
export function streamUrl(sessionId: string): string {
  return `/api/sessions/${sessionId}/hls/playlist.m3u8`;
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
