# Privacy policy

*Dynamic Notch · last updated 11 November 2026*

Dynamic Notch is made by Prism99-collab. This page explains what the app looks at on your PC, what it keeps, and
what (very little) goes over the network. The short version: **no account, no ads, no analytics, no telemetry.
Nothing about you or your PC is sent to the developer or to anyone else.**

## What the app reads on your PC, and why

Everything below is read on your PC, used on your PC, and never sent anywhere.

| What | Why |
|---|---|
| The media that's playing (title, artist, album art, position) from Windows' media controls | To show the music activity and its controls |
| Your notifications (only if you allow it in Windows) | To show them in the notch |
| The clipboard, when it changes | To show the "copied" alert and keep things on the shelf when you ask |
| Whether the microphone or camera is in use (not what they record) | To show the privacy indicator |
| Whether you're typing or moving the mouse (the time of the last input, never which keys) | So the notch and its companion react to you |
| The app in front, and whether it's full screen | To step aside, and to hide in games and videos |
| CPU, GPU, memory and temperature readings, and which apps use them most | For the System page, game stats and strain warnings |
| Battery, Wi-Fi, Bluetooth, brightness and audio device state | For the controls and alerts |
| Files you drop on the notch | To keep them on the shelf until you take them out |

## What the app keeps

Only on your PC, in `%APPDATA%\DynamicNotch`:

- your settings (`settings.json`, with a backup copy);
- the shelf (what you've dropped on the notch);
- the strain log (the last 60 strain warnings, with the readings and app names around each one);
- a small technical log (`app.log`, `error.log`) to help fix problems.

You can delete any of it at any time. Uninstalling the app removes it.

## What goes over the network

- **Connection check:** to show your ping on the System page, the app pings public DNS servers (1.1.1.1, 8.8.8.8,
  9.9.9.9) or opens a connection to one of them. Nothing is sent but the ping itself.
- **Album art:** for a browser tab that plays, the app may download that tab's own cover image from the website
  that's playing it, the same picture your browser already shows.
- **Things you drop:** if you drag a picture or link from a website onto the shelf, the app downloads that file so
  it can keep it.
- **Browser extension (optional):** it talks to the app only on your own PC (`127.0.0.1`, port 47853), and only
  about tabs that are playing media. It doesn't read pages, history or passwords.

The app doesn't check for updates on its own and doesn't contact the developer.

## Children

The app doesn't collect personal information from anyone, children included.

## Changes

If this policy changes, the new version will be published here with a new date.

## Contact

Questions: open an issue at https://github.com/Prism99-collab/DynamicNotch/issues
