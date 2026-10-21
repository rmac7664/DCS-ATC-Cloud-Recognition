# DCS-ATC: cloud speech recognition fixes

Fork of [Iceman1991/DCS-ATC-Release](https://github.com/Iceman1991/DCS-ATC-Release) (GPL-3.0), based on **0.18.0-alpha**.
0.18.0 has speech recognition over an OpenAI-compatible API built in (`AsrApiUrl`, `AsrApiKey`, `AsrApiModel`).
This fork changes only two things:

1. **Confidence for whisper-1** (`src/DcsAtc/AsrApi.cs`, `Calibrate`): 0.18.0 uses exp(avg_logprob), which puts most correct
   calls below DCS-ATC's "say again" threshold of 0.65 (35 recorded calls: 0.43-0.69, 31 of them below 0.65). The fork uses
   `clamp(1 + 0.35 × avg_logprob, 0, 1)`: the same calls score 0.70-0.87.
2. **API key masked in atc-debug.txt** (`src/DcsAtc/Program.cs`, `ConfigForLog`): the START line lists every setting; the key
   now shows as `sk-…abcd` instead of in full.

Both are covered by the self-test (`DcsAtc.exe --selftest`).

## Install

Install DCS-ATC 0.18.0 with its normal setup, then replace `DcsAtc.exe` in the DCS-ATC folder with the one from the latest
successful **Build DcsAtc.exe** run (Actions tab, Artifacts). Settings are 0.18.0's, e.g.:

```jsonc
"AsrApiUrl": "https://api.openai.com/v1/audio/transcriptions",
"AsrApiKey": "sk-...",
"AsrApiModel": "whisper-1",
```

## Updating from upstream

```
git remote add upstream https://github.com/Iceman1991/DCS-ATC-Release
git fetch upstream && git merge upstream/main
```

The earlier fork based on 0.17.0 (its own cloud-recognition code) is kept on the branch `fork-0.17`.
