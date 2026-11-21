using System;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// Knob turning in strokes, for the player's own poses. The game keeps its zoom target (scroll / keys) as usual;
    /// this only limits how far the knob (the scope's current field of view) may move, so the hand twist stays between
    /// the pose's lower and upper limit. At a limit with zoom still to go, the hand lets go, goes back to the other
    /// limit and grips again, then the knob carries on. The zoom-mode key (1x <-> 6x jump) becomes one quick twist.
    /// The knob angle comes from the game's own knob formula: t = InverseLerp(MinFov, MaxFov, fov), rotation =
    /// Slerp(StartPivot, EndPivot, t), which turns at a constant rate, so twist = t * knobTotal.
    /// </summary>
    internal static partial class HandDriver
    {
        private static readonly AccessTools.FieldRef<ScopeZoomHandler, float> ZoomFov =
            AccessTools.FieldRefAccess<ScopeZoomHandler, float>("_filedOfView");
        private static readonly AccessTools.FieldRef<ScopeZoomHandler, float> ZoomTarget =
            AccessTools.FieldRefAccess<ScopeZoomHandler, float>("float_2");
        private static readonly AccessTools.FieldRef<ScopeZoomHandler, SightComponent> ZoomSight =
            AccessTools.FieldRefAccess<ScopeZoomHandler, SightComponent>("_sightComponent");
        private static readonly AccessTools.FieldRef<ScopeZoomHandler, IAdjustableOpticData> ZoomOptic =
            AccessTools.FieldRefAccess<ScopeZoomHandler, IAdjustableOpticData>("_adjustableOpticData");
        private static readonly MethodInfo KnobRotate = AccessTools.Method(typeof(ScopeZoomHandler), "method_11");
        private static readonly MethodInfo KnobSlerp = AccessTools.Method(typeof(ScopeZoomHandler), "Slerp",
            new[] { typeof(Quaternion), typeof(Quaternion), typeof(float), typeof(bool) });

        private static readonly Action<ScopeZoomHandler> RotateKnob =
            KnobRotate != null ? AccessTools.MethodDelegate<Action<ScopeZoomHandler>>(KnobRotate) : null;

        private const float LiftMetres = 0.012f;

        /// <summary>
        /// Puts the knob (and the scope's zoom) at fov now. Needed because the game only steps the knob itself while
        /// aiming: the zoom-mode key also works from the hip, e.g. after Stance Overhaul lowered the gun.
        /// </summary>
        private static void SetKnob(ScopeZoomHandler z, float fov)
        {
            ZoomFov(z) = fov;
            SightComponent sight = ZoomSight(z);
            if (sight != null) sight.ScopeZoomValue = fov;
            try
            {
                RotateKnob?.Invoke(z);
            }
            catch (Exception)
            {
                // the next zoom step puts it right
            }
        }

        private static bool strokes;          // this move turns the knob in strokes
        private static float knobTotal;       // knob degrees from t = 0 to t = 1, about the pose axis
        private static float strokeHand;      // hand twist when this stroke began
        private static float strokeT;         // knob position (0..1) when this stroke began
        private static float frozenFov;       // the knob waits here while the hand is not gripping
        private static int blocked;           // +1 / -1: hand at upper / lower limit with zoom still to go
        private static bool regrip;
        private static float regripFrom, regripTo, regripProgress;
        private static bool fastAfterRegrip;  // the regrip only brings the hand to the start of a quick twist
        private static bool fastPending;      // zoom-mode key held: quick twist once the hand is on the knob
        private static bool fastTwist;
        private static float fastFrom, fastTo, fastFovFrom, fastFovTo, fastProgress;
        private static float lift;            // 0..1, hand lifted off the knob during a regrip

        private static float KnobT(ScopeZoomHandler z, float fov) => Mathf.InverseLerp(z.MinFov, z.MaxFov, fov);

        private static float FovAt(ScopeZoomHandler z, float t) => Mathf.Lerp(z.MinFov, z.MaxFov, t);

        private static void ResetStrokes()
        {
            strokes = false;
            knobTotal = 0f;
            strokeHand = strokeT = 0f;
            blocked = 0;
            regrip = fastAfterRegrip = fastPending = fastTwist = false;
            regripProgress = fastProgress = 0f;
            lift = 0f;
        }

        /// <summary>Begin of a move with the player's pose on a knob.</summary>
        private static void SetupStrokes()
        {
            ResetStrokes();
            ScopeZoomHandler z = target?.Zoom;
            if (target == null || target.Kind != PartKind.Knob || z == null || plan == null || !plan.FromPose) return;
            if (!waitForHand) return; // "Wait For Hand" off: the knob must not wait for anything
            if (plan.TwistMax - plan.TwistMin < 5f) return;
            knobTotal = MeasureKnobTotal(z, plan.Axis);
            if (Mathf.Abs(knobTotal) < 1f) return;
            strokes = true;
            frozenFov = ZoomFov(z);
            Log($"knob strokes: knob turns {knobTotal:0} deg from min to max fov, hand twist {plan.TwistMin:0}..{plan.TwistMax:0}");
        }

        /// <summary>Signed degrees the knob turns about axis as t goes 0 -> 1 (the game's own slerp, sampled).</summary>
        private static float MeasureKnobTotal(ScopeZoomHandler z, Vector3 axis)
        {
            if (KnobSlerp == null || z.StartPivot == null || z.EndPivot == null) return 0f;
            try
            {
                Quaternion s = z.StartPivot.rotation, e = z.EndPivot.rotation;
                bool shortWay = !z.InverseRotation;
                Quaternion previous = (Quaternion)KnobSlerp.Invoke(z, new object[] { s, e, 0f, shortWay });
                float total = 0f;
                for (int i = 1; i <= 8; i++)
                {
                    Quaternion q = (Quaternion)KnobSlerp.Invoke(z, new object[] { s, e, i / 8f, shortWay });
                    total += SignedAngle(q * Quaternion.Inverse(previous), axis);
                    previous = q;
                }
                return float.IsNaN(total) ? 0f : total;
            }
            catch (Exception error)
            {
                Log("knob angle not measurable: " + error.Message);
                return 0f;
            }
        }

        private static float SignedAngle(Quaternion q, Vector3 axis)
        {
            q.ToAngleAxis(out float angle, out Vector3 around);
            if (float.IsNaN(around.x) || around.sqrMagnitude < 1e-6f) return 0f;
            if (angle > 180f)
            {
                angle = 360f - angle;
                around = -around;
            }
            return Vector3.Dot(around, axis) >= 0f ? angle : -angle;
        }

        /// <summary>ScopeZoomHandler.method_4 postfix: the game just moved the knob towards its zoom target; keep it where the hand can follow.</summary>
        internal static void AfterZoomStep(ScopeZoomHandler handler)
        {
            if (!strokes || phase == Phase.Idle || target == null || handler != target.Zoom) return;
            try
            {
                float fov = ZoomFov(handler);
                float set;
                if (fastTwist)
                {
                    set = Mathf.Lerp(fastFovFrom, fastFovTo, Ease(fastProgress));
                }
                else if (phase == Phase.Hold && !regrip && !fastPending)
                {
                    float lo = strokeT + (plan.TwistMin - strokeHand) / knobTotal;
                    float hi = strokeT + (plan.TwistMax - strokeHand) / knobTotal;
                    if (lo > hi) (lo, hi) = (hi, lo);
                    float now = KnobT(handler, fov);
                    float held = Mathf.Clamp(now, Mathf.Max(0f, lo), Mathf.Min(1f, hi));
                    if (Mathf.Abs(held - now) < 1e-5f) return; // inside the hand's range: nothing to do
                    float hand = strokeHand + (held - strokeT) * knobTotal;
                    blocked = hand >= plan.TwistMax - 0.5f ? 1 : hand <= plan.TwistMin + 0.5f ? -1 : (int)Mathf.Sign((now - held) * knobTotal);
                    set = FovAt(handler, held);
                }
                else
                {
                    set = frozenFov; // reaching, letting go / gripping again, going home: the knob waits
                }
                if (Mathf.Abs(set - fov) < 1e-6f) return;
                ZoomFov(handler) = set;
                SightComponent sight = ZoomSight(handler);
                if (sight != null) sight.ScopeZoomValue = set;
            }
            catch (Exception error)
            {
                Plugin.Log.LogError(Plugin.LogTag + " knob control failed, knob left to the game: " + error);
                strokes = false;
            }
        }

        /// <summary>Hold phase, once per frame: follow the knob, let go and grip again at a limit, or run the quick twist.</summary>
        private static void StepStrokes(float dt)
        {
            ScopeZoomHandler z = target.Zoom;
            if (regrip)
            {
                regripProgress = Mathf.MoveTowards(regripProgress, 1f, dt / Mathf.Max(0.01f, Settings.RegripTime.Value));
                knobTwist = Mathf.Lerp(regripFrom, regripTo, Ease(regripProgress));
                lift = Mathf.Sin(Mathf.PI * regripProgress);
                holdTimer = 0f;
                if (regripProgress >= 1f)
                {
                    regrip = false;
                    lift = 0f;
                    knobTwist = regripTo;
                    NewStroke(z);
                    if (fastAfterRegrip)
                    {
                        fastAfterRegrip = false;
                        StartFastTwist();
                    }
                }
                return;
            }
            if (fastTwist)
            {
                fastProgress = Mathf.MoveTowards(fastProgress, 1f, dt / Mathf.Max(0.01f, Settings.FastTwistTime.Value));
                knobTwist = Mathf.Lerp(fastFrom, fastTo, Ease(fastProgress));
                holdTimer = 0f;
                if (fastProgress >= 1f)
                {
                    fastTwist = false;
                    SetKnob(z, fastFovTo);
                    knobTwist = fastTo;
                    NewStroke(z);
                }
                else SetKnob(z, Mathf.Lerp(fastFovFrom, fastFovTo, Ease(fastProgress))); // also from the hip, where the game does not step the knob
                return;
            }
            if (fastPending)
            {
                holdTimer = 0f;
                return;
            }
            if (blocked != 0)
            {
                // At a limit with zoom still to go: let go, back to the other limit, grip again.
                StartRegrip(blocked > 0 ? plan.TwistMin : plan.TwistMax, false);
                blocked = 0;
                return;
            }
            float fov = ZoomFov(z);
            knobTwist = strokeHand + (KnobT(z, fov) - strokeT) * knobTotal;
            frozenFov = fov;
            if (Mathf.Abs(fov - ZoomTarget(z)) > 1e-3f) holdTimer = 0f; // still turning
        }

        private static void NewStroke(ScopeZoomHandler z)
        {
            strokeHand = knobTwist;
            strokeT = KnobT(z, ZoomFov(z));
            frozenFov = ZoomFov(z);
            blocked = 0;
        }

        private static void StartRegrip(float to, bool thenFast)
        {
            ScopeZoomHandler z = target.Zoom;
            frozenFov = ZoomFov(z);
            fastAfterRegrip = thenFast;
            if (Mathf.Abs(to - knobTwist) < 0.5f)
            {
                knobTwist = to;
                NewStroke(z);
                if (thenFast)
                {
                    fastAfterRegrip = false;
                    StartFastTwist();
                }
                return;
            }
            regrip = true;
            regripFrom = knobTwist;
            regripTo = to;
            regripProgress = 0f;
            Log($"regrip: hand {regripFrom:0} -> {regripTo:0} deg");
        }

        /// <summary>
        /// Zoom-mode key on a knob: decide the quick twist's direction the way the game will (it jumps to whichever end
        /// is further away) and return the hand twist the stroke starts from.
        /// </summary>
        private static float PrepareFastTwist()
        {
            ScopeZoomHandler z = target.Zoom;
            float fov = ZoomFov(z);
            float to = Mathf.Abs(fov - z.MinFov) > Mathf.Abs(fov - z.MaxFov) ? z.MinFov : z.MaxFov;
            float direction = Mathf.Sign((KnobT(z, to) - KnobT(z, fov)) * knobTotal);
            fastFrom = direction >= 0f ? plan.TwistMin : plan.TwistMax;
            fastTo = direction >= 0f ? plan.TwistMax : plan.TwistMin;
            fastPending = true;
            return fastFrom;
        }

        /// <summary>The hand is at the stroke start: switch the mode now (the game jumps the knob), put the knob back and turn it over smoothly.</summary>
        private static void StartFastTwist()
        {
            ScopeZoomHandler z = target.Zoom;
            fastPending = false;
            fastFovFrom = ZoomFov(z);
            ApplyHeldMode();
            fastFovTo = ZoomFov(z);
            if (Mathf.Abs(fastFovTo - fastFovFrom) < 1e-4f)
            {
                NewStroke(z);
                return;
            }
            SetKnob(z, fastFovFrom); // undo the jump on screen this frame
            fastTwist = true;
            fastProgress = 0f;
            Log($"quick twist: hand {fastFrom:0} -> {fastTo:0} deg, fov {fastFovFrom:0.###} -> {fastFovTo:0.###}");
        }

        /// <summary>
        /// Zoom-mode key while this mod already has the hand on (or on its way to) the knob with strokes.
        /// Returns the value for the SetScopeMode prefix (false = held by the mod).
        /// </summary>
        private static bool ModeKeyDuringStrokes(ScopeState[] states)
        {
            if (regrip || fastTwist || fastPending) return false; // busy turning: ignore the extra press
            pendingStates = (ScopeState[])states.Clone();
            modeDone = false;
            float start = PrepareFastTwist();
            if (phase == Phase.Hold)
            {
                holdTimer = 0f;
                if (Mathf.Abs(knobTwist - start) < 0.5f) StartFastTwist();
                else StartRegrip(start, true);
            }
            else
            {
                if (phase == Phase.Return) phase = Phase.Reach;
                knobTwist = start; // arrive straight at the stroke start
            }
            return false;
        }
    }
}
