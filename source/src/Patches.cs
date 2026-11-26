using System;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace ScopeHandjob
{
    internal static class Patches
    {
        private static Harmony harmonyInstance;

        internal static void Install(Harmony harmony)
        {
            harmonyInstance = harmony;
            Patch(harmony, AccessTools.Method(typeof(ScopeZoomHandler), "GetZoomInput"),
                postfix: nameof(ZoomInputPostfix));
            Patch(harmony, AccessTools.Method(typeof(ScopeZoomHandler), "method_4"),
                postfix: nameof(ZoomStepPostfix));
            Patch(harmony, AccessTools.Method(typeof(ScopeZoomHandler), "method_11"),
                postfix: nameof(KnobRotatePostfix)); // the ring's own turn (Knob: Ring Turn Scale)
            Patch(harmony, AccessTools.Method(typeof(Player.FirearmController), "SetScopeMode", new[] { typeof(ScopeState[]) }),
                prefix: nameof(SetScopeModePrefix), postfix: nameof(SetScopeModePostfix));
            Patch(harmony, AccessTools.Method(typeof(SightModVisualControllers), "ForceChangeScopeState"),
                prefix: nameof(ForceChangeScopeStatePrefix));
            // Guard for mod sights with fewer Zooms than modes (Eukyre RMR HD). Optional: a missing method only loses it.
            MethodBase opticZoom = AccessTools.Method(typeof(SightComponent), "GetCurrentOpticZoom", Type.EmptyTypes);
            if (opticZoom != null) harmony.Patch(opticZoom, prefix: new HarmonyMethod(typeof(Patches), nameof(OpticZoomPrefix)));
            else Plugin.Log.LogWarning(Plugin.LogTag + " SightComponent.GetCurrentOpticZoom not found, short zoom list guard off");
            // Elbow smoothing: after every other AdjustElbows postfix (Stance Overhaul moves the elbow goal there too).
            MethodBase adjustElbows = AccessTools.Method(typeof(Player), "AdjustElbows", new[] { typeof(float) });
            if (adjustElbows == null) throw new MissingMethodException("game method not found for AdjustElbowsPostfix");
            harmony.Patch(adjustElbows, postfix: new HarmonyMethod(typeof(Patches), nameof(AdjustElbowsPostfix)) { priority = Priority.Last });
            ScopeScreens.Install(harmony); // thermal / night vision screens on when not aiming (optional: a missing method only loses it)
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
            HandDriver.PlaceKnobOnce(__instance);
        }

        /// <summary>Only steps in when the sight's Zooms list is too short for its mode; otherwise the game's own code runs.</summary>
        private static bool OpticZoomPrefix(SightComponent __instance, ref float __result)
        {
            try
            {
                if (!ScopeTargets.ShortZoomList(__instance, out float zoom)) return true;
                __result = zoom;
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void KnobRotatePostfix(ScopeZoomHandler __instance)
        {
            HandDriver.AfterKnobRotate(__instance);
        }

        private static void ZoomStepPostfix(ScopeZoomHandler __instance)
        {
            HandDriver.AfterZoomStep(__instance);
        }

        private static bool SetScopeModePrefix(Player.FirearmController __instance, ScopeState[] scopeStates)
        {
            if (overrideDepth > 0) return true; // already decided in a subclass override (Fika) that is calling base now
            bool go = HandDriver.InterceptMode(__instance, scopeStates);
            if (go) HandDriver.BeginModeRequest(__instance, scopeStates); // which sights this change really switches
            return go;
        }

        private static void SetScopeModePostfix()
        {
            if (overrideDepth > 0) return; // the subclass override's finalizer ends the request
            HandDriver.EndModeRequest();
        }

        // ---------------------------------------------------------------- subclass overrides (Fika)

        // Fika's own-player controller overrides SetScopeMode: it sends the change to the other players, THEN calls base.
        // Holding the change only at base let the others see it at the key press (and kept it when we dropped the press).
        // So every override of SetScopeMode gets the same decision first; when it goes ahead, the base hook stands aside.
        private static int overrideDepth;
        private static readonly System.Collections.Generic.Stack<bool> overrideWent = new System.Collections.Generic.Stack<bool>();

        /// <summary>Plugin.Start (every mod loaded): hook SetScopeMode in every subclass that overrides it.</summary>
        internal static void InstallOverrides()
        {
            if (harmonyInstance == null) return;
            Type baseType = typeof(Player.FirearmController);
            string gameAssembly = baseType.Assembly.GetName().Name;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic) continue;
                    if (asm != baseType.Assembly && !Array.Exists(asm.GetReferencedAssemblies(), n => n.Name == gameAssembly)) continue;
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException partial)
                    {
                        types = partial.Types;
                    }
                    foreach (Type t in types)
                    {
                        if (t == null || t == baseType || !t.IsSubclassOf(baseType)) continue;
                        MethodInfo m = t.GetMethod("SetScopeMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, new[] { typeof(ScopeState[]) }, null);
                        if (m == null || m.IsAbstract) continue;
                        harmonyInstance.Patch(m,
                            prefix: new HarmonyMethod(typeof(Patches), nameof(OverridePrefix)),
                            finalizer: new HarmonyMethod(typeof(Patches), nameof(OverrideFinalizer)));
                        Plugin.Log.LogInfo(Plugin.LogTag + " also hooked " + t.FullName + ".SetScopeMode (" + asm.GetName().Name + ")");
                    }
                }
                catch (Exception error)
                {
                    Plugin.Log.LogWarning(Plugin.LogTag + " could not look at " + asm.GetName().Name + " for SetScopeMode overrides: " + error.Message);
                }
            }
        }

        private static bool OverridePrefix(Player.FirearmController __instance, ScopeState[] scopeStates)
        {
            bool go = false;
            try
            {
                go = HandDriver.InterceptMode(__instance, scopeStates);
                if (go)
                {
                    overrideDepth++;
                    HandDriver.BeginModeRequest(__instance, scopeStates);
                }
            }
            finally
            {
                overrideWent.Push(go); // the finalizer always runs, even if this prefix throws
            }
            return go; // false = the override (and its network send) does not run now; the replay goes through later
        }

        private static void OverrideFinalizer()
        {
            if (overrideWent.Count == 0) return;
            if (!overrideWent.Pop()) return;
            overrideDepth--;
            if (overrideDepth == 0) HandDriver.EndModeRequest();
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
