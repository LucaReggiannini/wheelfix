# wheelfix (work in progress)

Stops a worn mouse wheel from scrolling in the wrong direction.

A worn wheel sometimes reports a step in the opposite direction, so the page
jumps back while you scroll. WheelFix sits between the mouse and your
programs and blocks those steps. It changes no system settings.

## Windows

For Windows 10 and 11.

### Install

1. Download this repository (**Code → Download ZIP**) and extract it.
2. Double-click `windows\install.bat`.

WheelFix is compiled on your PC with the C# compiler included in Windows:
nothing else to download, no administrator rights. It is installed in
`%LOCALAPPDATA%\WheelFix` and starts with Windows from then on.

To update, run `install.bat` from the new version (your settings are kept).
To remove everything, run `uninstall.bat`.

### Files

In the `windows` folder:

- `install.bat` compiles both programs and installs them; `uninstall.bat`
  removes them.
- `WheelFix.cs` becomes **WheelFix.exe**, the filter. It runs in the
  background with an icon near the clock. Right-click the icon to pause it,
  open the settings or the tools, or exit.
- `WheelFixTools.cs` becomes **wheelfix-tools.exe**, command-line tools for
  tuning and troubleshooting, opened from **Tools** in the menu:
  - `calibrate` measures your wheel and suggests settings;
  - `debug` shows live what WheelFix does with each wheel step;
  - `replay` tests settings on a recording;
  - `restore` puts back the default settings.

  Run `wheelfix-tools` without arguments for the details.
- `WheelFilter.cs` and `WheelHook.cs` hold the filter and the mouse hook,
  shared by both programs.

Created by WheelFix in its folder:

- `wheelfix.ini`, the settings. The defaults suit most wheels. Every option
  is explained in the file itself, and a change applies as soon as you save.
  Open it from **Settings** in the menu.

If the page still jumps, or changing direction feels slow, start with
`wheelfix-tools calibrate`.

### How it works

The direction you are scrolling in passes at once. A step in the other
direction is held back until WheelFix is sure you really changed direction;
then the held steps are sent after all. After a short pause, the next step is
accepted at once, whatever its direction.

```
You turn:       DOWN  DOWN  DOWN  DOWN  DOWN
Wheel reports:  DOWN  DOWN  UP    DOWN  DOWN
WheelFix:       pass  pass  block pass  pass
```

### Limitations

- Only the vertical wheel is filtered, and steps the mouse never reported
  cannot be recreated.
- The first step after a pause is always accepted, even if it is a fault.
- Programs running as administrator may ignore the steps WheelFix sends.

## Linux

To do.

## License

GPL-3.0, see `LICENSE`.
