## ADDED Requirements

### Requirement: Per-session HLS endpoint

The backend SHALL expose a per-session HLS stream that iOS can play natively:
`GET /api/sessions/{id}/hls/playlist.m3u8` returning an EVENT playlist, and
`GET /api/sessions/{id}/hls/{index}.ts` returning the audio segment for that
chunk. Each segment SHALL be the chunk's audio transcoded to AAC in an MPEG-TS
container, produced on demand and cached. The playlist SHALL list the contiguous
produced chunks (prefix from 0) with their durations, SHALL be available before
synthesis completes, and SHALL be finalized with `#EXT-X-ENDLIST` once synthesis
ends. The existing per-chunk endpoint `GET /api/sessions/{id}/chunks/{index}/audio`
SHALL remain available for the Web Audio (non-iOS) engine.

#### Scenario: Playlist lists produced segments in order

- **WHEN** a client requests the playlist for a session with chunks 0..N produced
- **THEN** the playlist contains `#EXTINF` entries for segments 0..N in order with
  each chunk's duration

#### Scenario: Playlist is available before synthesis completes

- **WHEN** the playlist is requested while later chunks are still synthesizing
- **THEN** it returns the segments produced so far without `#EXT-X-ENDLIST`, and is
  finalized with `#EXT-X-ENDLIST` only once synthesis ends

#### Scenario: Segment transcoded on demand

- **WHEN** a client requests `…/hls/{index}.ts` for a produced chunk
- **THEN** the chunk's WAV is transcoded to an AAC/MPEG-TS segment (if not already
  cached) and returned with content type `video/mp2t`

#### Scenario: Empty playlist is never served

- **WHEN** the playlist is requested before the first chunk is produced
- **THEN** the request waits until the first segment exists (or synthesis ends)
  rather than returning an empty playlist the player would give up on
