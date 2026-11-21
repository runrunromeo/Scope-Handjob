using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// Stance Overhaul (Realism, spt-stance-overhaul.dll) support, by reflection so the mod also runs without it.
    /// Short Stock while not aiming: a magnification change first lowers the gun to idle (the stance's own
    /// animation), then the change happens (with the hand), then Short Stock comes back. Zoom input while the
    /// stance animation plays is ignored and not replayed. With the toggle off, magnification simply cannot be
    /// changed in that stance.
    /// Uses the same calls Stance Overhaul's own input handler uses: SetChosen(stance) + Reconcile().
    /// </summary>
    internal static class StanceSupport
    {
        private enum State { None, Lowering, Lowered }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const float LowerTimeout = 3f;

        private static bool bound, available;
        private static FieldInfo controllerField, inputHandlerField, stanceStateField, chosenField;
        private static PropertyInfo shortStockProperty, currentTypeProperty;
        private static MethodInfo setChosen, reconcile, poseWeight;
        private static object shortStockType;

        private static State state;
        private static float startedAt, lastActivity;
        private static Action replay;
        private static Player.FirearmController owner;

        /// <summary>The gun is being lowered out of Short Stock: zoom input is ignored.</summary>
        internal static bool Lowering => state == State.Lowering;

        private static bool Bind()
        {
            if (bound) return available;
            bound = true;
            try
            {
                Type plugin = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    plugin = assembly.GetType("StanceOverhaul.Plugin", false);
                    if (plugin == null) continue;
                    Type controller = assembly.GetType("StanceOverhaul.Controllers.StanceController", true);
                    Type input = assembly.GetType("StanceOverhaul.SubSystem.StanceInput.StanceInputHandler", true);
                    Type stanceState = assembly.GetType("StanceOverhaul.State.StanceState", true);
                    Type stance = assembly.GetType("StanceOverhaul.Stances.IStance", true);
                    Type stanceType = assembly.GetType("StanceOverhaul.Enums.EStanceType", true);
                    controllerField = plugin.GetField("StanceControllerInstance", Any);
                    inputHandlerField = controller.GetField("_inputHandler", Any);
                    stanceStateField = controller.GetField("_stanceState", Any);
                    shortStockProperty = controller.GetProperty("ShortStock", Any);
                    currentTypeProperty = controller.GetProperty("CurrentStanceType", Any);
                    chosenField = input.GetField("_chosen", Any);
                    setChosen = input.GetMethod("SetChosen", Any, null, new[] { stance }, null);
                    reconcile = input.GetMethod("Reconcile", Any, null, Type.EmptyTypes, null);
                    poseWeight = stanceState.GetMethod("GetPoseWeight", Any, null, new[] { stanceType }, null);
                    shortStockType = Enum.Parse(stanceType, "ShortStock");
                    break;
                }
                if (plugin == null)
                {
                    HandDriver.Log("Stance Overhaul not installed: stance support off");
                    return available = false;
                }
                available = controllerField != null && inputHandlerField != null && stanceStateField != null && shortStockProperty != null &&
                            currentTypeProperty != null && chosenField != null && setChosen != null && reconcile != null && poseWeight != null;
                if (!available) Plugin.Log.LogWarning(Plugin.LogTag + " Stance Overhaul found but its stance controls changed: stance support off");
                else HandDriver.Log("Stance Overhaul found: Short Stock support on");
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning(Plugin.LogTag + " Stance Overhaul support off: " + error.Message);
                available = false;
            }
            return available;
        }

        private static object Controller => controllerField.GetValue(null);

        private static object Handler
        {
            get
            {
                object c = Controller;
                return c != null ? inputHandlerField.GetValue(c) : null;
            }
        }

        /// <summary>Short Stock is the stance (active or coming in) and the player is not aiming.</summary>
        internal static bool InShortStockHip(Player.FirearmController fc)
        {
            if (fc == null || fc.IsAiming || !Bind()) return false;
            try
            {
                object c = Controller;
                if (c == null) return false;
                object type = currentTypeProperty.GetValue(c, null);
                return type != null && type.Equals(shortStockType);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Lower the gun to idle; once Stance Overhaul's animation is done, replay runs (the zoom change).</summary>
        internal static void BeginLowering(Player.FirearmController fc, Action then)
        {
            object handler = Handler;
            if (handler == null)
            {
                then?.Invoke();
                return;
            }
            setChosen.Invoke(handler, new object[] { null });
            reconcile.Invoke(handler, null);
            state = State.Lowering;
            startedAt = Time.time;
            replay = then;
            owner = fc;
            HandDriver.Log("Short Stock: lowering to idle before the magnification change");
        }

        /// <summary>Any zoom activity (input, the hand moving) keeps Short Stock away a little longer.</summary>
        internal static void Touch()
        {
            if (state == State.Lowered) lastActivity = Time.time;
        }

        /// <summary>Plugin.Update: wait for the stance animation, then bring Short Stock back after the zoom change.</summary>
        internal static void Tick()
        {
            if (state == State.None) return;
            try
            {
                object controller = Controller;
                Player main = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
                if (controller == null || main == null || owner == null || !ReferenceEquals(main.HandsController, owner))
                {
                    Cancel("weapon changed");
                    return;
                }
                object handler = inputHandlerField.GetValue(controller);
                if (state == State.Lowering)
                {
                    object stanceState = stanceStateField.GetValue(controller);
                    float weight = stanceState != null ? (float)poseWeight.Invoke(stanceState, new[] { shortStockType }) : 0f;
                    if (weight > 0.001f && Time.time - startedAt < LowerTimeout) return;
                    state = State.Lowered;
                    lastActivity = Time.time;
                    Action next = replay;
                    replay = null;
                    HandDriver.Log("Short Stock: lowered, changing magnification");
                    next?.Invoke();
                    return;
                }

                // Lowered: the player picking a stance themself ends our job.
                if (handler == null || chosenField.GetValue(handler) != null)
                {
                    Cancel("player chose a stance");
                    return;
                }
                if (HandDriver.Busy) lastActivity = Time.time;
                if (Time.time - lastActivity < Mathf.Max(0.3f, Settings.HoldTime.Value)) return;
                object shortStock = shortStockProperty.GetValue(controller, null);
                if (shortStock != null)
                {
                    // While aiming Stance Overhaul keeps it waiting and brings it back after, as it does for its own stances.
                    setChosen.Invoke(handler, new[] { shortStock });
                    reconcile.Invoke(handler, null);
                    HandDriver.Log("Short Stock: back after the magnification change");
                }
                state = State.None;
                owner = null;
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning(Plugin.LogTag + " Stance Overhaul support stopped: " + error.Message);
                Cancel(null);
                available = false;
            }
        }

        private static void Cancel(string why)
        {
            if (why != null && state != State.None) HandDriver.Log("Short Stock: not coming back (" + why + ")");
            state = State.None;
            replay = null;
            owner = null;
        }
    }
}
