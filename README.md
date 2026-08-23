# Lexus WarKey

A Warcraft III **1.26a** / DotA **LoD** hotkey tool for Windows. It lets you play
with the keys you want — including changing a skill's in‑game hotkey **mid‑match**,
without editing game files or injecting a DLL.

> Built with .NET 8 (WPF). Distributed as a single portable `.exe` with built‑in
> auto‑update.

---

## Features

- **Mid‑match skill hotkeys.** Detects your hero's abilities from the game's
  command card in memory and writes the letter you chose onto each skill's
  hotkey cell — live, during the match, re‑applied every game that skill appears.
  Ideal for DotA LoD where skills are random each game.
- **Inventory remap.** Map your own keys onto the six item slots (fixed to the
  numpad hotkeys `7 8 / 4 5 / 1 2`), sent via `SendInput`.
- **QuickChat.** A dense table of key → message rows. One key can hold several
  messages; pressing it sends them all in order (e.g. `-clear`, `-ii`, `-hhn`).
- **In‑game overlay (Ctrl+F6).** Configure skills and inventory without
  alt‑tabbing; the overlay never steals focus so the game keeps running.
- **Unique keys.** A physical key is bound in only one place across skills and
  inventory — claiming it frees it everywhere else.
- **Discord sign‑in (required).** You log in once with Discord; the token is
  cached (encrypted per Windows user) so it keeps working, even offline.
- **Auto‑update.** Checks GitHub on startup, downloads and verifies a newer
  version (SHA‑256), and installs it on a click.

## Download & install

1. Go to the [latest release](https://github.com/ganochirdulguun-art/LexusWarKey/releases/latest).
2. Download `LexusWarKey.exe` (portable — no installer).
3. Run it. On first launch, sign in with **Discord**.

> Match the game's integrity level: if you run Warcraft III **as administrator**,
> run Lexus WarKey as administrator too, or it can't read/write skill memory.

## Usage

### Warkey tab
- **Inventory** — click a slot, press the key you want to use for that item.
- **Skills** — once a match starts and your hero is up, your abilities appear
  with their default letter and a "your key" cell. Click it and press the letter
  you want; the app writes it onto the skill in‑game.

### QuickChat tab
- Pick a key, type a message, **+ Нэмэх** (or Enter). Add several messages to the
  same key to send them in sequence. Edit inline, delete per row.

### In‑game (Ctrl+F6)
- Press **Ctrl+F6** to open the overlay over Warcraft, pick a skill/item by
  number, then press the letter/key. Press Ctrl+F6 or Esc to close.

Settings are stored at:

```text
%LocalAppData%\LexusWarKey\profile.json
```

## How it works

- **Skills** are changed by an external memory write to the authoritative hotkey
  cell (`*(UInode+0x84)`) — no `CustomKeys.txt`, no DLL injection. One‑byte,
  validated writes only, and only while Warcraft is the focused window.
- **Inventory and QuickChat** use `SendInput` — your key becomes the game key /
  chat line.
- Remapping is suspended while Warcraft's chat line is open, so typing never
  casts a skill. Keys injected by the app are ignored to avoid loops.

The app reports itself online to the platform backend (username + version) so it
can be seen in the admin dashboard; it sends no gameplay data.

## Updating

- On startup the app checks for a newer release and pre‑downloads it.
- The update button in the header shows **↻** (check) and turns green **⬇** when
  a version is staged — click to install and restart.
- Existing users on a build without the updater must install the new version
  once by hand; after that, updates apply automatically.

## Development

```bash
dotnet test tests/LexusWarKey.Tests
dotnet run --project src/LexusWarKey
```

Build a portable release exe locally:

```bash
dotnet publish src/LexusWarKey -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Releasing

Tag a commit and push the tag — GitHub Actions builds, tests, stamps the version
from the tag, and publishes `LexusWarKey.exe` + `SHA256.txt` as release assets
(the updater reads exactly those):

```bash
git tag v2.3.0
git push origin v2.3.0
```

## Notes

- Windows only (WPF). Targets Warcraft III 1.26a.
- No telemetry beyond the online/version heartbeat. No anti‑cheat bypass, process
  hiding, or game‑file editing.
