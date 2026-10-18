# DCS-ATC — cloud speech recognition fork

Fork of [Iceman1991/DCS-ATC-Release](https://github.com/Iceman1991/DCS-ATC-Release) (GPL-3.0) that adds:

- **Cloud speech recognition** through any OpenAI-compatible `/v1/audio/transcriptions` endpoint
  (OpenAI `gpt-4o-mini-transcribe`, `gpt-4o-transcribe`, `whisper-1`, or a self-hosted Whisper server).
  Falls back to the local whisper-cli per call on errors/timeouts, and for the rest of the session after
  a bad key/URL or three failures in a row.
- **`AsrMaxParallel`**: a limit on how many local whisper-cli recognitions run at once (default 2),
  so a burst of transmissions queues instead of every run slowing the others down.

Everything else is upstream DCS-ATC. The change lives in `src/DcsAtc/CloudAsr.cs` plus a small hook in
`Transcribe()` (Program.cs) and six new settings.

## Settings (`config.jsonc`)

```jsonc
"AsrBackend": "api",                 // "local" = as upstream, "api" = cloud with local fallback
"AsrApiUrl": "https://api.openai.com/v1/audio/transcriptions",
"AsrApiKey": "sk-...",               // or leave "" and set the OPENAI_API_KEY environment variable
"AsrApiModel": "gpt-4o-mini-transcribe",
"AsrApiTimeoutMs": 6000,
"AsrMaxParallel": 2,
```

The audio of every transmission DCS-ATC hears on its frequencies is sent to the provider when
`AsrBackend` is `"api"`.

## Install

Download `DcsAtc.exe` from the latest successful **Build DcsAtc.exe** run (Actions tab), close DCS-ATC,
and replace `DcsAtc.exe` in the DCS-ATC folder (usually `%LOCALAPPDATA%\Programs\DCS-ATC`, or wherever the
installer put it: the folder that also has `models` and `config.jsonc`). Keep a copy of the original. Add the
settings above to `config.jsonc` in the same folder.

## Test without DCS

```
DcsAtc.exe --asr-test
```

Replays the recordings in `debrief\asr` through the configured recognizer and prints word error rate and
seconds per call. Run it once with `"AsrBackend": "local"` and once with `"api"` to compare.

## Updating from upstream

```
git remote add upstream https://github.com/Iceman1991/DCS-ATC-Release
git fetch upstream && git merge upstream/main
```
