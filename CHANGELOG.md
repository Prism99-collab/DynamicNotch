# Changelog

All notable changes to Dynamic Notch are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/),
and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Lock: a gold button in the control center (also in Settings and the tray menu). Locked, the notch keeps showing
  music, calls, alerts and the FPS pill but reacts to nothing: no opening at the top edge, no file drops, no
  clicks, no touch gestures. A click on its tray icon (or Win+Alt+N) unlocks it. It stays locked across restarts.
- "Drop files on the notch" switch in Settings.
- The open notch takes the colour of the app you're on: a deep glow of it fades in behind the panel, the rim lights
  up in it and the sliders wear it. A call, download, timer, Focus or a colourful album cover still lead with their
  own colour.
- Quick toggles have their own colours (Wi-Fi blue, Bluetooth indigo, Focus purple), dimmer when on but not connected.
- Sliders come alive under the pointer: a soft knob springs in, the track thickens while dragging; the brightness
  icons glow with the level.

### Fixed
- Dragging a desktop icon near the top of the screen turned the notch into a drop zone: it now wakes only when the
  dragged thing is on the notch itself.

## [1.3.0] — 2026-10-31

### Added
- Game mode: while a game is in front, the notch becomes the smallest pill it has, the live frame rate and nothing
  else (no pop-ups, glow or side island). The rate is worked out from every real frame's timestamp, refreshed
  20 times a second, for any game (DirectX 9 to 12, Vulkan, OpenGL), counted through the graphics kernel. Its window
  shrinks around the pill while you play so it costs almost nothing. "FPS in games" in Settings; on a PC where the
  account can't read frame events yet, switching it on asks Windows once.
- Mini mixer: an Apps tab in the sound picker with every app's own volume and a mute on its icon.
- Touch screens: pull the notch down with a finger and it stretches after it, then springs open when you let go
  (or flick); let go early and it springs back. Swipe sideways on it to switch which live activity leads, tap to
  open, and it gives a little squeeze under your finger. None of this runs on a PC without a touch screen or pen.

### Removed
- The scrolling network and power graphs on the System page (the numbers stay).

### Fixed
- A game could be set up twice, and leaving one game for another quickly could stop the new frame count.
- Turning on "FPS in games" no longer freezes the notch while Windows' permission prompt is open.
- Dragging from the shelf no longer looks up the app under the cursor on every mouse move; the browser's download
  list is read less often.

## [1.2.0] — 2026-10-30

### Added
- File shelf: drag a file, picture or text up to the notch and it morphs into a drop zone (marching outline,
  bobbing arrow, what's already kept fanned out); let go and it swallows it. Drag things back out from the new
  Shelf tab on the Clipboard & Files page. Kept across restarts. Dragging out adapts to the app: Excel, Word,
  PowerPoint and other editors get the picture or text itself, folders and chats get the file; a Copy button
  puts it on the clipboard for Ctrl+V anywhere. Pictures from a browser or an Office app, and Outlook
  attachments, are kept as real files.
- Live call activity: while Discord, Teams, Zoom, WhatsApp, Slack, a browser (Meet)… records from a microphone, the
  notch becomes a call pill (app icon, live dot, voice bars that move with you, timer; red when muted). The control
  center shows a call card with a big mute button and "back to the call".
- "Opening …" alert with the app's icon when an app starts (switch in Settings and the tray menu).
- Keyboard language alerts (Win+Space / Alt+Shift), screen connected / disconnected, and "Fully charged" on the charger.
- Choose what the notch shows when several things are live: a switcher on Home lists the call, the timer, Focus,
  downloads and every tab or app playing sound (each browser tab separately); a "+N" list when they don't fit.
  During a call with music, scroll or drag the Home card to flip between them. Tap the side island on a touch screen to swap.
- System page, live: a download card with the real progress from the browser ("24 MB of 65 MB · 5 MB/s · 9 s left"),
  then Open / Show in folder; the last minute of network traffic scrolling behind the speeds; the battery card's
  charging or discharging watts as a live graph with a pulsing bolt.
- Touch screens and pens: tap the notch to open it, swipe up to close, lists scroll with a finger.
- A hello on the first start, so a new install is never mistaken for "nothing happened".
- Hover hints drawn inside the notch (dark glass, in front of it) instead of white Windows tooltips hidden behind it.

### Changed
- The music visualizer follows the real sound; the slim glow over windows pulses with it.
- Jelly morph: height and width spring on their own timing, so the notch stretches organically.
- The notch leans toward the cursor while you touch the top edge or hover a clickable alert.
- Good news (done, connected, charging, full) gets a light sweep; alerts cast a soft glow of their colour below the notch.
- Crisper edges: a one-pixel rim that fades up the sides, a faint glass bevel when idle, and a glow under live
  music or calls that moves with the sound.
- Opening the notch: rows rise in one after another; album covers flip to the next song.
- App and notification icons are loaded in high resolution.
- Installed as a normal app folder with its own .NET runtime instead of one self-extracting file: nothing is
  unpacked to a temp folder at start (some antivirus tools and locked-down PCs blocked that).

### Fixed
- On some PCs the notch didn't appear after installing: one feature failing at start (no Bluetooth radio, no
  battery, no audio device...) took the whole notch down silently. Each feature now starts on its own, and if
  the notch can't start at all it says why. A "presentation mode" flag left on by some PCs no longer hides it.
- The Home card could go blank after a call ended.
- The monitor brightness slider vanished after a reboot (handle 0 was taken for "no monitor"); both brightness
  sliders now retry after startup, wake and display changes instead of disappearing.

## [1.1.0] — 2026-10-29

### Added
- Notification ringtone, played at your own volume (one ring per burst of messages).
- Volume slider for just the playing app (Spotify, a browser…), under the media buttons.
- Brightness slider for an external monitor (DDC/CI), next to the laptop's.
- Input devices in the sound picker: switch microphones as easily as speakers.
- Bluetooth Low Energy devices (game controllers, earbuds…) on the Bluetooth tile and in connect alerts, with their battery
  level ("50% · Xbox Wireless Controller") and a heads-up when it drops to 15 %.
- Pomodoro focus timer on the Tools page: progress ring, focus / break, rounds, scroll to adjust, next step offered when one ends.
- Keep awake tool, colour-coded tool tiles, right-click shortcuts (notch clipboard, Downloads).
- Alerts for error / warning dialogs from any app (click: copies the message and brings the dialog up) and for failed downloads.
- A tiny shiver for things that didn't work (failure notifications, a toggle Windows refused, a disconnected Bluetooth device)
  and a bump at the end of the volume / brightness range.

### Changed
- Over a maximized window the notch shrinks to a slim glowing bar and fades away when the mouse heads for a tab under it.
- Animations run at the screen's real refresh rate instead of 60 fps.

### Fixed
- Lag when opening the sound picker (device names are now read once, in the background).
- Stutters during animations: background garbage collection, and icons, thumbnails, drive info, sounds and audio lookups
  no longer run on the UI thread.
- Alerts that waited behind a full-screen app no longer all play minutes later; a screenshot no longer shows a "Copied" alert first.
- Garbled icons and text in the Smart page's alerts.

## [1.0.0] — 2026-10-29

First public release.

### Notch
- Dynamic-Island-style notch with spring-physics motion, crisp at any display scaling.
- Minimal by default, click-through, slims over maximized windows, hides in full screen.
- Live activities: music (cover-tinted visualizer), timer, downloads, Focus; a second activity splits into its own island.
- Expressive motion: pulse for new things, shake for problems.

### Control center
- Swipeable pages: Home, System, Tools, Clipboard & Files, Smart, Settings.
- Quick toggles (Wi-Fi, Bluetooth, Airplane, Focus, Mic, Power mode), volume with output picker, brightness, media controls.
- System monitor with battery time left, ping & signal, battery health and power draw.

### Alerts
- Clickable pop-ups for notifications, attention requests, downloads, screenshots, USB drives, Wi-Fi, Bluetooth,
  battery, Caps Lock and copied text, each tinted with its app's colour.
- Smart helpers: battery-drain warnings, shaky-connection alerts, charge reminder, Downloads tidy-up,
  break reminder, opt-in meeting mode.
- Focus mode that holds notifications and summarises what you missed.

### Windows integration
- Per-user installer for x64 and ARM64 (no admin rights), Start menu and desktop shortcuts, clean uninstall.
- Starts at sign-in and comes back after lock, sleep or a crash; Exit keeps it off until reopened.
- Dark tray menu, automatic battery mode, about 1 % of one CPU core when idle.

[1.3.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.3.0
[1.2.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.2.0
[1.1.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.1.0
[1.0.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.0.0
