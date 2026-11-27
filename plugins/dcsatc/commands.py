"""DCS-ATC for DCSServerBot: status, on/off, install, update and uninstall of DCS-ATC from Discord.

DCS-ATC (https://github.com/Iceman1991/DCS-ATC-Release) is installed by its own Inno setup. On a dedicated server the
DCS hook in Saved Games starts DcsAtc.exe --auto with every mission, unless Scripts\\DcsAtc\\DcsAtcOff.txt exists.
This plugin uses exactly those mechanisms: the switch file, the setup from the release, and Inno's uninstaller.
"""
import asyncio
import hashlib
import json
import os
import shutil
import subprocess
import time
import zipfile

import aiohttp
import certifi
import discord
import psutil
import ssl

from core import Plugin, Group, utils, Server, Status, command, MizFile
from discord import app_commands
from services.bot import DCSServerBot

REPO_API = "https://api.github.com/repos/Iceman1991/DCS-ATC-Release/releases"
ASSET_PREFIX = "https://github.com/Iceman1991/DCS-ATC-Release/releases/download/"
FULL_ZIP, SMALL_ZIP, MANIFEST = "DCS-ATC.zip", "DCS-ATC-Update.zip", "manifest.json"
UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6B7E2C1A-4D2F-4E8B-9A11-DC5A7C0A7C01}_is1"
APP_KEY = r"Software\DCS-ATC"
# Your own files in the app folder: kept over every install, update and uninstall (charts\ stays the release's).
KEEP = ["config.jsonc", "charts.jsonc", "frequencies.jsonc"]
SETUP_TIMEOUT = 20 * 60


def _reg(key: str, name: str) -> str | None:
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as k:
            return str(winreg.QueryValueEx(k, name)[0])
    except OSError:
        return None


def _reg_set(key: str, name: str, value: str):
    import winreg
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, key) as k:
        winreg.SetValueEx(k, name, 0, winreg.REG_SZ, value)


def _num(version: str) -> tuple:
    import re
    m = re.search(r"(\d+)\.(\d+)\.(\d+)", version or "")
    return tuple(int(x) for x in m.groups()) if m else ()


def _sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def _kill_app() -> int:
    n = 0
    for p in psutil.process_iter(['name']):
        if (p.info['name'] or '').lower() == 'dcsatc.exe':
            try:
                p.kill()
                p.wait(10)
                n += 1
            except psutil.Error:
                pass
    return n


def _app_running() -> bool:
    return any((p.info['name'] or '').lower() == 'dcsatc.exe' for p in psutil.process_iter(['name']))


def _confirm_folder_dialogs(pid: int):
    """The setup asks for the server's Saved Games folder even when silent (Inno calls NextButtonClick then too).
    The registry holds the right folder, preselected in that dialog: press OK in any dialog box of the setup."""
    import win32con
    import win32gui
    import win32process
    try:
        own = {pid} | {c.pid for c in psutil.Process(pid).children(recursive=True)}
    except psutil.Error:
        return

    def visit(hwnd, _):
        if not win32gui.IsWindowVisible(hwnd) or win32gui.GetClassName(hwnd) != '#32770':
            return True
        if win32process.GetWindowThreadProcessId(hwnd)[1] in own:
            win32gui.PostMessage(hwnd, win32con.WM_COMMAND, win32con.IDOK, 0)
        return True

    win32gui.EnumWindows(visit, None)


def _run(args: list[str], timeout: int = SETUP_TIMEOUT) -> int:
    proc = subprocess.Popen(args)
    end = time.time() + timeout
    while proc.poll() is None:
        if time.time() > end:
            proc.kill()
            raise TimeoutError(f"{os.path.basename(args[0])} did not finish in {timeout // 60} minutes")
        _confirm_folder_dialogs(proc.pid)
        time.sleep(0.5)
    return proc.returncode


TIME_CHOICES = ["mission", "real", "05:30", "06:30", "08:00", "10:00", "12:00", "14:00", "16:00", "18:00", "19:30", "22:00"]


def _parse_time(value: str) -> int | None:
    """'HH:MM' -> seconds after midnight; None if it isn't a time."""
    import re
    m = re.fullmatch(r"\s*(\d{1,2})[:.h]?(\d{2})\s*", value or "")
    if not m or int(m.group(1)) > 23 or int(m.group(2)) > 59:
        return None
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60


async def time_autocomplete(interaction: discord.Interaction, current: str) -> list[app_commands.Choice[str]]:
    labels = {"mission": "Keep the mission's time", "real": "Real time now at that airport"}
    return [app_commands.Choice(name=labels.get(x, x), value=x) for x in TIME_CHOICES
            if not current or current.lower() in x or current.lower() in labels.get(x, '').lower()][:25]


class DCSATC(Plugin):
    """/dcsatc for DCS-ATC, /skyeye for DCSServerBot's SkyEye extension."""

    dcsatc = Group(name="dcsatc", description="Manage DCS-ATC on the server")

    def __init__(self, bot: DCSServerBot, eventlistener=None):
        super().__init__(bot, eventlistener)
        self.busy = asyncio.Lock()

    # ---- where things are -------------------------------------------------------------------------------------------

    @staticmethod
    def app_dir() -> str | None:
        d = _reg(UNINSTALL_KEY, 'Inno Setup: App Path') or _reg(UNINSTALL_KEY, 'InstallLocation')
        return d.rstrip('\\') if d and os.path.exists(os.path.join(d, 'DcsAtc.exe')) else None

    @staticmethod
    def installed_version() -> str | None:
        return _reg(UNINSTALL_KEY, 'DisplayVersion') if DCSATC.app_dir() else None

    @staticmethod
    def off_file(server: Server) -> str:
        return os.path.join(server.instance.home, 'Scripts', 'DcsAtc', 'DcsAtcOff.txt')

    @staticmethod
    def backup_dir(_app: str | None = None) -> str:
        # one fixed place, so a reinstall finds it even if the setup picks another app folder
        return os.path.join(os.path.expanduser('~'), 'Saved Games', 'DCS-ATC-DCSServerBot-backup')

    # ---- GitHub -----------------------------------------------------------------------------------------------------

    async def _session(self) -> aiohttp.ClientSession:
        return aiohttp.ClientSession(
            connector=aiohttp.TCPConnector(ssl=ssl.create_default_context(cafile=certifi.where())),
            headers={"User-Agent": "DCSServerBot-DCS-ATC"})

    async def latest_release(self, version: str | None = None) -> dict:
        """Newest release (betas included), or the one with that version."""
        async with await self._session() as s:
            async with s.get(REPO_API, params={"per_page": 20}, raise_for_status=True,
                             proxy=self.node.proxy, proxy_auth=self.node.proxy_auth) as r:
                releases = [x for x in await r.json() if not x.get('draft')]
        if version:
            releases = [x for x in releases if x['tag_name'].lstrip('v') == version.lstrip('v')]
            if not releases:
                raise ValueError(f"There is no release {version}.")
        if not releases:
            raise ValueError("No release found.")
        return max(releases, key=lambda x: _num(x['tag_name']))

    @staticmethod
    def _asset(release: dict, name: str) -> dict | None:
        for a in release.get('assets', []):
            if a['name'] == name and a['browser_download_url'].startswith(ASSET_PREFIX):
                return a
        return None

    async def _download(self, asset: dict, target: str):
        async with await self._session() as s:
            async with s.get(asset['browser_download_url'], raise_for_status=True, proxy=self.node.proxy,
                             proxy_auth=self.node.proxy_auth, timeout=aiohttp.ClientTimeout(total=3600)) as r:
                h = hashlib.sha256()
                with open(target, 'wb') as f:
                    async for chunk in r.content.iter_chunked(1 << 20):
                        f.write(chunk)
                        h.update(chunk)
        if os.path.getsize(target) != asset['size']:
            raise IOError(f"{asset['name']}: download incomplete")
        digest = (asset.get('digest') or '').lower()
        if digest.startswith('sha256:') and h.hexdigest() != digest[7:]:
            raise IOError(f"{asset['name']}: checksum does not match")

    async def _small_is_enough(self, release: dict, app: str | None) -> bool:
        """Like DCS-ATC's own updater: the small update only if every big file of this release is already here."""
        manifest = self._asset(release, MANIFEST)
        if not app or not manifest or not self._asset(release, SMALL_ZIP):
            return False
        try:
            async with await self._session() as s:
                async with s.get(manifest['browser_download_url'], raise_for_status=True, proxy=self.node.proxy,
                                 proxy_auth=self.node.proxy_auth) as r:
                    data = json.loads((await r.read()).decode('utf-8-sig'))
            if data.get('version') != release['tag_name'].lstrip('v') or not data.get('files'):
                return False

            def have_all() -> bool:
                for f in data['files']:
                    p = os.path.join(app, f['path'])
                    if not os.path.exists(p) or os.path.getsize(p) != f['size'] or _sha256(p) != f['sha256'].lower():
                        return False
                return True

            return await asyncio.to_thread(have_all)
        except Exception as ex:
            self.log.warning(f"DCS-ATC: manifest not usable, taking the full setup: {ex}")
            return False

    # ---- your own files ---------------------------------------------------------------------------------------------

    def save_own_files(self, app: str) -> str:
        bak = self.backup_dir(app)
        os.makedirs(bak, exist_ok=True)
        for name in KEEP:
            src = os.path.join(app, name)
            if os.path.isdir(src):
                shutil.copytree(src, os.path.join(bak, name), dirs_exist_ok=True)
            elif os.path.isfile(src):
                shutil.copy2(src, os.path.join(bak, name))
        return bak

    def restore_own_files(self, app: str):
        bak = self.backup_dir(app)
        if not os.path.isdir(bak):
            return
        for name in KEEP:
            src = os.path.join(bak, name)
            if os.path.isdir(src):
                shutil.copytree(src, os.path.join(app, name), dirs_exist_ok=True)
            elif os.path.isfile(src):
                shutil.copy2(src, os.path.join(app, name))

    # ---- install / update / uninstall -------------------------------------------------------------------------------

    async def _install(self, server: Server, release: dict, status) -> str:
        app = self.app_dir()
        if app:
            self.save_own_files(app)
        # the setup preselects these in its folder dialogs (we press OK there): this server's Saved Games and DCS
        _reg_set(APP_KEY, 'ServerSavedGames', server.instance.home)
        _reg_set(APP_KEY, 'ServerDcs', self.node.installation)
        small = await self._small_is_enough(release, app)
        asset = self._asset(release, SMALL_ZIP if small else FULL_ZIP)
        if not asset:
            raise ValueError(f"Release {release['tag_name']} has no {FULL_ZIP}.")
        tmp = os.path.join(os.environ.get('TEMP', os.path.expanduser('~')), 'DcsAtc-DCSServerBot')
        shutil.rmtree(tmp, ignore_errors=True)
        os.makedirs(tmp)
        zip_path = os.path.join(tmp, asset['name'])
        await status(f"Downloading {asset['name']} ({asset['size'] // (1 << 20)} MB) ...")
        await self._download(asset, zip_path)
        setup_name = asset['name'][:-4] + '-Setup.exe'
        with zipfile.ZipFile(zip_path) as z:
            z.extract(setup_name, tmp)
        os.remove(zip_path)
        await status(f"Installing DCS-ATC {release['tag_name'].lstrip('v')} ...")
        await asyncio.to_thread(_kill_app)
        args = [os.path.join(tmp, setup_name), '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LANG=en',
                '/LOG=' + os.path.join(tmp, 'setup.log')]
        if not app:
            args.append('/TASKS=server')
        code = await asyncio.to_thread(_run, args)
        if code != 0:
            raise RuntimeError(f"The setup ended with code {code}, see {os.path.join(tmp, 'setup.log')}")
        app = self.app_dir()
        if not app:
            raise RuntimeError(f"The setup finished, but DCS-ATC is not there. See {os.path.join(tmp, 'setup.log')}")
        self.restore_own_files(app)
        shutil.rmtree(tmp, ignore_errors=True)
        return release['tag_name'].lstrip('v')

    async def _uninstall(self) -> None:
        app = self.app_dir()
        uninstaller = (_reg(UNINSTALL_KEY, 'UninstallString') or '').strip('"')
        if not app or not os.path.exists(uninstaller):
            raise ValueError("DCS-ATC is not installed.")
        self.save_own_files(app)
        await asyncio.to_thread(_kill_app)
        code = await asyncio.to_thread(_run, [uninstaller, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'])
        if code != 0:
            raise RuntimeError(f"The uninstaller ended with code {code}.")

    # ---- commands ---------------------------------------------------------------------------------------------------

    async def _work(self, interaction: discord.Interaction, start: str, job) -> None:
        ephemeral = utils.get_ephemeral(interaction)
        # noinspection PyUnresolvedReferences
        await interaction.response.send_message(start, ephemeral=ephemeral)
        msg = await interaction.original_response()
        if self.busy.locked():
            await msg.edit(content="DCS-ATC: another install, update or uninstall is still running.")
            return

        async def status(text: str):
            await msg.edit(content=text)

        async with self.busy:
            try:
                await msg.edit(content=await job(status))
            except Exception as ex:
                self.log.exception("DCS-ATC")
                await msg.edit(content=f"DCS-ATC: {ex}")

    @dcsatc.command(name="status", description="DCS-ATC version, newest release and whether it is on")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def status_cmd(self, interaction: discord.Interaction,
                     server: app_commands.Transform[Server, utils.ServerTransformer]):
        ephemeral = utils.get_ephemeral(interaction)
        # noinspection PyUnresolvedReferences
        await interaction.response.defer(ephemeral=ephemeral)
        version = self.installed_version()
        try:
            latest = (await self.latest_release())['tag_name'].lstrip('v')
        except Exception as ex:
            latest = f"unknown ({ex})"
        embed = discord.Embed(title=f"DCS-ATC on {server.name}", color=discord.Color.blue())
        embed.add_field(name="Installed", value=version or "not installed")
        embed.add_field(name="Newest release", value=latest)
        if version:
            embed.add_field(name="Switched", value="off" if os.path.exists(self.off_file(server)) else "on")
            embed.add_field(name="Running now", value="yes" if _app_running() else "no")
            embed.add_field(name="Folder", value=self.app_dir(), inline=False)
            if _num(latest) and _num(latest) > _num(version):
                embed.set_footer(text="Update with /dcsatc update")
        await interaction.followup.send(embed=embed, ephemeral=ephemeral)

    @dcsatc.command(name="on", description="Start DCS-ATC with every mission again")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def switch_on(self, interaction: discord.Interaction,
                        server: app_commands.Transform[Server, utils.ServerTransformer]):
        ephemeral = utils.get_ephemeral(interaction)
        if not self.installed_version():
            # noinspection PyUnresolvedReferences
            await interaction.response.send_message("DCS-ATC is not installed, use /dcsatc install.", ephemeral=ephemeral)
            return
        off = self.off_file(server)
        if os.path.exists(off):
            os.remove(off)
        text = "DCS-ATC is on. It starts with every mission."
        if server.status in [Status.RUNNING, Status.PAUSED] and not _app_running():
            text += " It isn't running in the current mission: /mission restart starts it now."
        # noinspection PyUnresolvedReferences
        await interaction.response.send_message(text, ephemeral=ephemeral)

    @dcsatc.command(name="off", description="Stop DCS-ATC now and don't start it with missions")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def switch_off(self, interaction: discord.Interaction,
                         server: app_commands.Transform[Server, utils.ServerTransformer]):
        ephemeral = utils.get_ephemeral(interaction)
        if not self.installed_version():
            # noinspection PyUnresolvedReferences
            await interaction.response.send_message("DCS-ATC is not installed.", ephemeral=ephemeral)
            return
        off = self.off_file(server)
        os.makedirs(os.path.dirname(off), exist_ok=True)
        with open(off, 'w', encoding='utf-8') as f:
            f.write('off (DCSServerBot)\n')
        stopped = await asyncio.to_thread(_kill_app)
        # noinspection PyUnresolvedReferences
        await interaction.response.send_message(
            "DCS-ATC is off" + (" and stopped." if stopped else ".") + " /dcsatc on switches it back on.",
            ephemeral=ephemeral)

    @dcsatc.command(name="update", description="Update DCS-ATC to the newest release (or the version you give)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def update_cmd(self, interaction: discord.Interaction,
                     server: app_commands.Transform[Server, utils.ServerTransformer], version: str | None = None):
        async def job(status) -> str:
            installed = self.installed_version()
            if not installed:
                return "DCS-ATC is not installed, use /dcsatc install."
            release = await self.latest_release(version)
            if release['tag_name'].lstrip('v') == installed:
                return f"DCS-ATC {installed} is already installed."
            new = await self._install(server, release, status)
            return (f"DCS-ATC updated from {installed} to {new}. Your config, charts and frequencies were kept. "
                    f"Restart the server (/server restart) to load the new version.")
        await self._work(interaction, "Checking for a DCS-ATC update ...", job)

    @dcsatc.command(name="install", description="Install DCS-ATC (newest release, or the version you give)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def install_cmd(self, interaction: discord.Interaction,
                      server: app_commands.Transform[Server, utils.ServerTransformer], version: str | None = None):
        async def job(status) -> str:
            if self.installed_version():
                return f"DCS-ATC {self.installed_version()} is already installed. Use /dcsatc update."
            new = await self._install(server, await self.latest_release(version), status)
            kept = " Your saved config, charts and frequencies were put back." \
                if os.path.isdir(self.backup_dir()) else ""
            return f"DCS-ATC {new} installed.{kept} Restart the server (/server restart) to start it with missions."
        await self._work(interaction, "Installing DCS-ATC ...", job)

    @dcsatc.command(name="uninstall", description="Uninstall DCS-ATC (your config, charts and frequencies are kept for a reinstall)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def uninstall_cmd(self, interaction: discord.Interaction,
                        server: app_commands.Transform[Server, utils.ServerTransformer]):
        async def job(status) -> str:
            app = self.app_dir()
            await self._uninstall()
            return (f"DCS-ATC uninstalled. Your config, charts and frequencies are saved in "
                    f"{self.backup_dir(app)} and come back with /dcsatc install. "
                    f"Restart the server (/server restart) so DCS drops the hook.")
        await self._work(interaction, "Uninstalling DCS-ATC ...", job)

    # ---- Real weather with a time you pick --------------------------------------------------------------------------

    @command(description="Real weather from an airport, with the mission time you pick (date stays)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    @app_commands.rename(idx="airport")
    @app_commands.describe(idx="Airport whose current weather to use",
                           time="mission = keep, real = real time now, or a time like 06:30 (local)",
                           use_orig="Start from your original mission (recommended)")
    @app_commands.autocomplete(idx=utils.airbase_autocomplete, time=time_autocomplete)
    async def weather(self, interaction: discord.Interaction,
                      server: app_commands.Transform[Server, utils.ServerTransformer(
                          status=[Status.RUNNING, Status.PAUSED, Status.STOPPED])],
                      idx: int, time: str = "mission", use_orig: bool = True):
        ephemeral = utils.get_ephemeral(interaction)
        seconds = None
        if time not in ("mission", "real"):
            seconds = _parse_time(time)
            if seconds is None:
                # noinspection PyUnresolvedReferences
                await interaction.response.send_message(
                    f"\"{time}\" isn't a time. Use mission, real, or a time like 06:30.", ephemeral=ephemeral)
                return
        if not server.current_mission or idx >= len(server.current_mission.airbases):
            # noinspection PyUnresolvedReferences
            await interaction.response.send_message("Pick the airport from the list.", ephemeral=ephemeral)
            return
        airbase = server.current_mission.airbases[idx]
        if not airbase.get('code'):
            # noinspection PyUnresolvedReferences
            await interaction.response.send_message(f"{airbase.get('name')} has no ICAO code, pick another airport.",
                                                    ephemeral=ephemeral)
            return
        # noinspection PyUnresolvedReferences
        await interaction.response.defer(ephemeral=ephemeral)
        if server.is_populated() and not await utils.yn_question(
                interaction, "Players are on the server. Reload the mission with the new weather now?",
                ephemeral=ephemeral):
            await interaction.followup.send("Weather not changed.", ephemeral=ephemeral)
            return
        msg = await interaction.followup.send("Changing the weather ...", ephemeral=ephemeral, wait=True)
        try:
            on = {"enable": True}
            config = {"options": {
                "weather": {"enable": True, "icao": airbase['code'], "wind": on, "clouds": on, "fog": on,
                            "dust": on, "temperature": on, "pressure": on},
                "time": {"enable": time == "real"},
                "date": {"enable": False}
            }}
            filename = await server.get_current_mission_file()
            status = server.status
            new_filename = await server.run_on_extension('RealWeather', 'apply_realweather',
                                                         filename=filename, config=config, use_orig=use_orig)
            if seconds is not None:
                def set_time():
                    miz = MizFile(new_filename)
                    miz.start_time = seconds
                    miz.save()
                await asyncio.to_thread(set_time)
            if new_filename != filename:
                await server.replaceMission(int(server.settings['listStartIndex']), new_filename)
            text = f"Weather from {airbase['name']} ({airbase['code']})"
            text += {"mission": ", mission time kept", "real": ", real time now"}.get(time, f", time {time}")
            text += ", date kept."
            if status in [Status.RUNNING, Status.PAUSED]:
                await server.restart(modify_mission=False)
                text += " Mission reloaded."
            await msg.edit(content=text)
        except ValueError:
            await msg.edit(content="The RealWeather extension isn't loaded on this server.")
        except Exception as ex:
            self.log.exception("weather")
            await msg.edit(content=f"Couldn't change the weather: {ex}")


    # ---- SkyEye (DCSServerBot's own SkyEye extension) ---------------------------------------------------------------

    skyeye = Group(name="skyeye", description="Manage SkyEye on the server")

    @staticmethod
    def _skyeye_configured(server: Server) -> bool:
        return 'SkyEye' in (server.instance.locals.get('extensions') or {})

    async def _skyeye(self, interaction: discord.Interaction, server: Server, start: str, job) -> None:
        if not self._skyeye_configured(server):
            # noinspection PyUnresolvedReferences
            await interaction.response.send_message(f"SkyEye is not set up for {server.name} in nodes.yaml.",
                                                    ephemeral=utils.get_ephemeral(interaction))
            return
        await self._work(interaction, start, job)

    @skyeye.command(name="status", description="SkyEye version, newest release, on/off and running")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def skyeye_status(self, interaction: discord.Interaction,
                            server: app_commands.Transform[Server, utils.ServerTransformer]):
        async def job(status) -> str:
            ext = server.extensions.get('SkyEye')
            enabled = (server.instance.locals['extensions']['SkyEye'] or {}).get('enabled', True)
            if not ext:
                return f"SkyEye is switched {'on' if enabled else 'off'} but not loaded (the server isn't running)."
            version = ext.version
            latest = await server.run_on_extension('SkyEye', 'get_latest_version')
            running = await server.run_on_extension('SkyEye', 'is_running')
            lines = [f"**SkyEye on {server.name}**",
                     f"Installed: {version or 'unknown'}  |  Newest: {latest or 'unknown'}",
                     f"Switched: {'on' if enabled else 'off'}  |  Running now: {'yes' if running else 'no'}"]
            for cfg in getattr(ext, 'configs', []):
                lines.append(f"{cfg.get('coalition', 'blue')}: {cfg.get('callsign', '?')} on "
                             f"{cfg.get('srs-frequencies', '?')}")
            if latest and version and _num(latest) > _num(version):
                lines.append("Update with /skyeye update")
            return "\n".join(lines)
        await self._skyeye(interaction, server, "Checking SkyEye ...", job)

    @skyeye.command(name="restart", description="Restart SkyEye only (DCS keeps running)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def skyeye_restart(self, interaction: discord.Interaction,
                             server: app_commands.Transform[Server, utils.ServerTransformer(
                                 status=[Status.RUNNING, Status.PAUSED])]):
        async def job(status) -> str:
            if 'SkyEye' not in server.extensions:
                return "SkyEye isn't loaded. Is it switched on? (/skyeye on)"
            await server.run_on_extension('SkyEye', 'shutdown')
            await asyncio.sleep(2)
            await server.run_on_extension('SkyEye', 'prepare')
            ok = await server.run_on_extension('SkyEye', 'startup')
            return "SkyEye restarted." if ok else "SkyEye did not start again, check the DCSServerBot log."
        await self._skyeye(interaction, server, "Restarting SkyEye ...", job)

    @skyeye.command(name="on", description="Switch SkyEye on (starts now if the server is running)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def skyeye_on(self, interaction: discord.Interaction,
                        server: app_commands.Transform[Server, utils.ServerTransformer]):
        async def job(status) -> str:
            ok = await server.enable_extension('SkyEye')
            return "SkyEye is on." if ok else "SkyEye could not be switched on, check the DCSServerBot log."
        await self._skyeye(interaction, server, "Switching SkyEye on ...", job)

    @skyeye.command(name="off", description="Stop SkyEye and keep it off (files stay installed)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def skyeye_off(self, interaction: discord.Interaction,
                         server: app_commands.Transform[Server, utils.ServerTransformer]):
        async def job(status) -> str:
            ok = await server.disable_extension('SkyEye')
            return "SkyEye is off. /skyeye on switches it back on." if ok \
                else "SkyEye could not be switched off, check the DCSServerBot log."
        await self._skyeye(interaction, server, "Switching SkyEye off ...", job)

    @skyeye.command(name="update", description="Update SkyEye to the newest release (restarts SkyEye)")
    @app_commands.guild_only()
    @utils.app_has_role('DCS Admin')
    async def skyeye_update(self, interaction: discord.Interaction,
                            server: app_commands.Transform[Server, utils.ServerTransformer]):
        async def job(status) -> str:
            if 'SkyEye' not in server.extensions:
                return "SkyEye isn't loaded. Switch it on first (/skyeye on)."
            old = server.extensions['SkyEye'].version
            latest = await server.run_on_extension('SkyEye', 'get_latest_version')
            if not latest:
                return "Couldn't reach GitHub for the newest SkyEye."
            if old and _num(latest) <= _num(old):
                return f"SkyEye {old} is already the newest."
            was_running = await server.run_on_extension('SkyEye', 'is_running')
            if was_running:
                await status("Stopping SkyEye ...")
                await server.run_on_extension('SkyEye', 'shutdown')
                await asyncio.sleep(2)
            await status(f"Downloading SkyEye {latest} ...")
            if not await server.run_on_extension('SkyEye', 'update', version=latest):
                return "The SkyEye update failed, check the DCSServerBot log."
            server.extensions['SkyEye']._version = None
            if was_running:
                await server.run_on_extension('SkyEye', 'prepare')
                await server.run_on_extension('SkyEye', 'startup')
            return f"SkyEye updated from {old} to {latest}." + (" It's running again." if was_running else "")
        await self._skyeye(interaction, server, "Checking for a SkyEye update ...", job)


async def setup(bot: DCSServerBot):
    await bot.add_cog(DCSATC(bot))
