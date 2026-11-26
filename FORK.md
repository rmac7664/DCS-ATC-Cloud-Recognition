# DCS-ATC: cloud speech recognition fixes

Fork of [Iceman1991/DCS-ATC-Release](https://github.com/Iceman1991/DCS-ATC-Release) (GPL-3.0), based on **0.23.29-alpha**.
0.18.0 has speech recognition over an OpenAI-compatible API built in (`AsrApiUrl`, `AsrApiKey`, `AsrApiModel`).
This fork changes one thing:

1. **Confidence for whisper-1** (`src/DcsAtc/AsrApi.cs`, `Calibrate`): 0.18.0 uses exp(avg_logprob), which puts most correct
   calls below DCS-ATC's "say again" threshold of 0.65 (35 recorded calls: 0.43-0.69, 31 of them below 0.65). The fork uses
   `clamp(1 + 0.35 × avg_logprob, 0, 1)`: the same calls score 0.70-0.87.
Upstream masks the API keys in atc-debug.txt itself since 0.23.x (`MaskKey`), so the fork's own masking was dropped.
For CI (no DCS, no test terrain on the runner) the fork also skips the charts.jsonc-on-Syria self-test step when DCS is missing, skips
`--anflugtest` and `--mptest` when `tests\terrain` is missing (the release repo does not ship it), and the workflow fetches a BtbN ffmpeg for the radio-effect test.

The calibration is covered by the self-test (`DcsAtc.exe --selftest`).

## Install

Install DCS-ATC 0.23.29 with its normal setup, then replace `DcsAtc.exe` in the DCS-ATC folder with the one from the latest
successful **Build DcsAtc.exe** run: the newest `fork-<sha>` release (Releases page), or the run's Artifacts. Settings are 0.18.0's, e.g.:

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
