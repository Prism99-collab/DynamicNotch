# Changelog

All notable changes to Dynamic Notch are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/),
and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- "Opening …" alert with the app's icon when an app starts (switch in Settings and the tray menu).
- Keyboard language alerts (Win+Space / Alt+Shift), screen connected / disconnected, and "Fully charged" on the charger.

### Changed
- The music visualizer follows the real sound; the slim glow over windows pulses with it.
- Jelly morph: height and width spring on their own timing, so the notch stretches organically.
- The notch leans toward the cursor while you touch the top edge or hover a clickable alert.
- Good news (done, connected, charging, full) gets a light sweep; alerts cast a soft glow of their colour below the notch.

### Fixed
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

[1.1.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.1.0
[1.0.0]: https://github.com/Prism99-collab/DynamicNotch/releases/tag/v1.0.0
