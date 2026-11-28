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
        // Where the scroll has taken the zoom (float_2, the target, is rebuilt from it on every scroll).
        private static readonly AccessTools.FieldRef<ScopeZoomHandler, float> ZoomInputAt =
            AccessTools.FieldRefAccess<ScopeZoomHandler, float>("float_4");
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
        private static float fastEnd;         // hand twist this quick twist ends at (a last, shorter stroke stops short of fastTo)
        private static float fastTime;        // seconds for this quick twist
        // "Zoom Key On Knob: Turn In Strokes": the mode key's turn goes in strokes like the mouse wheel, only by itself.
        private static bool sweeping;         // more strokes to come after this one
        private static float sweepFovEnd;     // where the whole turn ends (the game's other end)
        private static float lift;            // 0..1, hand lifted off the knob during a regrip

        private static float KnobT(ScopeZoomHandler z, float fov) => Mathf.InverseLerp(z.MinFov, z.MaxFov, fov);

        private static float FovAt(ScopeZoomHandler z, float t) => Mathf.Lerp(z.MinFov, z.MaxFov, t);

        private static void ResetStrokes()
        {
            strokes = false;
            knobTotal = 0f;
            strokeHand = strokeT = 0f;
            blocked = 0;
            regrip = fastAfterRegrip = fastPending = fastTwist = sweeping = false;
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
            knobTotal = MeasureKnobTotal(z, plan.Axis) * KnobScaleFor(z); // the ring turns scaled (Knob: Ring Turn Scale)
            if (Mathf.Abs(knobTotal) < 1f) return;
            strokes = true;
            frozenFov = ZoomFov(z);
            Log($"knob strokes: knob turns {knobTotal:0} deg from min to max fov, hand twist {plan.TwistMin:0}..{plan.TwistMax:0}");
        }

        /// <summary>Signed degrees the knob turns about axis as t goes 0 -> 1 (the game's own slerp, sampled).</summary>
        private static float MeasureKnobTotal(ScopeZoomHandler z, Vector3 axis)
        {
            if (GameSlerp == null || z.StartPivot == null || z.EndPivot == null) return 0f;
            try
            {
                Quaternion s = z.StartPivot.rotation, e = z.EndPivot.rotation;
                bool shortWay = !z.InverseRotation;
                Quaternion previous = GameSlerp(z, s, e, 0f, shortWay); // delegate: no reflection / boxing per sample
                float total = 0f;
                for (int i = 1; i <= 8; i++)
                {
                    Quaternion q = GameSlerp(z, s, e, i / 8f, shortWay);
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

        // ---------------------------------------------------------------- ring turn scale (per pose)

        private static readonly Func<ScopeZoomHandler, Quaternion, Quaternion, float, bool, Quaternion> GameSlerp =
            KnobSlerp != null ? AccessTools.MethodDelegate<Func<ScopeZoomHandler, Quaternion, Quaternion, float, bool, Quaternion>>(KnobSlerp) : null;

        // knob instance -> (the gun it was looked up on, scale); cleared when a pose is saved or deleted
        private static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.KeyValuePair<object, float>> knobScales =
            new System.Collections.Generic.Dictionary<int, System.Collections.Generic.KeyValuePair<object, float>>();

        /// <summary>
        /// How many times its normal turn this knob's zoom ring turns: the pose's "Knob: Ring Turn Scale" (Leupold Mark 5HD:
        /// the ring turns much further than a hand can, so it is scaled down). Only knobs on your own gun; 1 = the game's turn.
        /// </summary>
        private static float KnobScaleFor(ScopeZoomHandler z)
        {
            if (Settings.ZoomKeyStrokes.Value) return 1f; // turning in strokes: the ring turns as far as the real one (user 2026-11-27)
            if (!Settings.Active) return 1f; // switched off / standing aside: the game's own ring turn
            if (!TryGetMainController(out _, out Player.FirearmController fc)) return 1f;
            EFT.Firearms firearms = FirearmsOf(fc);
            if (firearms == null) return 1f;
            int id = z.GetInstanceID();
            if (knobScales.TryGetValue(id, out var known) && ReferenceEquals(known.Key, firearms)) return known.Value;
            SightModVisualControllers owner = ScopeTargets.OwnerOf(firearms, z);
            if (owner == null) return 1f; // not on your gun (not kept: it may be mounted later)
            ScopeTarget t = ScopeTargets.ForKnob(z, owner);
            SavedPose pose = t != null ? PoseStore.Find(t, Settings.HandToUse, out _) : null;
            float scale = pose != null ? pose.RingScale : 1f;
            if (knobScales.Count > 64) knobScales.Clear();
            knobScales[id] = new System.Collections.Generic.KeyValuePair<object, float>(firearms, scale);
            return scale;
        }

        /// <summary>
        /// ScopeZoomHandler.method_11 postfix: the game just put the ring at Slerp(StartPivot, EndPivot, t). With a ring
        /// scale the ring turns scale times as far, measured from its lowest-magnification end (t = 1, MaxFov), so it sits
        /// as before at the lowest magnification. The zoom itself is the game's, so the whole magnification range stays.
        /// </summary>
        internal static void AfterKnobRotate(ScopeZoomHandler z)
        {
            try
            {
                if (GameSlerp == null || z == null || z.ScopeSwitcher == null || z.StartPivot == null || z.EndPivot == null) return;
                float scale = KnobScaleFor(z);
                if (Mathf.Abs(scale - 1f) < 0.001f) return;
                Quaternion s = z.StartPivot.rotation, e = z.EndPivot.rotation;
                bool shortWay = !z.InverseRotation;
                Quaternion lowest = GameSlerp(z, s, e, 1f, shortWay);
                Quaternion now = GameSlerp(z, s, e, KnobT(z, ZoomFov(z)), shortWay);
                (now * Quaternion.Inverse(lowest)).ToAngleAxis(out float angle, out Vector3 axis);
                if (float.IsNaN(angle) || float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f) return; // at the lowest end: nothing to scale
                z.ScopeSwitcher.rotation = Quaternion.AngleAxis(angle * scale, axis) * lowest;
            }
            catch (Exception error)
            {
                Log("ring turn scale not applied: " + error.Message);
            }
        }

        private static readonly System.Collections.Generic.HashSet<int> knobsPlaced = new System.Collections.Generic.HashSet<int>();

        /// <summary>
        /// GetZoomInput postfix (every frame while aiming through a knob scope): the ring was placed when the scope was
        /// mounted, maybe before it was on your gun, so put it once where its scale says.
        /// </summary>
        internal static void PlaceKnobOnce(ScopeZoomHandler z)
        {
            if (z == null) return;
            int id = z.GetInstanceID();
            if (knobsPlaced.Contains(id)) return;
            if (knobsPlaced.Count > 64) knobsPlaced.Clear();
            knobsPlaced.Add(id);
            if (knobScaleSwitched || Mathf.Abs(KnobScaleFor(z) - 1f) >= 0.001f) RefreshKnob(z);
        }

        private static bool knobScaleSwitched; // "Turn In Strokes" changed this session: rings already placed may sit scaled

        /// <summary>"Zoom Key On Knob: Turn In Strokes" changed: every ring is put where its (now used or not) scale says.</summary>
        internal static void OnZoomKeyStrokesChanged()
        {
            knobScales.Clear();
            knobsPlaced.Clear();
            knobScaleSwitched = true;
            if (zoomHandler != null) RefreshKnob(zoomHandler);
        }

        /// <summary>Puts this knob's ring where its scale says, now (the editor slider moved, a pose was saved).</summary>
        private static void RefreshKnob(ScopeZoomHandler z)
        {
            if (z == null) return;
            try
            {
                RotateKnob?.Invoke(z);
            }
            catch (Exception)
            {
                // the next zoom step puts it right
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
                fastProgress = Mathf.MoveTowards(fastProgress, 1f, dt / Mathf.Max(0.01f, fastTime));
                knobTwist = Mathf.Lerp(fastFrom, fastEnd, Ease(fastProgress));
                holdTimer = 0f;
                if (fastProgress >= 1f)
                {
                    fastTwist = false;
                    SetKnob(z, fastFovTo);
                    knobTwist = fastEnd;
                    NewStroke(z);
                    if (sweeping) StartRegrip(fastFrom, true); // wrist at its limit, ring not at the end: let go, back, grip, next stroke
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

        /// <summary>
        /// The hand leaves the knob: the game's zoom target becomes where the knob is now, so the knob does not keep
        /// turning on its own while the hand is away (it used to carry on once our hold on it ended). Scroll not yet
        /// turned is dropped with it (user 2026-11-26: no hand, no movement).
        /// </summary>
        private static void LetGoOfKnob()
        {
            if (target == null || target.Kind != PartKind.Knob) return;
            ScopeZoomHandler z = target.Zoom ?? zoomHandler;
            if (z == null) return;
            try
            {
                float fov = ZoomFov(z);
                bool turning = Mathf.Abs(ZoomTarget(z) - fov) > 1e-4f;
                // Both, the way the game itself settles the knob (Init, ForceChangeScopeState): the next scroll starts here.
                ZoomTarget(z) = fov;
                ZoomInputAt(z) = fov;
                frozenFov = fov;
                pendingZoom = 0f;
                releaseZoom = false;
                if (turning) Log("hand off the knob: knob stops where it is");
            }
            catch (Exception error)
            {
                Log("knob not stopped: " + error.Message);
            }
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

        /// <summary>
        /// The hand is at the stroke start: switch the mode now (the game jumps the knob), put the knob back and turn it
        /// over smoothly. "Turn In Strokes" on: only as far as the wrist goes (the ring's real turn), then StepStrokes lets
        /// go, grips again at the stroke start and calls this again for the next stroke (the mode is switched only once).
        /// </summary>
        private static void StartFastTwist()
        {
            ScopeZoomHandler z = target.Zoom;
            fastPending = false;
            fastFovFrom = ZoomFov(z);
            bool first = !sweeping;
            if (first)
            {
                ApplyHeldMode();
                sweepFovEnd = ZoomFov(z);
                if (Mathf.Abs(sweepFovEnd - fastFovFrom) < 1e-4f)
                {
                    NewStroke(z);
                    return;
                }
                SetKnob(z, fastFovFrom); // undo the jump on screen this frame
            }
            fastFovTo = sweepFovEnd;
            fastEnd = fastTo;
            fastTime = Settings.FastTwistTime.Value;
            sweeping = false;
            float wrist = Mathf.Abs(fastTo - fastFrom);
            if (Settings.ZoomKeyStrokes.Value && wrist > 1f && Mathf.Abs(knobTotal) > 1f)
            {
                float tFrom = KnobT(z, fastFovFrom), tEnd = KnobT(z, sweepFovEnd);
                float needed = Mathf.Abs(tEnd - tFrom) * Mathf.Abs(knobTotal); // degrees the ring still has to turn
                if (needed > wrist + 0.5f)
                {
                    // A full stroke: the wrist's whole range, the ring with it; more strokes to come.
                    fastFovTo = FovAt(z, tFrom + Mathf.Sign(tEnd - tFrom) * wrist / Mathf.Abs(knobTotal));
                    sweeping = true;
                }
                else
                {
                    // The last stroke: only as far as the ring still goes, at the same wrist speed.
                    fastEnd = fastFrom + Mathf.Sign(fastTo - fastFrom) * needed;
                    fastTime = Settings.FastTwistTime.Value * Mathf.Max(0.15f, needed / wrist);
                }
            }
            fastTwist = true;
            fastProgress = 0f;
            Log($"quick twist{(Settings.ZoomKeyStrokes.Value ? first ? " (in strokes, first)" : " (in strokes, next)" : "")}: hand {fastFrom:0} -> {fastEnd:0} deg, " +
                $"fov {fastFovFrom:0.###} -> {fastFovTo:0.###}{(sweeping ? ", more to come" : "")}");
        }

        /// <summary>
        /// Zoom-mode key while this mod already has the hand on (or on its way to) the knob with strokes.
        /// Returns the value for the SetScopeMode prefix (false = held by the mod).
        /// </summary>
        private static bool ModeKeyDuringStrokes(ScopeState[] states)
        {
            if (regrip || fastTwist || fastPending || sweeping) return false; // busy turning: ignore the extra press
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
