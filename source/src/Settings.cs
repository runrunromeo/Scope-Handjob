using BepInEx.Configuration;

namespace ScopeHandjob
{
    internal enum HandSide
    {
        Left,
        Right,
    }

    internal enum ButtonFace
    {
        Top,
        Right,
        Left,
        Front,
        Rear,
    }

    /// <summary>Button-scope knob twist: the line the hand turns about.</summary>
    internal enum TwistAxis
    {
        ScopeLine, // along the scope (muzzle to eye), through the sight line: like the Vudu knob
        UpDown,    // straight up out of the scope, through the point between thumb and index tips
        LeftRight, // across the scope, through the point between thumb and index tips
    }

    /// <summary>Flip parts: what rides the flip.</summary>
    internal enum FlipMover
    {
        WholeHand,  // the hand rides along (HHS-1 magnifier)
        ThumbOnly,  // the hand stays, the thumb lifts with the part (flicking a cover open)
        IndexOnly,  // the hand stays, the index finger curls with the part (a "come here" flick closing a cover)
    }

    internal static class Settings
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> IgnoreEmptyModeKey;
        internal static ConfigEntry<bool> WaitForHand;
        internal static ConfigEntry<bool> WheelOnlyAiming, ModeKeyOnlyAiming;
        internal static ConfigEntry<float> LeverDarken, LeverBlur;
        internal static ConfigEntry<bool> ScreensAlwaysOn;
        internal static ConfigEntry<int> ScreenFps;

        internal static ConfigEntry<float> ReachTime;
        internal static ConfigEntry<float> ReturnTime;
        internal static ConfigEntry<float> HoldTime;
        internal static ConfigEntry<float> SwitchTime;
        internal static ConfigEntry<float> CoverSpeed;
        internal static ConfigEntry<float> RegripTime, FastTwistTime;

        internal static ConfigEntry<bool> LowerShortStock;
        internal static ConfigEntry<bool> ShakeEnabled;
        internal static ConfigEntry<float> ShakeStrength;
        internal static ConfigEntry<bool> ShakeModeKey;

        // The motion values the hand poses were made with (the dev build lets you change them while posing).
        internal static HandSide HandToUse => HandSide.Left;
        internal const float LeverTwistDegrees = 35f;
        internal const float ButtonPressMm = 5f;

        // A pose's values in order (see the dev build's pose sliders); a pose saved with fewer values gets these.
        private static readonly float[] PoseDefaults =
        {
            70f, 45f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, // around, distance, along, tilt, turn, roll, thumb x3, index x2, others
            0f,                                               // 12 up / down
            -35f, 35f,                                        // 13-14 twist lower / upper limit
            0f, 0f, 0f, 0f, 0f,                               // 15 others tip, 16 left / right, 17 thumb tip, 18 thumb twist, 19 index lift
        };
        internal static int PoseValueCount => PoseDefaults.Length;
        internal static float PoseValueDefault(int i) => PoseDefaults[i];

        // Defaults: the values the hand animations were tuned with.
        private const float DefReach = 0.35f, DefReturn = 0.35f, DefHold = 0.5f, DefSwitch = 0.165f, DefRegrip = 0.35f, DefFastTwist = 0.35f;
        private const float DefShake = 1f, DefCoverSpeed = 2.5f;
        private const string SectionStance = "3. Stance Overhaul", SectionShake = "4. Gun Shake";

        /// <summary>The mod works now: Enabled, (public release) Scope Handjob Dev is not installed and switched on too, and
        /// PiP Disabler is not installed and switched on.</summary>
        internal static bool Active => Enabled.Value && !OtherBuildActive() && !PipDisablerActive();

        // PiP Disabler (com.fiodor.pipdisabler) replaces the scope picture with a zoomed main camera, so the hand on the scope
        // shows up in the view and covers the reticle. While it is installed and its "Mod Enabled" is on, this mod stands
        // aside completely (vanilla zoom / mode changes), whatever our own Enabled says.
        internal const string PipDisablerGuid = "com.fiodor.pipdisabler";
        private static bool pipLoaded;
        private static ConfigEntry<bool> pipEnabled;
        private static bool PipDisablerActive() => pipLoaded && (pipEnabled == null || pipEnabled.Value);

        /// <summary>Called from Plugin.Start, when every plugin has loaded.</summary>
        internal static void FindPipDisabler()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(PipDisablerGuid, out BepInEx.PluginInfo info) || info == null) return;
                pipLoaded = true;
                if (info.Instance != null && info.Instance.Config.TryGetEntry("General", "Mod Enabled", out ConfigEntry<bool> entry))
                    pipEnabled = entry;
                Plugin.Log.LogInfo(Plugin.LogTag + " PiP Disabler is installed: this mod stands aside while PiP Disabler's 'Mod Enabled' is on"
                                   + (pipEnabled == null ? " (setting not found: always)" : $" (now {(pipEnabled.Value ? "on" : "off")})"));
            }
            catch (System.Exception error)
            {
                Plugin.Log.LogWarning(Plugin.LogTag + " could not look for PiP Disabler: " + error.Message);
            }
        }

        // Both builds installed: the dev build wins while its Enabled is on; switch it off and this one takes over.
        internal const string DevGuid = "scopehandjob.dev.spt416";
        private static ConfigEntry<bool> devEnabled;
        private static bool OtherBuildActive() => devEnabled != null && devEnabled.Value;

        /// <summary>Called from Plugin.Start, when every plugin has loaded.</summary>
        internal static void FindDevBuild()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(DevGuid, out BepInEx.PluginInfo info) || info?.Instance == null) return;
                if (info.Instance.Config.TryGetEntry("1. General", "Enabled", out ConfigEntry<bool> entry))
                {
                    devEnabled = entry;
                    Plugin.Log.LogInfo(Plugin.LogTag + " Scope Handjob Dev is installed too: this build stands aside while the dev build's Enabled is on");
                }
            }
            catch (System.Exception error)
            {
                Plugin.Log.LogWarning(Plugin.LogTag + " could not look for Scope Handjob Dev: " + error.Message);
            }
        }

        internal static void Bind(ConfigFile config)
        {
            const string general = "1. General";
            Enabled = config.Bind(general, "Enabled", true,
                new ConfigDescription("Off = vanilla instant magnification change.", null, Order(10)));
            WaitForHand = config.Bind(general, "Wait For Hand", true,
                new ConfigDescription("On = magnification only changes once the hand reaches the scope. Off = changes instantly, the hand is only for looks.", null, Order(8)));
            IgnoreEmptyModeKey = config.Bind(general, "Ignore Mode Key When Nothing Changes", true,
                new ConfigDescription("On = the mode key does nothing at all (no button press animation, no gun wobble) on sights that have only one mode, " +
                                      "so there is nothing to switch. Off = vanilla: the press animation still plays.", null, Order(5)));
            WheelOnlyAiming = config.Bind(general, "Mouse Wheel Zoom Only While Aiming", false,
                new ConfigDescription("On = the mouse wheel changes magnification only while you aim down sights. Not aiming, the wheel does nothing to the " +
                                      "scope and the hand stays on the handguard (useful when another mod, e.g. Stance Overhaul's Alt + mouse wheel, " +
                                      "uses the wheel too). Off = as before.", null, Order(4)));
            ModeKeyOnlyAiming = config.Bind(general, "Zoom / Mode Key Only While Aiming", false,
                new ConfigDescription("On = the zoom / mode key works only while you aim down sights. Not aiming, a press does nothing " +
                                      "(no hand, no press animation). Off = as before.", null, Order(3)));
            LeverDarken = config.Bind(general, "Lever Scope Picture: Darken", 0.85f,
                new ConfigDescription("Scopes whose lever changes magnification (Specter DR, ELCAN 1.5/6, PS-320): inside, a lens group turns " +
                                      "when the lever is thrown, so the picture through the scope goes dark mid-throw. How dark at the darkest " +
                                      "moment (half way, where the magnification changes). 0 = off, 1 = black. Only the picture through the scope.",
                    new AcceptableValueRange<float>(0f, 1f), Order(2)));
            LeverBlur = config.Bind(general, "Lever Scope Picture: Blur", 0.8f,
                new ConfigDescription("Same scopes: how blurred the picture through the scope gets mid-throw. 0 = off, 1 = most.",
                    new AcceptableValueRange<float>(0f, 1f), Order(1)));
            ScreensAlwaysOn = config.Bind(general, "Thermal / Night Vision Screen When Not Aiming", true,
                new ConfigDescription("On = scopes with an electronic screen (thermal: FLIR RS-32, Zeus-Pro, REAP-IR, ECHO1...; night vision " +
                                      "scopes) keep showing their picture when you are not aiming, whether or not you can see the screen from " +
                                      "where you hold the gun. One scope at a time (the first one on the gun). Costs frame rate like aiming " +
                                      "through it. Off = vanilla: the screen is only on while you aim through it.", null, Order(0)));
            ScreenFps = config.Bind(general, "Thermal / Night Vision Screen FPS When Not Aiming", 30,
                new ConfigDescription("How many times a second that screen's picture is redrawn while you are NOT aiming (lower = more frame " +
                                      "rate saved). Aiming through the scope is never limited. Higher than your frame rate = every frame.",
                    new AcceptableValueRange<int>(10, 90), Order(-1)));

            const string timing = "2. Timing";
            ReachTime = config.Bind(timing, "Reach Time", DefReach,
                new ConfigDescription("Seconds for the hand to travel to the scope.", new AcceptableValueRange<float>(0.05f, 1.5f), Order(10)));
            ReturnTime = config.Bind(timing, "Return Time", DefReturn,
                new ConfigDescription("Seconds for the hand to travel back to its normal place.", new AcceptableValueRange<float>(0.05f, 1.5f), Order(9)));
            HoldTime = config.Bind(timing, "Hold After Zoom", DefHold,
                new ConfigDescription("Variable scopes: seconds the hand stays on the knob after the last scroll before going back.", new AcceptableValueRange<float>(0f, 3f), Order(8)));
            SwitchTime = config.Bind(timing, "Switch Time", DefSwitch,
                new ConfigDescription("Lever / flip magnifier: seconds the hand spends moving it. The magnification changes when it is done.", new AcceptableValueRange<float>(0.05f, 1.5f), Order(7)));
            RegripTime = config.Bind(timing, "Regrip Time", DefRegrip,
                new ConfigDescription("Knob (saved poses): seconds the hand takes to let go at a twist limit, go back and grip again.", new AcceptableValueRange<float>(0.05f, 1.5f), Order(6)));
            FastTwistTime = config.Bind(timing, "Fast Twist Time", DefFastTwist,
                new ConfigDescription("Knob (saved poses): seconds for the zoom-mode key's one quick twist from lowest to highest magnification (or back).", new AcceptableValueRange<float>(0.05f, 1.5f), Order(5)));
            CoverSpeed = config.Bind(timing, "Lens Cover Speed", DefCoverSpeed,
                new ConfigDescription("Flip-up lens covers only (e.g. Aimpoint CS): how fast the finger flicks the cover open / shut. " +
                                      "1 = Switch Time, 2 = twice as fast, 0.5 = half as fast. The hand reaching there and going back " +
                                      "(Reach / Return Time) and other scopes are not affected. Default " + DefCoverSpeed + ".",
                    new AcceptableValueRange<float>(0.5f, 3f), Order(4)));

            const string stance = SectionStance;
            LowerShortStock = config.Bind(stance, "Short Stock: Lower To Change Zoom", true,
                new ConfigDescription("Needs Stance Overhaul. In Short Stock while not aiming: " +
                                      "On = changing magnification first lowers the gun to idle (zoom input during that animation is ignored and not kept), " +
                                      "then the magnification changes, then Short Stock comes back. " +
                                      "Off = magnification cannot be changed in Short Stock while not aiming (HHS-1 magnifier included).", null, Order(10)));

            const string shake = SectionShake;
            ShakeEnabled = config.Bind(shake, "Shake When Hand Moves (Mouse Wheel)", true,
                new ConfigDescription("Mouse-wheel zoom: the gun gives a small jolt, like pressing a button on it, at four moments: " +
                                      "the hand leaves the handguard, touches the scope, leaves the scope, and is back on the handguard.", null, Order(10)));
            ShakeModeKey = config.Bind(shake, "Shake When Hand Presses (Mode Key)", true,
                new ConfigDescription("Mode key (button / lever / flip magnifier / quick knob twist): the gun jolts when the hand leaves the handguard, " +
                                      "at every press (when the mode switches), and when the hand is back on the handguard.", null, Order(9)));
            ShakeStrength = config.Bind(shake, "Shake Strength", DefShake,
                new ConfigDescription("How strong the jolt is, for the mouse wheel and the mode key alike. 0 = none, 3 = strongest. Default " + DefShake + ".",
                    new AcceptableValueRange<float>(0f, 3f), Order(8)));
        }

        private static ConfigurationManagerAttributes Order(int order) => new ConfigurationManagerAttributes { Order = order };
    }

    // Recognised by name by BepInEx ConfigurationManager.
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }
}
