<div align="center">

<img src="ui/Assets/ViaTTS.png" width="128" alt="ViaTTS">

# ViaTTS

Text to speech for games and voice chat, with mic passthrough and sound effects, output through a virtual microphone.

[![Release](https://img.shields.io/github/v/release/wlsnD7/ViaTTS)](https://github.com/wlsnD7/ViaTTS/releases/latest)
[![License](https://img.shields.io/github/license/wlsnD7/ViaTTS)](LICENSE)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet)

[Install](#install) · [In-game setup](#in-game-setup) · [Hotkeys](#default-hotkeys) · [Development](#development)

[简体中文](README.md) | English

<table>
  <tr>
    <td><img src="docs/en/home.png" width="400" alt="Home"></td>
    <td><img src="docs/en/phrases.png" width="400" alt="Phrases & sounds"></td>
  </tr>
  <tr>
    <td><img src="docs/en/audio.png" width="400" alt="Audio"></td>
    <td><img src="docs/en/settings.png" width="400" alt="Settings"></td>
  </tr>
</table>

</div>

## Install

- Download `ViaTTS_<version>_setup.exe` from [Releases](https://github.com/wlsnD7/ViaTTS/releases/latest).
- Works on Windows 10 version 19041 or later, and Windows 11.
- Before using it, read the FAQ and disclaimer on the About page in the app.

## Voice engines

| Engine | Notes |
|---|---|
| Edge TTS | Free, needs internet, many voices. |
| System voice | Built into Windows, works offline. |
| GPT-SoVITS (optional) | Add any GPT-SoVITS model (GPT weights + SoVITS weights + reference audio); the version is detected automatically. Needs an NVIDIA GPU and the GPT-SoVITS package. |

Download the GPT-SoVITS package from [lj1995/GPT-SoVITS-windows-package](https://huggingface.co/lj1995/GPT-SoVITS-windows-package/tree/main): choose `GPT-SoVITS-v2pro-20250604-nvidia50.7z` for RTX 50 series, or `GPT-SoVITS-v2pro-20250604.7z` for other NVIDIA GPUs (about 9 GB). To use it, set the package location in GPT-SoVITS folder on the Home page, then click + next to Model to add a model.

## In-game setup

1. Install [VB-Audio Virtual Cable](https://vb-audio.com/Cable/). The app tells you if it's missing.
2. In your game or voice app (Discord, TeamSpeak, etc.), **set the microphone to `CABLE Output`**.
3. The Audio page outputs to `CABLE Input` by default, and also plays to your headphones so you can hear it.
4. To send your microphone along with the speech, turn on Mic passthrough on the Audio page and choose your microphone.
5. If the game uses push to talk, go to Push to talk on the Audio page, click the Key button, then press the game's talk key (a single keyboard key, or the middle or side mouse button). The key is held down while speech plays.
6. Run the game in **borderless windowed** mode. In fullscreen, opening the input box minimizes the game.

## Default hotkeys

| Action | Hotkey |
|---|---|
| Show / hide input box | Ctrl + Alt + T |
| Focus input box | Not set |
| Stop | Ctrl + Alt + X |
| Repeat last | Ctrl + Alt + R |

Closing the main window keeps ViaTTS running in the background. Right-click the tray icon to exit.

## Development

### Structure

```
ViaTTS/
├─ ui/               UI and audio (C# / WinUI 3)
├─ gsv_server.py     GPT-SoVITS synthesis service
├─ installer/        Inno Setup script
├─ tools/            Development scripts (translation check)
├─ design/           Icon generation script
├─ docs/             README screenshots
├─ start.bat         Runs the app in app/
├─ start_debug.bat   Same, with the GPT-SoVITS service console
├─ app/              dotnet publish output (created by the build)
└─ dist/             Installer (created by the build)
```

### Build

Requires .NET 8 SDK and Inno Setup 6:

```
dotnet publish ui -c Release -p:Platform=x64 -o app
ISCC installer\ViaTTS.iss
```


## License

- The code is open source under the [MIT](LICENSE) license.
- Third-party components bundled with the installer and their licenses: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- The icon lettering uses [Yellowtail](https://fonts.google.com/specimen/Yellowtail) (Astigmatic, Apache License 2.0). 
