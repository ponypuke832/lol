SHUSH (C#) - how to build WITHOUT Visual Studio
================================================
1. Install the .NET 8 SDK once (free): https://dotnet.microsoft.com/download/dotnet/8.0
   (If you already have Visual Studio with ".NET desktop development", you already have it.)
2. Unzip this folder anywhere.
3. Double-click build.bat
4. Run  release\shush.exe   (configs and logs are saved in release\config)

If build.bat shows an error, copy the red text and send it to Claude.

Default keys:  Insert = hide/show window     F12 = panic exit   (both changeable in Settings > Window)

First run checklist
-------------------
* Settings > In-game keybinds > "Key setup wizard" : press your Tropical Royale keys when asked (~15 sec).
* Settings > Performance : pick your in-game FPS, then "Apply recommended timings".
* Click a macro's key box (the "-" box) to bind it. A macro is active when it has a key.
* Click the three dots on a macro for all its sliders. For Drag Edit / Single Edit use the 3x3 grid to
  choose which tiles it selects, and try the three "Confirm by" modes to see which one Tropical Royale likes.
* Press "Start Macro" (bottom of the sidebar).

Tip: put your own text in release\config\about.txt and it shows in Profiles > About.

If shush ever freezes your input
--------------------------------
* Press Ctrl+Alt+Shift+F12  -> shush closes instantly (works even if the normal panic key does not).
* shush also closes itself if its input hook or SendInput is stuck for 2 seconds.
* Only one copy can run. If it says "already running" and you can't see it: Task Manager -> end every "shush" process.
* Before rebuilding in Visual Studio, make sure no shush.exe is still running (that causes the "file is locked" error).
* Settings > Window > "Log every input sent" writes everything shush sends to config\shush.log.
