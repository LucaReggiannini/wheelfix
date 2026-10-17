# wheelfix (work in progress)

Stops a worn mouse wheel from scrolling in the wrong direction.

A worn mouse wheel can report steps in the wrong direction, so the page jumps
back while you scroll. WheelFix runs in the background on Windows 10 and 11
and blocks the steps that look like faults. It changes no Windows settings.

## Install

1. Download this repository (**Code → Download ZIP**) and extract it.
2. Double-click `windows\install.bat`.

WheelFix is compiled on your PC with the C# compiler included in Windows, so
there is nothing else to download and no administrator rights are needed. An
icon appears near the clock, and from now on WheelFix starts automatically
with Windows through a shortcut in your Startup folder. To stop that, turn
WheelFix off in Task Manager under Startup apps.

To update, download the new version and run `install.bat` again. Your
settings are kept.

## Use

There is nothing to do. Right-click the icon to pause WheelFix, change its
settings, open the tools or exit.

**Settings** opens `wheelfix.ini` in Notepad. The file explains every option;
save it and WheelFix applies the change within a second. The most common
adjustments:

- The page still jumps the wrong way: set `response = grouped`, or raise
  `confirm-ms`.
- Changing direction without pausing feels slow: lower `confirm-ms` or
  `groups`.
- After a short pause, the first movement feels held back: lower `idle-ms`,
  for example to 500.

Not sure what to change? Let WheelFix measure your wheel: open **Tools** and
run `wheelfix-tools calibrate`.

## Tools

**Tools** opens a console in the WheelFix folder with these commands:

- `wheelfix-tools calibrate [down|up|both]`: records your wheel while you
  turn it in one direction at a time, finds the settings that let the fewest
  wrong steps through and offers to save them.
- `wheelfix-tools debug [FILE]`: shows every wheel event and the decision of
  WheelFix as it happens. With a file name, it also saves them.
- `wheelfix-tools replay FILE [--option value ...] [--verbose]`: shows what
  WheelFix would do with a recording, with your settings or with the options
  given, for example `--response grouped --confirm-ms 600`.
- `wheelfix-tools restore`: puts back the default settings and keeps the old
  file as `wheelfix.ini.bak`.

## Uninstall

Double-click `windows\uninstall.bat`. It closes WheelFix, deletes its shortcut
in the Startup folder and deletes its folder, settings included.

## How it works

The direction you are scrolling in is accepted. A step in the other direction
is held back: it may be a fault, or the start of a real change of direction.
If the new direction keeps coming, WheelFix confirms the change and replays up
to 3 of the held steps. After a pause, the next step is accepted at once,
whatever its direction.

```
You turn:       DOWN  DOWN  DOWN  DOWN  DOWN
Wheel reports:  DOWN  DOWN  UP    DOWN  DOWN
WheelFix:       pass  pass  block pass  pass
```

## Limitations

- Only the vertical wheel is filtered. WheelFix cannot repair the wheel or
  recreate steps the mouse never reported.
- The first step after a pause is always accepted, even if it is a fault.
- Applications running as administrator may ignore the steps WheelFix replays.
