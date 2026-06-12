import { useState } from "react";
import { EpubReader } from "./components/EpubReader";
import { PdfReader } from "./components/PdfReader";
import { PlaybackBar } from "./components/PlaybackBar";
import { Scrubber } from "./components/Scrubber";
import { UploadView } from "./components/UploadView";
import { useReader } from "./hooks/useReader";
import "./App.css";

export default function App() {
  const reader = useReader();
  const [file, setFile] = useState<File | null>(null);
  const [speed, setSpeed] = useState(1);

  const onStart = (f: File, voice: string, s: number) => {
    setFile(f);
    setSpeed(s);
    void reader.start(f, voice, s);
  };

  const onSpeed = (s: number) => {
    setSpeed(s);
    reader.setRate(s);
  };

  const inReader =
    file !== null &&
    (reader.state === "processing" ||
      reader.state === "playing" ||
      reader.state === "paused" ||
      reader.state === "reconnecting" ||
      (reader.state === "error" && reader.timeline.words.length > 0));

  const isPdf = file?.name.toLowerCase().endsWith(".pdf") ?? false;

  return (
    <div className="app">
      {reader.error && <div className="error-banner">{reader.error}</div>}

      {!inReader ? (
        <UploadView busy={reader.state === "uploading"} onStart={onStart} />
      ) : (
        <div className="reader-view">
          <PlaybackBar
            state={reader.state}
            progress={reader.progress}
            speed={speed}
            onPlay={reader.play}
            onPause={reader.pause}
            onSpeed={onSpeed}
          />
          <Scrubber
            getPositionMs={reader.getPositionMs}
            totalMs={reader.timeline.totalMs}
            onSeek={reader.seekToMs}
          />
          {file && isPdf ? (
            <PdfReader
              file={file}
              timeline={reader.timeline}
              activeIndex={reader.activeIndex}
              onSeekToWord={reader.seekToWord}
            />
          ) : file ? (
            <EpubReader
              file={file}
              timeline={reader.timeline}
              activeIndex={reader.activeIndex}
              onSeekToWord={reader.seekToWord}
            />
          ) : null}
        </div>
      )}
    </div>
  );
}
