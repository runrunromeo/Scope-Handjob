using System;
using System.Reflection;
using EFT;
using HarmonyLib;

namespace ScopeHandjob
{
    internal static class Patches
    {
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(ScopeZoomHandler), "GetZoomInput"),
                postfix: nameof(ZoomInputPostfix));
            Patch(harmony, AccessTools.Method(typeof(ScopeZoomHandler), "method_4"),
                postfix: nameof(ZoomStepPostfix));
            Patch(harmony, AccessTools.Method(typeof(Player.FirearmController), "SetScopeMode", new[] { typeof(ScopeState[]) }),
                prefix: nameof(SetScopeModePrefix), postfix: nameof(SetScopeModePostfix));
            Patch(harmony, AccessTools.Method(typeof(SightModVisualControllers), "ForceChangeScopeState"),
                prefix: nameof(ForceChangeScopeStatePrefix));
            // Elbow smoothing: after every other AdjustElbows postfix (Stance Overhaul moves the elbow goal there too).
            MethodBase adjustElbows = AccessTools.Method(typeof(Player), "AdjustElbows", new[] { typeof(float) });
            if (adjustElbows == null) throw new MissingMethodException("game method not found for AdjustElbowsPostfix");
            harmony.Patch(adjustElbows, postfix: new HarmonyMethod(typeof(Patches), nameof(AdjustElbowsPostfix)) { priority = Priority.Last });
            Patch(harmony, AccessTools.Method(typeof(Player), "IkProcess", new[] { typeof(float) }),
                prefix: nameof(IkProcessPrefix));
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null)
        {
            if (target == null) throw new MissingMethodException("game method not found for " + (prefix ?? postfix));
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix));
        }

        private static void ZoomInputPostfix(ScopeZoomHandler __instance, ref float __result)
        {
            __result = HandDriver.FilterZoom(__instance, __result);
        }

        private static void ZoomStepPostfix(ScopeZoomHandler __instance)
        {
            HandDriver.AfterZoomStep(__instance);
        }

        private static bool SetScopeModePrefix(Player.FirearmController __instance, ScopeState[] scopeStates)
        {
            bool go = HandDriver.InterceptMode(__instance, scopeStates);
            if (go) HandDriver.BeginModeRequest(__instance, scopeStates); // which sights this change really switches
            return go;
        }

        private static void SetScopeModePostfix()
        {
            HandDriver.EndModeRequest();
        }

        /// <summary>Vanilla snaps every variable scope on the gun on any mode change: only the switched one may snap.</summary>
        private static bool ForceChangeScopeStatePrefix(SightModVisualControllers __instance)
        {
            return !HandDriver.SkipZoomSnap(__instance);
        }

        private static bool elbowErrorLogged;

        private static void AdjustElbowsPostfix(Player __instance)
        {
            try
            {
                HandDriver.AfterAdjustElbows(__instance);
            }
            catch (Exception error)
            {
                if (!elbowErrorLogged) Plugin.Log.LogError(Plugin.LogTag + " elbow smoothing failed, elbow left to the game: " + error.Message);
                elbowErrorLogged = true;
            }
        }

        private static void IkProcessPrefix(Player __instance, float distance2Camera)
        {
            HandDriver.BeforeIk(__instance, distance2Camera);
        }
    }
}
