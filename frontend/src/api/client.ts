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
