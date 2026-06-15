// Persist the open document across reloads: the uploaded file lives in IndexedDB
// (too big for localStorage), the lightweight session metadata in localStorage.

const DB_NAME = "pdftotts";
const STORE = "files";
const FILE_KEY = "current";
const META_KEY = "pdftotts:session";

export interface SessionMeta {
  name: string;
  voice: string;
  speed: number;
  page: number;
  /** Source-document word index last being read, so a full reload (iOS often kills
   *  a backgrounded PWA) resumes at the exact word, not just the top of the page. */
  word?: number;
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

export async function saveFile(file: File): Promise<void> {
  const db = await openDb();
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction(STORE, "readwrite");
    tx.objectStore(STORE).put(file, FILE_KEY);
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
  });
}

export async function loadFile(): Promise<Blob | null> {
  try {
    const db = await openDb();
    return await new Promise((resolve) => {
      const tx = db.transaction(STORE, "readonly");
      const req = tx.objectStore(STORE).get(FILE_KEY);
      req.onsuccess = () => resolve((req.result as Blob) ?? null);
      req.onerror = () => resolve(null);
    });
  } catch {
    return null;
  }
}

export async function clearSaved(): Promise<void> {
  localStorage.removeItem(META_KEY);
  try {
    const db = await openDb();
    await new Promise<void>((resolve) => {
      const tx = db.transaction(STORE, "readwrite");
      tx.objectStore(STORE).delete(FILE_KEY);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
    });
  } catch {
    /* ignore */
  }
}

export function saveMeta(meta: SessionMeta): void {
  try {
    localStorage.setItem(META_KEY, JSON.stringify(meta));
  } catch {
    /* storage full / unavailable — non-fatal */
  }
}

export function loadMeta(): SessionMeta | null {
  try {
    const s = localStorage.getItem(META_KEY);
    return s ? (JSON.parse(s) as SessionMeta) : null;
  } catch {
    return null;
  }
}

export function updateSavedPage(page: number): void {
  const meta = loadMeta();
  if (meta && meta.page !== page) saveMeta({ ...meta, page });
}

export function updateSavedWord(word: number): void {
  const meta = loadMeta();
  if (meta && meta.word !== word) saveMeta({ ...meta, word });
}

export function patchMeta(partial: Partial<SessionMeta>): void {
  const meta = loadMeta();
  if (meta) saveMeta({ ...meta, ...partial });
}
