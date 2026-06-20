// Per-document reading state (position, voice, speed) plus the last-opened id, so
// reopening a document resumes where you left off and a reload restores the last
// book. The originals themselves now live on the server (the library), so we no
// longer cache files in IndexedDB — we just remember lightweight state per id.

const LIB_KEY = "pdftotts:library";
// Legacy single-document storage from before the library existed; cleared on load.
const LEGACY_META_KEY = "pdftotts:session";
const LEGACY_DB_NAME = "pdftotts";

export interface DocState {
  page: number;
  word: number;
  voice: string;
  speed: number;
  /** Unix ms of the last local change — compared against the server's position to
   *  pick the newer one when resuming across devices (last-writer-wins). */
  updatedAt?: number;
}

interface LibraryState {
  lastOpenedId?: string;
  docs: Record<string, DocState>;
}

function load(): LibraryState {
  try {
    const s = localStorage.getItem(LIB_KEY);
    if (s) return JSON.parse(s) as LibraryState;
  } catch {
    /* ignore */
  }
  return { docs: {} };
}

function save(state: LibraryState): void {
  try {
    localStorage.setItem(LIB_KEY, JSON.stringify(state));
  } catch {
    /* storage full / unavailable — non-fatal */
  }
}

export function getDocState(id: string): DocState | undefined {
  return load().docs[id];
}

export function patchDocState(id: string, partial: Partial<DocState>): void {
  const state = load();
  const prev = state.docs[id] ?? { page: 1, word: 0, voice: "", speed: 1 };
  // Stamp the change time so cross-device resume can pick the newer position.
  // A caller restoring a known position passes its own updatedAt to avoid making
  // a restore look like a fresh local edit.
  state.docs[id] = { ...prev, ...partial, updatedAt: partial.updatedAt ?? Date.now() };
  save(state);
}

export function updateSavedPage(id: string, page: number): void {
  const cur = load().docs[id];
  if (cur?.page !== page) patchDocState(id, { page });
}

export function updateSavedWord(id: string, word: number): void {
  const cur = load().docs[id];
  if (cur?.word !== word) patchDocState(id, { word });
}

export function getLastOpenedId(): string | undefined {
  return load().lastOpenedId;
}

export function setLastOpenedId(id: string | undefined): void {
  const state = load();
  state.lastOpenedId = id;
  save(state);
}

export function forgetDoc(id: string): void {
  const state = load();
  delete state.docs[id];
  if (state.lastOpenedId === id) state.lastOpenedId = undefined;
  save(state);
}

/** Best-effort one-time cleanup of the pre-library single-file storage so it
 *  doesn't waste localStorage / IndexedDB quota. Safe to call on every load. */
export function clearLegacyStorage(): void {
  try {
    localStorage.removeItem(LEGACY_META_KEY);
  } catch {
    /* ignore */
  }
  try {
    indexedDB.deleteDatabase(LEGACY_DB_NAME);
  } catch {
    /* ignore */
  }
}
