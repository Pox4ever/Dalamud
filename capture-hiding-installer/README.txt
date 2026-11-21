Dalamud 15.0.4.1 + "Hide Dalamud from OBS Game Capture"
==========================================================

Install
  1. Close the game and XIVLauncher.
  2. Extract this whole zip somewhere, then double-click install.cmd.
  3. Start the game from XIVLauncher as usual.
     Launch it twice: the first launch only learns where to hook.

OBS
  Use a Game Capture source.
  "Capture third-party overlays" OFF = Dalamud/plugin windows hidden from the stream.
  "Capture third-party overlays" ON  = Dalamud/plugin windows shown on the stream.
  (Window Capture / Display Capture record the screen, so they always show them.)

Uninstall
  Close the game, double-click uninstall.cmd.

The option can be toggled in Dalamud Settings > Experimental (click Save, restart the game).
When Dalamud updates past 15.0.4.1, XIVLauncher installs the official new version and this build
stops applying; a new build is needed.

Troubleshooting: send %AppData%\XIVLauncher\dalamud.log (lines containing [CaptureHiding]).
