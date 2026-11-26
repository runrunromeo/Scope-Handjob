using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;

namespace ScopeHandjob
{
    /// <summary>
    /// Scopes with an electronic screen (thermal: FLIR RS-32, Zeus-Pro, REAP-IR, ECHO1...; night vision: NSPU-M...) keep
    /// their picture when you are not aiming, like a real powered sight. The game turns every scope's picture on and off in
    /// ProceduralWeaponAnimation.method_1 (aim / pose / mode / weapon changes): only the one you aim through gets the
    /// optic camera. It already leaves one on when nothing is aimed for modes marked IgnoreOpticsForCameraPlane; this does
    /// the same for the first screen scope on your gun, whenever no scope picture is on (not aiming, or aiming a red dot).
    /// There is one optic camera, so one scope picture at a time. Costs about what aiming through it costs.
    /// Any error switches it off for the session.
    /// </summary>
    internal static class ScopeScreens
    {
        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, List<ProceduralWeaponAnimation.SightNBone>> Optics =
            AccessTools.FieldRefAccess<ProceduralWeaponAnimation, List<ProceduralWeaponAnimation.SightNBone>>("_optics");

        private static OpticSight shown; // the screen we switched on (the game's own pass switches it off again)
        private static bool broken;
        private static System.Reflection.MethodInfo gamePass; // ProceduralWeaponAnimation.method_1

        // Off-ADS frame rate cap: the optic camera draws into one render texture, so on a skipped frame the screen keeps
        // its last picture. Only while the optic camera is drawing OUR screen (aiming switches it back to every frame).
        private static UnityEngine.Camera throttled;
        private static float sinceDraw;

        /// <summary>Plugin.Update (before the cameras draw): the off-ADS screen's camera on only on its own frames.</summary>
        internal static void Tick()
        {
            if (broken || (shown == null && throttled == null)) return;
            try
            {
                OpticCameraManager optics = shown != null ? CameraManager.Instance?.OpticCameraManager : null;
                UnityEngine.Camera camera = optics?.Camera;
                if (camera == null || optics.CurrentOpticSight != shown)
                {
                    Unthrottle(); // our screen is not the one drawn now (aiming, gone)
                    return;
                }
                if (throttled != camera)
                {
                    Unthrottle();
                    throttled = camera;
                    sinceDraw = float.MaxValue; // draw at once
                }
                float interval = 1f / UnityEngine.Mathf.Clamp(Settings.ScreenFps.Value, 10, 90);
                sinceDraw = sinceDraw == float.MaxValue ? interval : sinceDraw + UnityEngine.Time.unscaledDeltaTime;
                bool draw = sinceDraw >= interval;
                if (draw) sinceDraw = UnityEngine.Mathf.Min(sinceDraw - interval, interval); // no burst after a long frame
                if (camera.enabled != draw) camera.enabled = draw;
            }
            catch (Exception error)
            {
                Break(error);
            }
        }

        private static void Unthrottle()
        {
            UnityEngine.Camera camera = throttled;
            throttled = null;
            if (camera != null && !camera.enabled) camera.enabled = true;
        }

        private static bool Wanted => Settings.ScreensAlwaysOn.Value && Settings.Active;

        internal static void Install(Harmony harmony)
        {
            var refresh = AccessTools.Method(typeof(ProceduralWeaponAnimation), "method_1");
            gamePass = refresh;
            if (refresh == null)
            {
                Plugin.Log.LogWarning(Plugin.LogTag + " ProceduralWeaponAnimation.method_1 not found, scope screens stay vanilla");
                return;
            }
            harmony.Patch(refresh,
                prefix: new HarmonyMethod(typeof(ScopeScreens), nameof(Before)),
                postfix: new HarmonyMethod(typeof(ScopeScreens), nameof(After)));
            Settings.ScreensAlwaysOn.SettingChanged += (_, __) => Refresh();
            Settings.Enabled.SettingChanged += (_, __) => Refresh();
        }

        /// <summary>Before the game's pass: our screen off first, so a scope you now aim through is switched on after it
        /// (switching one off clears the reticle drawing of whichever is on).</summary>
        private static void Before(ProceduralWeaponAnimation __instance)
        {
            if (broken || shown == null) return;
            try
            {
                if (!IsYours(__instance)) return;
                Hide();
            }
            catch (Exception error)
            {
                Break(error);
            }
        }

        /// <summary>After the game's pass: no scope picture on -> the first screen scope's current mode gets it.</summary>
        private static void After(ProceduralWeaponAnimation __instance)
        {
            if (broken) return;
            try
            {
                if (!Wanted || !IsYours(__instance) || !__instance.FirstPersonPointOfView) return;
                List<ProceduralWeaponAnimation.SightNBone> optics = Optics(__instance);
                if (optics == null) return;
                OpticSight pick = null;
                foreach (ProceduralWeaponAnimation.SightNBone sight in optics)
                {
                    ScopePrefabCache cache = sight?.ScopePrefabCache;
                    if (cache == null) continue;
                    for (int i = 0; i < cache.ModesCount; i++)
                    {
                        OpticSight o = cache.GetOpticSight(i);
                        if (o != null && o.enabled) return; // the game shows a scope picture already (you aim through it)
                    }
                    if (pick != null) continue;
                    OpticSight current = cache.CurrentModOpticSight;
                    if (current != null && current.gameObject.activeInHierarchy && IsScreen(current)) pick = current;
                }
                if (pick == null) return;
                pick.enabled = true;
                pick.LensFade(false);
                shown = pick;
            }
            catch (Exception error)
            {
                Break(error);
            }
        }

        private static bool IsScreen(OpticSight optic)
        {
            ScopeData data = optic.ScopeData;
            if (data == null) return false;
            return (data.ThermalVisionData != null && data.ThermalVisionData.ThermalVision)
                   || (data.NightVisionData != null && data.NightVisionData.NightVision);
        }

        private static bool IsYours(ProceduralWeaponAnimation pwa)
        {
            Player main = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            return main != null && ReferenceEquals(main.ProceduralWeaponAnimation, pwa);
        }

        private static void Hide()
        {
            Unthrottle(); // the camera draws every frame again before another scope gets it
            OpticSight o = shown;
            shown = null;
            if (o == null) return; // destroyed with its weapon
            if (o.enabled) o.enabled = false; // its OnDisable fades the lens and frees the optic camera
            else o.LensFade(true);
        }

        /// <summary>Setting changed: run the game's pass again so the screen goes on / off now.</summary>
        private static void Refresh()
        {
            if (broken) return;
            try
            {
                Player main = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
                ProceduralWeaponAnimation pwa = main != null ? main.ProceduralWeaponAnimation : null;
                if (pwa != null) gamePass?.Invoke(pwa, null);
                else if (shown != null) Hide();
            }
            catch (Exception error)
            {
                Break(error);
            }
        }

        private static void Break(Exception error)
        {
            broken = true;
            try
            {
                Hide();
            }
            catch (Exception)
            {
                // already gone
            }
            Plugin.Log.LogWarning(Plugin.LogTag + " thermal / night vision screen when not aiming switched off: " + error.Message);
        }
    }
}
