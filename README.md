# Stowbyte ❄️♨️

**The cure for budget builds with one fast drive.**

Got a small fast SSD (C:) and a big slow drive (D:)? Stowbyte lets you install programs
wherever you like, then **freeze** the ones you're not using over to the roomy drive — leaving
a transparent link behind so every shortcut, launcher, and registry path still works. When you
want one back, **defrost** it to the fast drive in a click. Heavy apps can ride in **Shuttle**
mode: pulled to C while you use them, frozen back to D when you close them.

It lives in your system tray as a little grid of your apps' real icons:

- 🟢 **thawed on C** — alive on the fast drive
- 🔵 **frozen on D** — parked on the big drive, still launches (via a directory junction)
- 🟡 **moving** — with a live % over the icon

## How it works

Stowbyte uses Windows **directory junctions**. When you freeze an app:

1. Its folder is robocopied to your offload drive (D:).
2. The copy is **verified** (file count + byte total must match).
3. Only then is the original moved aside and replaced with a junction pointing at the D: copy.

Because the junction sits at the *exact original path*, everything that referenced the app keeps
working — it just reads from the slower drive while frozen. Defrosting reverses it: copy back,
verify, then remove the junction (link only — your data is never touched).

### Safety

- **Never moves a running app.** Stowbyte checks for live processes under the folder before *and*
  during a move, and aborts cleanly if anything is in use. Running apps show a `● running` badge
  and their Freeze button is disabled.
- **Verify-before-replace.** The original is only removed after the copy is confirmed byte-for-byte.
- **Rename-aside, not delete.** A locked file makes the move fail atomically — nothing is lost.

## Features

- System-tray grid of real app icons, one-click launch
- Freeze (C→D) / Defrost (D→C) with a live progress %
- **Shuttle mode** — auto-defrost on launch, auto-freeze on close (great for ComfyUI and other heavy apps)
- Per-app move timings + a hover clock so you know how long a freeze/defrost will take
- Configurable offload location and install zone
- Add an app by picking its **folder** — Stowbyte auto-detects the launch .exe
- Start with Windows (silent, elevated, no UAC nag — via a Scheduled Task)
- No telemetry. Nothing ever leaves your PC.

## Requirements

- Windows 10/11
- .NET 8 (Windows Desktop runtime)
- Runs **elevated** (admin) — directory junctions into `C:\Program Files` require it, so Stowbyte
  self-elevates via UAC on launch.

## Build

```
dotnet build
```

Output: `bin/Debug/net8.0-windows/Stowbyte.exe`

## License

MIT — see [LICENSE](LICENSE). Short version: this is my code, but share it freely and modify it as
you like. If you get rich with it, toss me a bit please :)

☕ Like it? **CashApp: $minidraco711**
