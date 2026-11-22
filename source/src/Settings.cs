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
        private const float DefReach = 0.35f, DefReturn = 0.35f, DefHold = 0.5f, DefSwitch = 0.3f, DefRegrip = 0.35f, DefFastTwist = 0.35f;
        private const float DefShake = 1f, DefCoverSpeed = 2.5f;
        private const string SectionStance = "3. Stance Overhaul", SectionShake = "4. Gun Shake";

        /// <summary>The mod works now: Enabled, and (public release) Scope Handjob Dev is not installed and switched on too.</summary>
        internal static bool Active => Enabled.Value && !OtherBuildActive();

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
