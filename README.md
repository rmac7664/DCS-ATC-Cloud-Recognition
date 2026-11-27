# DCS-ATC plugin for DCSServerBot

Discord commands for DCS-ATC on a DCSServerBot server (role DCS Admin):

| Command | What it does |
|---|---|
| `/dcsatc status` | Installed version, newest release (betas included), on/off, running |
| `/dcsatc on` / `off` | Off writes `Scripts\DcsAtc\DcsAtcOff.txt` (the hook then doesn't start DCS-ATC) and stops it; on removes the file |
| `/dcsatc update [version]` | Downloads the release from Iceman1991/DCS-ATC-Release (small update if possible, like the app's own updater) and runs its setup silently |
| `/dcsatc install [version]` | Same for a new install (server task, this server's Saved Games) |
| `/dcsatc uninstall` | Runs DCS-ATC's uninstaller silently |

**SkyEye** (DCSServerBot's own SkyEye extension, set up in nodes.yaml):

| Command | What it does |
|---|---|
| `/skyeye status` | Installed and newest version, on/off, running, callsign and frequencies |
| `/skyeye restart` | Restarts SkyEye only, DCS keeps running |
| `/skyeye on` / `off` | Same as `/extension enable` / `disable SkyEye`: starts or stops it now and keeps that setting |
| `/skyeye update` | Downloads the newest SkyEye and restarts it if it was running |

`config.jsonc`, `charts.jsonc` and `frequencies.jsonc` are copied to `Saved Games\DCS-ATC-DCSServerBot-backup` before every
install, update and uninstall and put back afterwards.

Install (on the server, as the user that runs the bot): `irm https://raw.githubusercontent.com/rmac7664/DCS-ATC-Cloud-Recognition/dcsserverbot/install.ps1 | iex`, then `/node restart`. By hand: copy `plugins\dcsatc` into DCSServerBot's `plugins` folder and add `dcsatc` to `opt_plugins` in `config\main.yaml`.
