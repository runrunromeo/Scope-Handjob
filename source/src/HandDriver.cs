using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// Moves the main player's hand to the scope, holds magnification changes until it arrives,
    /// then brings the hand back. The grip (where the palm goes, how the fingers close on the part) comes from
    /// the pose saved for that scope part (PoseBuilder). The left hand uses EFT's own left-hand interaction
    /// target (doors use it too). Fingers are posed through EFT's HandPoser with a bone copy (HandRig).
    /// </summary>
    internal static partial class HandDriver
    {
        private enum Phase { Idle, Reach, Hold, Act, Return }

        private static readonly AccessTools.FieldRef<Player, Transform[]> Markers =
            AccessTools.FieldRefAccess<Player, Transform[]>("_markers");
        private static readonly AccessTools.FieldRef<Player, Transform[]> IkTargets =
            AccessTools.FieldRefAccess<Player, Transform[]>("_ikTargets");
        private static readonly AccessTools.FieldRef<Player, Transform[]> GripReferences =
            AccessTools.FieldRefAccess<Player, Transform[]>("_gripReferences");
        private static readonly AccessTools.FieldRef<Player, LimbIK[]> Limbs =
            AccessTools.FieldRefAccess<Player, LimbIK[]>("_limbs");
        private static readonly AccessTools.FieldRef<Player, GripPose> LeftTarget =
            AccessTools.FieldRefAccess<Player, GripPose>("LeftHandInteractionTarget");
        private static readonly AccessTools.FieldRef<Player, Player.BetterValueBlender> ThirdIkWeight =
            AccessTools.FieldRefAccess<Player, Player.BetterValueBlender>("ThirdIkWeight");
        private static readonly AccessTools.FieldRef<Player.ItemHandsController, Player> HandsOwner =
            AccessTools.FieldRefAccess<Player.ItemHandsController, Player>("_player");
        private static readonly AccessTools.FieldRef<Player.FirearmController, EFT.Firearms> FirearmsOf =
            AccessTools.FieldRefAccess<Player.FirearmController, EFT.Firearms>("Firearms");

        /// <summary>True while this mod replays a held mode change, so the patch lets it through.</summary>
        internal static bool Replaying;

        private static Phase phase = Phase.Idle;
        private static Player player;
        private static Player.FirearmController controller;
        private static ScopeTarget target;
        private static HandSide side;
        private static HandRig rig;
        private static GripPlan plan;
        private static bool leverReverse;      // lever is at the other position than the one its pose was made on

        // Stance Overhaul: the scroll that asked for a change, replayed once the gun is out of Short Stock
        private static ScopeZoomHandler replayHandler;
        private static float replayZoom;
        private static float weight;          // 0 = normal grip, 1 = on the scope
        private static float lastStepTime;
        private static float holdTimer;
        private static float actProgress;     // lever / magnifier: 0..1
        private static bool waitForHand;

        // Variable zoom
        private static ScopeZoomHandler zoomHandler;
        private static float pendingZoom;
        private static bool releaseZoom;
        private static bool knobGrabbed;
        private static Quaternion knobGrabLocal;
        private static float knobTwist;

        // Mode change
        private static ScopeState[] pendingStates;
        private static bool modeDone;

        // Thumb-only / index-only flip (a thumb flicking a cover open, an index finger beckoning it shut): which finger,
        // and how far along the flip its tip is carried this frame (relative to the posed spot), applied after the hand is placed
        private static FlipMover fingerTurning;
        private static float fingerFlip;
        private const float IndexCurlMin = -10f, IndexCurlMax = 45f; // HandRig.IndexBeckon a: Tip Bend - a, Base Up / Down - 2a

        // Flip magnifier visual swing
        private static bool swinging;
        private static Transform[] swingPieces;
        private static Vector3[] swingLocalPos, swingSpacePos;
        private static Quaternion[] swingLocalRot, swingSpaceRot;
        private static float swingStart;

        // Result for this frame
        private static Vector3 bonePosition;
        private static Quaternion boneRotation;

        // ---------------------------------------------------------------- requests

        /// <summary>ScopeZoomHandler.GetZoomInput postfix: returns the zoom input the game should use.</summary>
        internal static float FilterZoom(ScopeZoomHandler handler, float raw)
        {
            if (!Settings.Active) return raw;
            DropStale();

            // Stance Overhaul Short Stock (not aiming): lower the gun first, the input waits for that.
            if (replayHandler == handler && phase == Phase.Idle && !Mathf.Approximately(replayZoom, 0f))
            {
                raw += replayZoom; // the scroll that asked for the change, now that the gun is lowered
                replayZoom = 0f;
                replayHandler = null;
            }
            if (!Mathf.Approximately(raw, 0f))
            {
                // Aiming through another sight (e.g. a backup red dot): zoom input is not for this scope.
                if (AimingThroughOtherSight(handler)) return 0f;
                // Hand not on the knob and the knob already at its end this way: nothing to turn, no hand.
                bool handOff = phase == Phase.Idle || (phase == Phase.Return && zoomHandler == handler);
                if (handOff && KnobAtEnd(handler, raw)) return raw;
            }
            if (StanceSupport.Lowering) return 0f; // stance animation playing: input ignored, not kept
            if (!Mathf.Approximately(raw, 0f) && phase == Phase.Idle && TryGetMainController(out _, out Player.FirearmController shortStockGun) &&
                StanceSupport.InShortStockHip(shortStockGun))
            {
                if (!Settings.LowerShortStock.Value) return 0f; // toggle off: no magnification change in Short Stock
                float amount = raw;
                StanceSupport.BeginLowering(shortStockGun, () =>
                {
                    replayHandler = handler;
                    replayZoom = amount;
                });
                return 0f;
            }
            if (!Mathf.Approximately(raw, 0f)) StanceSupport.Touch();

            bool active = phase != Phase.Idle && zoomHandler == handler;

            if (!active)
            {
                if (Mathf.Approximately(raw, 0f)) return raw;
                if (phase != Phase.Idle) return raw;
                if (!TryGetMainController(out Player p, out Player.FirearmController fc)) return raw;
                EFT.Firearms firearms = FirearmsOf(fc);
                SightModVisualControllers owner = ScopeTargets.OwnerOf(firearms, handler);
                if (owner == null || !CanStart(p, fc)) return raw;
                ScopeTarget t = ScopeTargets.ForKnob(handler, owner);
                if (t == null || !Begin(p, fc, t)) return raw;
                wheelMove = true; // gun shake at the hand's four moments
                zoomHandler = handler;
                Log($"zoom input {raw:0.###} on {t.Name}: hand reaching");
            }
            else if (phase == Phase.Return && !Mathf.Approximately(raw, 0f))
            {
                phase = Phase.Reach; // more scrolling while the hand leaves: go back, from where it is
                knobGrabbed = false;
                if (!strokes) knobTwist = 0f; // strokes: keep the twist the hand has, so it does not jump
            }

            if (phase == Phase.Reach)
            {
                if (!waitForHand || strokes) return raw; // strokes: the game keeps the target, the knob waits (AfterZoomStep)
                pendingZoom += raw;
                return 0f;
            }

            if (phase == Phase.Hold)
            {
                if (releaseZoom)
                {
                    raw += pendingZoom;
                    pendingZoom = 0f;
                    releaseZoom = false;
                }
                if (!Mathf.Approximately(raw, 0f)) holdTimer = 0f;
            }
            return raw;
        }

        /// <summary>FirearmController.SetScopeMode prefix: false = held until the hand reaches the scope.</summary>
        internal static bool InterceptMode(Player.FirearmController fc, ScopeState[] states)
        {
            if (Replaying || !Settings.Active || states == null || states.Length == 0) return true;
            Player p = HandsOwner(fc);
            if (p == null || !p.IsYourPlayer) return true;
            DropStale();

            // At most 4 presses a second: a faster press is dropped, so the hand can do every press that counts.
            if (PressTooSoon())
            {
                Log("mode key pressed again within 0.25 s: ignored");
                return false;
            }

            // From the hip the key is for the main sight: a canted sight selected for aiming, or a backup red dot while a
            // scope magnifies, hands the change to that one instead (through this prefix again, with the hand).
            if (!fc.IsAiming)
            {
                ScopeState[] redirected = RedirectFromHip(fc, states);
                if (redirected != null)
                {
                    if (redirected.Length == 0)
                    {
                        Log("mode key from the hip with a canted sight selected: no main sight to switch, ignored");
                        return false;
                    }
                    OwnSetScopeMode(fc, redirected);
                    return false;
                }
            }

            // Nothing to switch (a one-mode sight): no press at all, not even the vanilla press animation.
            if (Settings.IgnoreEmptyModeKey.Value && !ChangesAnything(fc, states))
            {
                Log("mode key: this sight has nothing to switch, ignored");
                return false;
            }

            // Stance Overhaul Short Stock (not aiming): lower the gun first, then the mode change goes through again.
            if (StanceSupport.Lowering) return false; // stance animation playing: ignored, not kept
            if (phase == Phase.Idle && StanceSupport.InShortStockHip(fc) && ChangesAnyZoom(fc, states))
            {
                // Toggle off: no magnification change in Short Stock; a palette / reticle change goes through without the hand.
                if (!Settings.LowerShortStock.Value) return !ChangesAnyMagnification(fc, states);
                ScopeState[] copy = (ScopeState[])states.Clone();
                StanceSupport.BeginLowering(fc, () =>
                {
                    Player owner = HandsOwner(fc);
                    if (owner != null && ReferenceEquals(owner.HandsController, fc)) OwnSetScopeMode(fc, copy); // through this prefix again
                });
                return false;
            }
            StanceSupport.Touch();

            // Player's pose on this knob, hand on it or on its way: the jump becomes one quick twist.
            if (strokes && controller == fc && target != null && target.Kind == PartKind.Knob)
                return ModeKeyDuringStrokes(states);

            // Mode key on the variable scope the hand already holds: act now.
            if (phase == Phase.Hold && target != null && target.Kind == PartKind.Knob && controller == fc)
            {
                holdTimer = 0f;
                return true;
            }
            if (phase != Phase.Idle)
            {
                // Pressed again while the hand works a lever / button / magnifier on this gun: the hand does it next.
                if (controller == fc && target != null && target.Kind != PartKind.Knob && waitForHand) return QueuePress(fc, states);
                return true; // busy with another move: vanilla behaviour
            }

            EFT.Firearms firearms = FirearmsOf(fc);
            foreach (ScopeState state in states)
            {
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, state.Id);
                if (vc == null || vc.SightMod == null) continue;
                ScopeZoomHandler zoom = ScopeTargets.ZoomOf(vc);
                if (!HandForMode(vc, state)) continue;
                if (!CanStart(p, fc)) return true;
                ScopeTarget t = ScopeTargets.ForMode(vc);
                if (t == null)
                {
                    Log($"mode change on {vc.name}: no knob / lever / magnifier found, vanilla");
                    return true;
                }
                if (!Begin(p, fc, t)) return true;

                zoomHandler = t.Kind == PartKind.Knob ? zoom : null;
                keyMove = true; // gun shake: hand leaves the handguard, presses, comes back
                if (strokes)
                {
                    Log($"mode change on {vc.name}: quick twist on {t.Name}");
                    return ModeKeyDuringStrokes(states);
                }
                pendingStates = waitForHand ? (ScopeState[])states.Clone() : null;
                modeDone = !waitForHand;
                Log($"mode change on {vc.name}: {t.Kind} {t.Name}, hand reaching");
                return !waitForHand;
            }
            return true; // reticle-only mode change, or nothing we know
        }

        /// <summary>Does any of these mode changes alter a magnification (not just a reticle)?</summary>
        private static bool ChangesAnyZoom(Player.FirearmController fc, ScopeState[] states)
        {
            EFT.Firearms firearms = FirearmsOf(fc);
            foreach (ScopeState state in states)
            {
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, state.Id);
                if (vc == null || vc.SightMod == null) continue;
                if (HandForMode(vc, state)) return true;
            }
            return false;
        }

        private static bool ChangesAnyMagnification(Player.FirearmController fc, ScopeState[] states)
        {
            EFT.Firearms firearms = FirearmsOf(fc);
            foreach (ScopeState state in states)
            {
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, state.Id);
                if (vc != null && vc.SightMod != null && ScopeTargets.ChangesZoom(vc.SightMod, state, ScopeTargets.ZoomOf(vc))) return true;
            }
            return false;
        }

        /// <summary>
        /// Does this mode change get the hand? Magnification changes always; other mode changes (thermal palette on
        /// the REAP-IR, reticle) only when that scope has a saved pose (dev build: also while the pose editor is on).
        /// </summary>
        private static bool HandForMode(SightModVisualControllers vc, ScopeState state)
        {
            ScopeZoomHandler zoom = ScopeTargets.ZoomOf(vc);
            if (ScopeTargets.ChangesZoom(vc.SightMod, state, zoom)) return true;
            if (zoom != null || !ScopeTargets.ChangesVisibleMode(vc, state)) return false;
            ScopeTarget t = ScopeTargets.ForMode(vc);
            return t != null && PoseStore.Find(t, Settings.HandToUse, out _) != null;
        }

        /// <summary>Aiming, but through a different sight than this knob's scope (e.g. a backup red dot).</summary>
        private static bool AimingThroughOtherSight(ScopeZoomHandler handler)
        {
            if (!TryGetMainController(out Player p, out Player.FirearmController fc) || !fc.IsAiming) return false;
            EFT.InventoryLogic.SightComponent aimed = p.ProceduralWeaponAnimation?.CurrentAimingMod;
            SightModVisualControllers owner = ScopeTargets.OwnerOf(FirearmsOf(fc), handler);
            if (aimed?.Item == null || owner?.SightMod?.Item == null) return false;
            return aimed.Item.Id != owner.SightMod.Item.Id;
        }

        /// <summary>The knob is already at its end in the direction this input turns it: nothing to turn.</summary>
        private static bool KnobAtEnd(ScopeZoomHandler handler, float raw)
        {
            float sensitivity = 1f;
            try
            {
                EFT.InventoryLogic.IAdjustableOpticData data = ZoomOptic(handler);
                if (data != null && !Mathf.Approximately(data.ZoomSensitivity, 0f)) sensitivity = data.ZoomSensitivity;
            }
            catch (Exception)
            {
                // keep 1
            }
            // The game: positive input * sensitivity lowers its zoom target towards MaxFov (method_7 / method_4).
            float edge = raw * sensitivity > 0f ? handler.MaxFov : handler.MinFov;
            float tolerance = Mathf.Abs(handler.MinFov - handler.MaxFov) * 0.001f + 1e-4f;
            return Mathf.Abs(ZoomFov(handler) - edge) <= tolerance && Mathf.Abs(ZoomTarget(handler) - edge) <= tolerance;
        }

        /// <summary>The hand is doing something (for Stance Overhaul support: Short Stock waits for it).</summary>
        internal static bool Busy => phase != Phase.Idle;

        // ---------------------------------------------------------------- per frame (Player.IkProcess)

        internal static void BeforeIk(Player p, float distance2Camera)
        {
            if (phase == Phase.Idle || p != player) return;
            lastStepTime = Time.time;
            try
            {
                Step(p, distance2Camera);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError(Plugin.LogTag + " hand update failed, hand released: " + error);
                Stop();
            }
        }

        private static void Step(Player p, float distance2Camera)
        {
            if (ShouldStop(p, out bool lostHand))
            {
                if (lostHand || target == null || !target.Alive || plan == null || plan.Anchor == null) { Stop(); return; }
                if (phase != Phase.Return) Log("interrupted: hand going back, held change dropped");
                EndSwing();
                if (strokes)
                {
                    // A quick twist already switched the mode: finish the knob at once; anything else just stops.
                    if (fastTwist && target.Zoom != null) SetKnob(target.Zoom, fastFovTo);
                    fastTwist = fastPending = regrip = fastAfterRegrip = false;
                    lift = 0f;
                    if (target.Zoom != null) frozenFov = ZoomFov(target.Zoom);
                }
                pendingStates = null;
                pendingZoom = 0f;
                ClearQueuedPresses();
                phase = Phase.Return;
            }

            float dt = Time.deltaTime;
            switch (phase)
            {
                case Phase.Reach:
                    weight = Mathf.MoveTowards(weight, 1f, dt / Mathf.Max(0.01f, Settings.ReachTime.Value));
                    if (weight >= 1f) Arrive();
                    break;
                case Phase.Hold:
                    holdTimer += dt;
                    if (strokes) StepStrokes(dt);
                    if (holdTimer >= Settings.HoldTime.Value) phase = Phase.Return;
                    break;
                case Phase.Act:
                    actProgress = Mathf.MoveTowards(actProgress, 1f, dt * Speed() / PressDuration());
                    if (target.Kind == PartKind.Magnifier)
                    {
                        if (actProgress >= 1f)
                        {
                            EndSwing();
                            if (!modeDone) ApplyHeldMode();
                        }
                        else Swing(MagnifierAt(actProgress));
                    }
                    else if (!modeDone && actProgress >= 0.5f) ApplyHeldMode();
                    // A kept press: the next one starts as soon as the finger is off the button / the lever is (nearly) over.
                    if (modeDone && queuedPresses > 0 && actProgress >= NextPressAt() && StartQueuedPressInPlace()) break;
                    if (actProgress >= 1f) phase = Phase.Return;
                    break;
                case Phase.Return:
                    weight = Mathf.MoveTowards(weight, 0f, dt / Mathf.Max(0.01f, Settings.ReturnTime.Value));
                    if (weight <= 0f)
                    {
                        BackOnGripShake(p);
                        Player.FirearmController again = controller;
                        ScopeState[] next = TakeQueuedPress(); // magnifier pressed again meanwhile: flip it once more
                        int left = queuedPresses;
                        ScopeState[] template = queuedTemplate;
                        int[] steps = queuedSteps;
                        Stop();
                        ReplayQueuedPressAfterStop(again, next, left, template, steps);
                        return;
                    }
                    break;
            }
            WatchShake(p);

            int index = side == HandSide.Right ? 1 : 0;
            BasePose(p, index, distance2Camera, out Vector3 basePos, out Quaternion baseRot);
            TargetPose(out Vector3 rootPos, out Quaternion rootRot, out Vector3 approach);
            rig.Root.SetPositionAndRotation(rootPos, rootRot); // fingers: EFT blends them in as the hand gets close
            if (fingerTurning != FlipMover.WholeHand && rig.FingersFound)
            {
                rig.LoadFingers(plan.Fingers);
                if (fingerTurning == FlipMover.ThumbOnly)
                {
                    // Thumb flick (a sideways thumbs-up): from the posed finger shape, the tip goes where the part carries it
                    // and the whole thumb swings at its base to get there, the shortest way, so it lifts instead of spinning.
                    Transform thumbBase = rig.Thumb.Joints[0];
                    Vector3 tip = rig.Thumb.Tip;
                    Vector3 carried = plan.Flip.Point(tip, fingerFlip);
                    Vector3 from = tip - thumbBase.position, to = carried - thumbBase.position;
                    if (from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f)
                        thumbBase.rotation = Quaternion.FromToRotation(from, to) * thumbBase.rotation;
                }
                else
                {
                    // "Come here" flick: the index tip goes where the part carries it; Base Up / Down drops twice as fast
                    // as Tip Bend, by as much as follows it most closely.
                    Vector3 tip = rig.Index.Tip;
                    rig.IndexBeckon(plan.Flip.Point(tip, fingerFlip), IndexCurlMin, IndexCurlMax);
                }
            }
            rig.IkBonePose(rootPos, rootRot, out Vector3 endPos, out Quaternion endRot);

            float e = Mathf.Clamp01(weight);
            Vector3 via = endPos + approach;
            bonePosition = e < 0.5f
                ? Vector3.Lerp(basePos, via, Ease(e / 0.5f))
                : Vector3.Lerp(via, endPos, Ease((e - 0.5f) / 0.5f));
            boneRotation = Quaternion.Slerp(baseRot, endRot, Ease(Mathf.Min(1f, e * 1.4f)));

            if (side == HandSide.Left)
            {
                rig.IkTarget.transform.SetPositionAndRotation(bonePosition, boneRotation);
                LeftTarget(p) = rig.IkTarget;
                ThirdIkWeight(p).Value = 1f; // the path above already starts at the normal grip
            }
        }

        /// <summary>Where EFT itself would put this hand this frame (same blend as Player.IkProcess).</summary>
        private static void BasePose(Player p, int index, float distance2Camera, out Vector3 pos, out Quaternion rot)
        {
            Transform[] markers = Markers(p);
            Transform marker = markers != null && markers.Length > index ? markers[index] : null;
            if (marker == null)
            {
                pos = bonePosition;
                rot = boneRotation;
                return;
            }
            pos = marker.position;
            rot = marker.rotation;
            Transform[] targets = IkTargets(p);
            Transform[] references = GripReferences(p);
            if (targets != null && targets.Length > index && targets[index] != null && distance2Camera < 40f &&
                references != null && references.Length > index && references[index] != null)
            {
                float f = Mathf.InverseLerp(0.1f, 0f, Vector3.Distance(marker.position, references[index].position));
                pos = Vector3.Lerp(pos, targets[index].position, f);
                rot = Quaternion.Lerp(rot, targets[index].rotation, f);
            }
        }

        /// <summary>The planned hand-copy root pose this frame, including knob twist / lever turn / magnifier swing.</summary>
        private static void TargetPose(out Vector3 pos, out Quaternion rot, out Vector3 approach)
        {
            Transform anchor = plan.Anchor;
            pos = anchor.TransformPoint(plan.RootPos);
            rot = anchor.rotation * plan.RootRot;
            approach = anchor.TransformVector(plan.ApproachLocal);
            fingerTurning = FlipMover.WholeHand;
            Quaternion turn = Quaternion.identity;
            Vector3 centre = plan.Centre;

            switch (target.Kind)
            {
                case PartKind.Knob:
                    if (!strokes && knobGrabbed && phase == Phase.Hold) knobTwist = KnobAngle();
                    turn = Quaternion.AngleAxis(Mathf.Clamp(knobTwist, plan.TwistMin, plan.TwistMax), plan.Axis);
                    if (lift > 0f)
                    {
                        // Letting go to grip again: lift the hand off the knob a little on the way back.
                        pos = centre + turn * (pos - centre) + (turn * approach).normalized * (LiftMetres * lift);
                        rot = turn * rot;
                        approach = turn * approach;
                        return;
                    }
                    break;
                case PartKind.Lever:
                    // From the lever position the pose was made on: 0 -> LeverAngle. From the other position: back.
                    float throw01 = leverReverse ? 1f - Ease(actProgress) : Ease(actProgress);
                    if (plan.LeverPush && plan.PushAxisLocal.sqrMagnitude > 0.5f)
                    {
                        // Push: the hand rides the lever about the lever's own pivot (Specter DR / PS-320: an upright
                        // pivot, so the lever sweeps forward / back), towards the lever's other position.
                        Vector3 pivot = plan.Anchor.TransformPoint(plan.PushPivotLocal);
                        Vector3 pushAxis = plan.Anchor.TransformDirection(plan.PushAxisLocal).normalized;
                        float amount = plan.PushFromLever ? Mathf.Abs(plan.LeverAngle) : plan.LeverAngle;
                        turn = Quaternion.AngleAxis(amount * throw01, pushAxis);
                        pos = pivot + turn * (pos - pivot);
                        rot = turn * rot;
                        approach = turn * approach;
                        return;
                    }
                    turn = Quaternion.AngleAxis(plan.LeverAngle * throw01, plan.Axis); // twist: about the scope's line (left / right)
                    break;
                case PartKind.Button when plan.Twists:
                    {
                        // Knob twist (SUSAT, MARS): like the Vudu knob, about the chosen line (Button: Twist Axis).
                        float turned = leverReverse ? 1f - Ease(actProgress) : Ease(actProgress);
                        Vector3 pivot = anchor.TransformPoint(plan.TwistPivotLocal);
                        turn = Quaternion.AngleAxis(plan.ButtonTwist * turned, anchor.TransformDirection(plan.TwistAxisLocal).normalized);
                        pos = pivot + turn * (pos - pivot);
                        rot = turn * rot;
                        approach = turn * approach;
                        return;
                    }
                case PartKind.Button:
                    {
                        // Press in (deepest halfway, where the mode changes), then the finger comes off the button.
                        Vector3 dir = plan.PressDirLocal.sqrMagnitude > 0.5f ? anchor.TransformDirection(plan.PressDirLocal).normalized : Vector3.zero;
                        pos += dir * ButtonOffset(actProgress);
                        return;
                    }
                case PartKind.Magnifier:
                    if (plan.Flip != null)
                    {
                        // The pose is at the in-line spot (0); aside is 1. Coming back in line runs 1 -> 0.
                        float f = swinging ? MagnifierAt(actProgress) : modeDone && actProgress >= 1f ? MagnifierAt(1f) : MagnifierAt(0f);
                        // The hand rides only part of the flip if the pose says so (a thumb flicking a lens cover open). A closing
                        // pose is made on the open part (f = 1) and rides the first part of the way back.
                        float hand = plan.CloseFlip ? 1f - (1f - f) * plan.FlipFollow : f * plan.FlipFollow;
                        if (plan.FlipMover != FlipMover.WholeHand)
                        {
                            // Thumb / index flick: the hand stays where it was posed, only that finger moves with the part.
                            float posed = plan.CloseFlip ? 1f : 0f;
                            plan.Flip.Pose(ref pos, ref rot, posed);
                            approach = plan.Flip.Rotation(posed) * approach;
                            fingerFlip = hand - posed;
                            fingerTurning = plan.FlipMover;
                            return;
                        }
                        plan.Flip.Pose(ref pos, ref rot, hand);
                        approach = plan.Flip.Rotation(hand) * approach;
                    }
                    return;
            }
            pos = centre + turn * (pos - centre);
            rot = turn * rot;
            approach = turn * approach;
        }

        private static void Arrive()
        {
            if (target.Kind == PartKind.Knob)
            {
                phase = Phase.Hold;
                holdTimer = 0f;
                releaseZoom = true;
                if (strokes)
                {
                    // The hand arrived at its twist (the pose, or the start of a quick twist): this is where the stroke begins.
                    NewStroke(target.Zoom);
                    if (fastPending) StartFastTwist();
                    return;
                }
                if (target.FollowsKnob)
                {
                    knobGrabbed = true;
                    knobGrabLocal = target.Part.localRotation;
                    knobTwist = 0f;
                }
                if (pendingStates != null) ApplyHeldMode(); // mode key on a variable scope: snap once the hand is on the knob
            }
            else
            {
                // Back on a lever / button after letting go, for a press kept meanwhile: press it again from here.
                if (modeDone && waitForHand && StartQueuedPressInPlace()) return;
                phase = Phase.Act;
                actProgress = 0f;
                if (target.Kind == PartKind.Magnifier && plan.Flip != null && !modeDone)
                {
                    // Magnifier aside (HHS-1 on 1x) coming back in line: switch to the in-line mode FIRST and swing its own
                    // pieces from aside back in, i.e. the in-line -> aside flip played backwards. Switching at the end
                    // (as before) kept the 1x mode, and its red-dot reticle, which the game draws over everything, on
                    // top of the hand for the whole flip.
                    if (target.Reverse && pendingStates != null && target.EndPieces != null && target.EndPieces.Length > 0)
                    {
                        ApplyHeldMode();
                        BeginSwing(target.EndPieces, 0f);
                        Swing(MagnifierAt(0f)); // straight to aside, before this frame is drawn
                        Log("magnifier back in line: mode switched first, in-line pieces swing in from aside");
                    }
                    else BeginSwing();
                }
            }
        }

        /// <summary>Magnifier position along its swing: 0 = in line (where the pose is), 1 = aside.</summary>
        private static float MagnifierAt(float progress)
        {
            float start = target != null && target.Reverse ? 1f : 0f;
            return Mathf.Lerp(start, 1f - start, Ease(progress));
        }

        /// <summary>Remember where every visible magnifier piece is, relative to the hinge's frame.</summary>
        private static void BeginSwing() =>
            BeginSwing(target.Pieces != null && target.Pieces.Length > 0 ? target.Pieces : new[] { target.Part }, MagnifierAt(0f));

        /// <param name="pieces">the parts to move</param>
        /// <param name="piecesAt">where those parts are now along the swing (0 = in line, 1 = aside)</param>
        private static void BeginSwing(Transform[] pieces, float piecesAt)
        {
            Transform space = plan.Flip.Space;
            swingPieces = pieces;
            swingLocalPos = new Vector3[pieces.Length];
            swingLocalRot = new Quaternion[pieces.Length];
            swingSpacePos = new Vector3[pieces.Length];
            swingSpaceRot = new Quaternion[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                if (pieces[i] == null) continue;
                swingLocalPos[i] = pieces[i].localPosition;
                swingLocalRot[i] = pieces[i].localRotation;
                swingSpacePos[i] = space.InverseTransformPoint(pieces[i].position);
                swingSpaceRot[i] = Quaternion.Inverse(space.rotation) * pieces[i].rotation;
            }
            swingStart = piecesAt;
            swinging = true;
        }

        /// <summary>Turns the whole visible magnifier (housing and lenses) to swing position f; the real mode switch happens at the end.</summary>
        private static void Swing(float f)
        {
            if (!swinging || swingPieces == null || plan.Flip == null || plan.Flip.Space == null) return;
            Transform space = plan.Flip.Space;
            float delta = f - swingStart; // pieces start where they are now (aside when coming back)
            for (int i = 0; i < swingPieces.Length; i++)
            {
                if (swingPieces[i] == null) continue;
                plan.Flip.PoseLocal(swingSpacePos[i], swingSpaceRot[i], delta, out Vector3 pos, out Quaternion rot);
                swingPieces[i].SetPositionAndRotation(space.TransformPoint(pos), space.rotation * rot);
            }
        }

        private static void EndSwing()
        {
            if (!swinging) return;
            swinging = false;
            if (swingPieces == null) return;
            for (int i = 0; i < swingPieces.Length; i++)
            {
                if (swingPieces[i] == null) continue;
                swingPieces[i].localPosition = swingLocalPos[i];
                swingPieces[i].localRotation = swingLocalRot[i];
            }
            swingPieces = null;
        }

        private static void ApplyHeldMode()
        {
            modeDone = true;
            ScopeState[] states = pendingStates;
            pendingStates = null;
            if (states == null || controller == null || player == null || !ReferenceEquals(player.HandsController, controller)) return;
            Replaying = true;
            try
            {
                controller.SetScopeMode(states);
                Log("held mode change applied");
                PressShake(player); // the press itself jolts the gun
            }
            finally
            {
                Replaying = false;
            }
        }

        /// <summary>How far the knob has turned about its axis since the hand grabbed it.</summary>
        private static float KnobAngle()
        {
            Transform knob = target.Part;
            Quaternion parent = knob.parent != null ? knob.parent.rotation : Quaternion.identity;
            Quaternion delta = parent * knob.localRotation * Quaternion.Inverse(knobGrabLocal) * Quaternion.Inverse(parent);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f) return 0f;
            if (angle > 180f) angle -= 360f;
            return Vector3.Dot(axis, plan.Axis) >= 0f ? angle : -angle;
        }

        // ---------------------------------------------------------------- state helpers

        /// <summary>Starts a hand move to this scope part with its saved pose. No pose for it: false (vanilla change).</summary>
        private static bool Begin(Player p, Player.FirearmController fc, ScopeTarget t)
        {
            HandSide wanted = HandSide.Left;
            LimbIK[] limbs = Limbs(p);
            if (limbs == null || limbs.Length == 0 || limbs[0] == null) return false;
            IKSolverLimb solver = limbs[0].solver;

            if (rig == null || !rig.Alive || rig.Owner != p || rig.Side != wanted)
            {
                rig?.Destroy();
                rig = HandRig.Build(p, wanted);
                if (rig == null) return false;
            }
            if (rig.FingersTakenByOther) return false;

            EFT.Firearms firearms = FirearmsOf(fc);
            if (firearms == null || WeaponRoot(fc, firearms) == null) return false;

            SavedPose saved = PoseStore.Find(t, wanted, out _);
            // A flip part that is open now (lens cover up): its closing pose, if it has one.
            bool closeFlip = false;
            if (t.Kind == PartKind.Magnifier && t.Reverse)
            {
                SavedPose closing = PoseStore.FindClose(t, wanted, out _);
                if (closing != null)
                {
                    saved = closing;
                    closeFlip = true;
                }
            }
            if (saved == null) return false; // no pose for this scope: the vanilla change, no hand

            rig.Sync(solver.bone3.transform, PoseStore.BaseFor(saved, rig));
            BasePose(p, 0, 0f, out Vector3 startPos, out Quaternion startRot);

            // Lever: the pose belongs to the lever position it was made on; from the other position the move runs backwards.
            Transform poseLever = null;
            if (t.Kind == PartKind.Lever)
            {
                poseLever = t.Part;
                if (t.Levers != null)
                    foreach (Transform l in t.Levers)
                        if (l != null && l.name == saved.Part) poseLever = l;
            }
            PoseFrame frame = PoseFrame.Create(t, fc, ViewTransform(p), wanted, poseLever);
            ResetMove(p, fc, t, wanted, startPos, startRot);
            leverReverse = poseLever != null && poseLever != t.Part;
            // Rotary knob on a button scope: posed in the first mode, so in every other mode it sits turned and turns back.
            if (t.Kind == PartKind.Button) leverReverse = t.ModeIndex % 2 == 1;
            StartMove(PoseBuilder.Build(rig, frame, saved.Values, saved.LeverPush, saved.Face, saved.ButtonTwist, saved.Axis, saved.Follow, saved.Mover));
            plan.CloseFlip = closeFlip;
            SetupStrokes();
            return true;
        }

        private static void ResetMove(Player p, Player.FirearmController fc, ScopeTarget t, HandSide wanted, Vector3 startPos, Quaternion startRot)
        {
            ResetStrokes();
            leverReverse = false;
            player = p;
            controller = fc;
            target = t;
            side = wanted;
            waitForHand = Settings.WaitForHand.Value;
            weight = 0f;
            lastStepTime = Time.time;
            holdTimer = 0f;
            actProgress = 0f;
            pressFrom = 0f;
            repeatPress = false;
            pendingZoom = 0f;
            releaseZoom = false;
            knobGrabbed = false;
            knobTwist = 0f;
            pendingStates = null;
            modeDone = true;
            swinging = false;
            bonePosition = startPos;
            boneRotation = startRot;
        }

        private static void StartMove(GripPlan found)
        {
            plan = found;
            phase = Phase.Reach;
            weight = 0f;
            rig.LoadFingers(plan.Fingers);
            rig.HoldFingers();
        }

        private static Transform WeaponRoot(Player.FirearmController fc, EFT.Firearms firearms)
        {
            WeaponPrefab prefab = firearms.WeaponPrefab;
            if (prefab != null) return prefab.transform;
            GameObject go = fc.ControllerGameObject;
            return go != null ? go.transform : null;
        }

        private static void Stop()
        {
            EndSwing();
            if (player != null && side == HandSide.Left && rig != null && rig.IkTarget != null && LeftTarget(player) == rig.IkTarget)
            {
                LeftTarget(player) = null;
                ThirdIkWeight(player).Value = 0f;
            }
            rig?.ReleaseFingers();
            ResetStrokes();
            ResetShake();
            ClearQueuedPresses();
            phase = Phase.Idle;
            weight = 0f;
            target = null;
            plan = null;
            zoomHandler = null;
            pendingStates = null;
            pendingZoom = 0f;
        }

        /// <summary>A move left over from a player that is gone or no longer updated (raid ended, died).</summary>
        private static void DropStale()
        {
            if (phase == Phase.Idle) return;
            if (player == null || Time.time - lastStepTime > 1f)
            {
                Log("previous hand move was stale, reset");
                Stop();
            }
        }

        private static bool CanStart(Player p, Player.FirearmController fc)
        {
            if (p.IsSprintEnabled || fc.IsInReloadOperation()) return false;
            if (p.HealthController == null || !p.HealthController.IsAlive) return false;
            if (Settings.HandToUse == HandSide.Left)
            {
                GripPose current = LeftTarget(p);
                if (current != null && (rig == null || current != rig.IkTarget)) return false; // door, Realism, ...: leave the hand to them
            }
            return true;
        }

        private static bool ShouldStop(Player p, out bool lostHand)
        {
            lostHand = false;
            if (!Settings.Active) return true; // switched off (or the other build took over): hand goes home
            if (side == HandSide.Left)
            {
                GripPose current = LeftTarget(p);
                if (current != null && current != rig.IkTarget) { lostHand = true; return true; }
            }
            if (rig.FingersTakenByOther) { lostHand = true; return true; }
            if (target == null || !target.Alive || controller == null || plan == null || plan.Anchor == null) return true;
            if (!ReferenceEquals(p.HandsController, controller)) return true;
            if (p.HealthController == null || !p.HealthController.IsAlive) return true;
            return p.IsSprintEnabled || controller.IsInReloadOperation();
        }

        private static bool TryGetMainController(out Player p, out Player.FirearmController fc)
        {
            fc = null;
            p = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            if (p == null) return false;
            fc = p.HandsController as Player.FirearmController;
            return fc != null;
        }

        private static Transform ViewTransform(Player p)
        {
            if (CameraManager.Exist && CameraManager.Instance.Camera != null) return CameraManager.Instance.Camera.transform;
            return Camera.main != null ? Camera.main.transform : p.gameObject.transform;
        }

        private static float Ease(float t) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));

        /// <summary>What the mod is doing, for finding problems (public release: BepInEx debug level, hidden by default).</summary>
        internal static void Log(string message)
        {
            Plugin.Log.LogDebug(Plugin.LogTag + " " + message);
        }
    }
}
