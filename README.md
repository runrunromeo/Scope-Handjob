# Scope Handjob

**No more remote-control scopes.** In vanilla Tarkov the magnification, reticle or mode of a scope changes by itself, as if by remote control. With this mod your character's **left hand** reaches up to the scope and works it first: turns the zoom knob, throws the lever, presses the button or flips the magnifier. Then the hand goes back to the handguard.

A BepInEx client mod for **SPT 4.1.6**.

## Showcase

**Zoom key on a variable scope (EOTech Vudu):** one quick twist from 1x to 6x. The picture zooms in smoothly instead of jumping like vanilla.

![Vudu zoom key](vudu-zoom-key.gif)

**Mouse wheel (EOTech Vudu):** zoom in and out step by step. The left hand is on the knob for every turn.

![Vudu mouse wheel](vudu-mouse-wheel.gif)

**Thermal scope buttons (FLIR RS-32):** the hand presses the button on top of the scope to change zoom.

![FLIR RS-32 buttons](flir-rs32-buttons.gif)

**Flip magnifier (EOTech HHS-1):** the hand flips the 3x magnifier up and down, both while aiming and from the hip.

![HHS-1 magnifier flip](hhs1-flip.gif)

**Flip-up cover (Aimpoint CS):** finger flips the cover up and down, both while aiming and from the hip.

![Aimpoint CS cover flip](aimpoint-cs-flipcover.gif)

**Lever Scopes (ELCAN Specter DR):** finger pushes and pulls the lever back and forth, picture inside now goes dark and blurry for a moment mid-throw.

![ELCAN Specter DR lever](specterdr-lever.gif)

**Some Other Animations:**

![BOSS XE knob](bossxe-knob.gif)
![ECHO1 front button](echo1-front.gif)
![EKP1S03 knob](ekp1s03-knob.gif)


## What it does

- **Mouse wheel zoom** on variable scopes: the hand grips the zoom knob and turns it with the magnification. If the knob has to turn further than a wrist can, the hand lets go, goes back and grips again.
- **Zoom / mode key** (default `T`):
  - Variable scopes: one quick twist from lowest to highest magnification, or back.
  - Lever scopes (ELCAN Specter DR, Valday PS-320): the hand throws the lever, and the picture through the scope goes dark for a moment mid-throw.
  - EOTech HHS-1 and magnifier combos: the hand flips the magnifier over, and the magnifier moves with the hand.
  - Aimpoint CS (WTT-Armory): the thumb flicks the rear lens cover open, and the index finger flicks it shut again.
  - Sights with buttons (thermals, red dots, reticle or palette changes): the hand presses the button, or turns the knob on SUSAT, MARS and Tasco ProPoint.
- **Thermal / night vision screens** keep their picture when you are not aiming, like a real powered sight (can be turned off, with its own frame rate limit).
- **Waits for the hand:** the change happens when the hand gets there, not before.
- **Fast presses:** the mode key counts at most 4 presses a second. Every press that counts gets its own hand movement.
- **Sights with nothing to switch:** pressing the mode key on a sight with only one mode does nothing at all, with no press animation and no gun wobble.
- **Canted sights** (on a 45° mount): while you aim through the canted sight, the mode key works on the canted sight only. While you are not aiming, it works on the main scope.
- **Gun shake:** the gun gives a small jolt when the hand leaves the handguard, works the scope, and comes back.
- **Stance Overhaul (Realism) support:** in Short Stock while not aiming, the gun is lowered first, the zoom changes, then Short Stock comes back. In the pistol Patrol stance, the hand reaches from where Stance Overhaul holds it. Stance Overhaul is optional.
- **Fika:** teammates see your scope change when your hand makes it, not at the key press.
- **PiP Disabler:** while PiP Disabler is installed and switched on, this mod stands aside and the game changes zoom the vanilla way.

Only your own character's hand moves. Other players and bots are not changed.

## Requirements

- SPT 4.1.6. BepInEx comes with SPT.
- Nothing else. Stance Overhaul, WTT-ContentBackport, WTT-Armory, Epic's All In One and Eukyre's Consortium of Things are optional.

## Install

1. Close the game.
2. Unzip `ScopeHandjob-1.0.0.zip` into your SPT folder, the one that has `EscapeFromTarkov.exe` in it.
3. You should now have `BepInEx/plugins/ScopeHandjob/ScopeHandjob.dll`.

To uninstall, delete the `BepInEx/plugins/ScopeHandjob` folder.

## Settings (F12)

| Section | Setting | What it does |
|---|---|---|
| 1. General | Enabled | Off = vanilla instant changes. |
| | Wait For Hand | On = the change waits for the hand. Off = instant change, and the hand only moves for looks. |
| | Ignore Mode Key When Nothing Changes | On = the mode key does nothing on one-mode sights. |
| | Mouse Wheel Zoom Only While Aiming | On = the mouse wheel changes magnification only while you aim down sights; from the hip the hand stays put. Handy if another mod uses the wheel too, such as Stance Overhaul's Alt + mouse wheel. Off by default. |
| | Zoom / Mode Key Only While Aiming | On = the zoom / mode key works only while you aim down sights. Off by default. |
| | Lever Scope Picture: Darken / Blur | How dark and blurry the picture through a lever scope gets mid-throw. 0 = off. |
| | Thermal / Night Vision Screen When Not Aiming | On = screen scopes keep their picture when you are not aiming. Off = vanilla. On by default. |
| | Thermal / Night Vision Screen FPS When Not Aiming | 10 to 90, default 30: how often that picture is redrawn while not aiming. Lower = more frame rate saved. Aiming is never limited. |
| 2. Timing | Reach / Return Time | How fast the hand goes to the scope and back. |
| | Hold After Zoom | How long the hand stays on the knob after the last scroll. |
| | Switch Time | How long one lever throw, button press or magnifier flip takes. |
| | Regrip Time | How long a regrip on the zoom knob takes. |
| | Fast Twist Time | How long the mode key's quick twist on a variable scope takes. |
| | Lens Cover Speed | How fast the finger flicks a lens cover open or shut (Aimpoint CS). The hand's trip there and back is not affected. |
| 3. Stance Overhaul | Short Stock: Lower To Change Zoom | On = lower the gun to change zoom in Short Stock. Off = no zoom change in Short Stock while not aiming. |
| 4. Gun Shake | Shake When Hand Moves (Mouse Wheel) / Shake When Hand Presses (Mode Key) / Shake Strength | Turn the jolts on or off and set how strong they are. |

## Supported scopes

Each scope has a hand animation made by hand in game. Scopes not on this list change the vanilla way, with no hand.

**Variable zoom or zoom modes:** Armasight Zeus-Pro 640 · Burris FullField TAC30 1-4x24 · DEON March-F 1-8x24 (WTT-ContentBackport) · ELCAN Specter DR 1x/4x (black and FDE) · EOTech HHS-1 (black and tan) · EOTech Vudu 1-6x24 · FLIR RS-32 · Hensoldt FF 4-16x56 · KMZ 1P59 3-10x · KMZ 1P69 3-10x · Leupold Mark 4 LR 6.5-20x50 · Leupold Mark 4 MR/T 2.5-8x36 (WTT-ContentBackport) · Leupold Mark 5HD 5-25x56 · March Tactical 3-24x42 · NcSTAR ADO P4 3-9x42 · Nightforce ATACR 7-35x56 · SIG Sauer ECHO1 · SIG Sauer TANGO6T 1-6x24 · Schmidt & Bender PM II 1-8x24, 3-20x50 and 5-25x56 · Valday PS-320 · Vortex Razor HD Gen.2 1-6x24 · Vortex Viper HS 2.5-10x (Eco Attachment Emporium)

**Lens cover:** Aimpoint CS (WTT-Armory; its mode key flips the rear lens cover open and shut)

**Reticle, brightness, laser or palette modes:** Axion Kobra EKP-1S-03, EKP-8-02 and EKP-8-18 · BAE SUSAT 4x32 (WTT-Armory) · BelOMO PK-06 · BelOMO PSO-1, PSO-1M2 and PSO-1M2-1 · Holosun HS401G5 · ITL MARS (WTT-Armory; its mode turns the IR laser on and off, which you can only see with night vision) · Leapers UTG · Monstrum Compact 2x32 · Monstrum Marksman 3x30 (WTT-ContentBackport) · Trijicon REAP-IR · VOMZ Pilad P1x42 · Walther MRS · Wilcox BOSS Xe

**Epic's All In One:** Burris RT-6 1-6x24 · ELCAN SpecterDR 1.5x/6x · Kahles K624i 6-24x56 · Leupold Mark 5HD 3.6-18x44 (black and FDE) and 5-25x56 (Mil-FDE) · Leupold Mark 8 CQBSS 1.1-8x28 · Nightforce ATACR F1 1-8x24, 4-16x42 and 4-20x50 · Nightforce NXS 3.5-15x50 · Steiner R1X · Trijicon Accupower 1-8x28 · Vortex "Eleanor" AMG 1-10x24 (black and FDE) · Vortex Golden Eagle HD 15-60x52 · magnifier combos: EOTech EXPS3, Leupold LCO, SIG ROMEO8T and Vortex UH-1 with G33 or Unity FTC, Aimpoint T-1 with Unity FTC

**Eukyre's Consortium of Things:** EOTech EXPS3-1 · Holosun HE509T X2 (black and FDE) · SIG BRAVO5 5x30 · SIG ROMEO9T (black and FDE) · Tasco ProPoint 1x42 · Trijicon RMR HD · Vortex M157 1-8x30

## Known limitations

- **Left hand only.**
- The animations were made with **WTT-ContentBackport** installed. That mod replaces the models of several scopes, for example the HHS-1, Vudu, Specter DR and some thermals. Without it, some of those scopes may have no hand, or the hand may not sit exactly on the part.
- A second sight stacked on top of a scope, such as the backup sight on a Specter DR or SUSAT, keeps the vanilla head movement and gets no hand.
- Incompatible with PiP Disabler.
- Fingers might still clip through guns / scopes.
- With Fika, teammates do not see your hand move; they only see the scope change.
- Epic's ROMEO9t switches red / green with the change-sight key (it is two sights), so it gets no hand.
- The gun-shake strengths are tuned by feel.

## Reporting a problem

Please open an issue and include:

- which scope and which gun,
- what you did (mouse wheel or mode key, aiming or not),
- your `BepInEx/LogOutput.log`.

## Disclaimer

This mod's code and the in-game posing tools were built with the help of AI. The hand animations themselves were made by hand: every pose was set and tuned in game manually, using those AI-built tools.
