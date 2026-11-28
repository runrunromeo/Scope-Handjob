# Scope Handjob: source code

The source code of the Scope Handjob release DLL (`BepInEx/plugins/ScopeHandjob/ScopeHandjob.dll`), a BepInEx
client plugin for SPT 4.1.6.

## Build

1. Install a .NET SDK.
2. Run:

   ```powershell
   ./build.ps1 -GameDir "C:\path\to\your\SPT"
   ```

   The game's own DLLs are referenced from that folder (`EscapeFromTarkov_Data/Managed`, `BepInEx/core`). Nothing is
   downloaded.
3. The result is `bin/Release/ScopeHandjob.dll`.

If the build cannot find .NET Framework 4.7.2 reference assemblies, pass `-FrameworkPath` with a folder that has
them, for example Unity's `MonoBleedingEdge/lib/mono/4.7.2-api`.

## What is where

| File | What it does |
|---|---|
| `src/Patches.cs` | The Harmony hooks into the game: zoom input, mode key, scope-state snap, hand IK. |
| `src/HandDriver.cs` | Moves the left hand: reach, hold or act, return. Holds zoom and mode changes until the hand is there. |
| `src/HandDriver.Path.cs` | The hand's way between the handguard and the scope: round the gun's side, not through it. |
| `src/KnobStrokes.cs` | Mouse-wheel strokes and regrips on a zoom knob, and the mode key's quick twist (or turn in strokes). |
| `src/HandTurns.cs` | Whose turn the left hand is: waits for the game's own hand actions and for other mods, keeps waiting presses. |
| `src/HandQueue.cs` | The left-hand queue shared with other mods (first come, first served; same copy in each mod). |
| `src/PressQueue.cs` | Repeated mode-key presses: at most 4 a second, each one done by the hand. |
| `src/ScopeTargets.cs` | Finds the part the hand goes to: knob, lever, flip magnifier, or a button on the body. |
| `src/Canted.cs` | Canted sights, and which sight the mode key is for. |
| `src/Poses.cs` | The built-in hand poses (`poses.json`) and how a pose is turned into a hand placement. |
| `src/HandRig.cs` | A copy of the hand's bones, used to pose the fingers through the game's own hand poser. |
| `src/Hinge.cs`, `src/MeshReader.cs` | How a lever or magnifier swings, worked out from the scope's meshes. |
| `src/GunShake.cs` | The small gun jolts. |
| `src/StanceSupport.cs` | Optional Stance Overhaul (Realism) Short Stock support, by reflection. |
| `src/Settings.cs` | The F12 settings. |
| `poses.json` | The hand poses, one per scope part. They were made by hand in game with a separate posing build of the mod. |
