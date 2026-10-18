# wheelfix

Stops a worn mouse wheel from scrolling in the wrong direction.

A worn wheel sometimes reports a step in the opposite direction, so the page
jumps back while you scroll. WheelFix sits between the mouse and your
programs and blocks those steps. It changes no system settings.

It works on Windows 10 and 11, and on Linux distributions with systemd, such
as Ubuntu, Fedora, Debian and Arch. On Linux it works below the desktop, so
with GNOME, KDE, any other Wayland compositor and X11 alike.

## Install

1. Download this repository (**Code → Download ZIP**) and extract it.
2. Run the installer for your system:
   - **Windows**: double-click `windows\install.bat`. WheelFix is compiled on
     your PC with the C# compiler included in Windows: nothing else to
     download, no administrator rights. It is installed in
     `%LOCALAPPDATA%\WheelFix`.
   - **Linux**: in a terminal, in the extracted folder, run
     `sh linux/install.sh`. It asks for your password. WheelFix uses only the
     Python 3 included in the system: nothing else to download. It is
     installed in `/usr/local/lib/wheelfix` and runs as a system service.

WheelFix starts at once, and from then on every time you start the computer.

To update, run the installer from the new version (your settings are kept).
To remove everything, run `windows\uninstall.bat` or `sh linux/uninstall.sh`.

## Use

There is nothing to do: the defaults suit most wheels.

- The settings are in `wheelfix.ini`. Every option is explained in the file
  itself, and WheelFix applies a change within a second after you save.
- `wheelfix-tools` holds command-line tools for tuning and troubleshooting:
  - `calibrate` measures your wheel and suggests settings;
  - `debug` shows live what WheelFix does with each wheel step;
  - `replay` tests settings on a recording;
  - `restore` puts back the default settings.

  Run `wheelfix-tools` without arguments for the details.

If the page still jumps, or changing direction feels slow, start with
`wheelfix-tools calibrate`.

### Windows

WheelFix runs in the background with an icon near the clock. Right-click the
icon to pause it, open the settings or the tools, or exit.

### Linux

Unlike Windows, there is no tray icon: how tray icons work differs from
desktop to desktop (GNOME, for example, has none without an extension).
Instead:

- Settings: `sudoedit /etc/wheelfix.ini`.
- Pause and resume: `sudo systemctl stop wheelfix` and
  `sudo systemctl start wheelfix`. A pause lasts until the next restart.
- Tools: the `wheelfix-tools` command. `calibrate`, `debug` and `restore` ask
  for your password.

## How it works

The direction you are scrolling in passes at once. A step in the other
direction is held back until WheelFix is sure you really changed direction;
then the held steps are sent after all. After a short pause, the next step is
accepted at once, whatever its direction.

```
You turn:       DOWN  DOWN  DOWN  DOWN  DOWN
Wheel reports:  DOWN  DOWN  UP    DOWN  DOWN
WheelFix:       pass  pass  block pass  pass
```

## Files

In the `windows` folder:

- `install.bat` compiles both programs and installs them; `uninstall.bat`
  removes them.
- `WheelFix.cs` becomes **WheelFix.exe**, the filter with the icon.
- `WheelFixTools.cs` becomes **wheelfix-tools.exe**.
- `WheelFilter.cs` and `WheelHook.cs` hold the filter and the mouse hook,
  shared by both programs.

WheelFix creates `wheelfix.ini` in its own folder.

In the `linux` folder:

- `install.sh` installs WheelFix; `uninstall.sh` removes it.
- `wheelfix.py` is the service. It takes every mouse with a wheel for itself
  and passes its events on through a virtual copy of the mouse, filtering
  the wheel on the way.
- `wheelfix_tools.py` is `wheelfix-tools`.
- `wheelfilter.py` holds the filter and the settings: the same algorithm as
  the Windows version.
- `wheelfix.ini` holds the default settings, installed as
  `/etc/wheelfix.ini`.
- `wheelfix.service` starts WheelFix with the system.

## Limitations

- Only the vertical wheel is filtered, and steps the mouse never reported
  cannot be recreated.
- The first step after a pause is always accepted, even if it is a fault.
- On Windows, programs running as administrator may ignore the steps
  WheelFix sends.
- On Linux, the desktop lists each mouse twice: the real one, which no longer
  sends anything, and the virtual copy, which has the same name.

## License

GPL-3.0, see `LICENSE`.
