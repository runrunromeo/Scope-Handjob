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

        // Stance Overhaul pistol Patrol stance holds the left hand off the gun with its own target (user 2026-11-25: the
        // hand should reach from there). We borrow it for the move: the path starts and ends at that hand, then it gets
        // the target back. Doors and other users of the left hand are still left alone.
        private const string StancePatrolHand = "ProceduralLeftHandTarget";
        private static GripPose borrowedHand;

        private static bool Borrowable(GripPose g) => g != null && g.name == StancePatrolHand;

        private static Phase phase = Phase.Idle;
        private static Player player;
        private static Player.FirearmController controller;
        private static ScopeTarget target;
        private static HandSide side;
        private static HandRig rig;
        private static GripPlan plan;
        private static bool leverReverse;      // lever is at the other position than the one its pose was made on
        // Lever / dial with 3+ positions (Walther MRS, BOSS Xe): the position it is at (Levers index), where the hand's
        // turn about the lever's pivot starts this press (degrees from the pose) and how far it pushes (signed: the way the
        // lever really goes this press, at most Lever Twist and never past the lever's own step). The hand does NOT follow
        // the lever all the way: it pushes from the posed spot and comes back to it, so it cannot drift into the scope.
        private static int leverAt;
        private static float leverStart, leverPeak;

        private static bool SteppedLever => plan != null && plan.LeverPush && plan.LeverAngles != null && plan.LeverAngles.Length >= 3;

        /// <summary>Lever with 3+ positions: this press goes from position `at` to the next one (wrapping to the first);
        /// the hand starts at `from` degrees.</summary>
        private static void AimLever(int at, float from)
        {
            int n = plan.LeverAngles.Length;
            leverAt = ((at % n) + n) % n;
            leverStart = from;
            // The short way round: the lever just jumps between positions, so the nearer way is the one it seems to go
            // (BOSS Xe dial: last press 109 / 0 / -140 / -104 -> back to 109 is -147, not +213 the other way).
            float step = Mathf.DeltaAngle(plan.LeverAngles[leverAt], plan.LeverAngles[(leverAt + 1) % n]);
            leverPeak = Mathf.Sign(step) * Mathf.Min(Mathf.Abs(plan.LeverAngle), Mathf.Abs(step));
        }

        /// <summary>Stepped lever: the hand's turn at press progress p, out to the peak at 0.5 (the mode changes there), then
        /// back to the posed spot, so every press is a separate push like a button tap.</summary>
        private static float SteppedLeverAngle(float p)
        {
            p = Mathf.Clamp01(p);
            return p < 0.5f
                ? Mathf.Lerp(leverStart, leverPeak, Ease(p / 0.5f))
                : Mathf.Lerp(leverPeak, 0f, Ease((p - 0.5f) / 0.5f));
        }

        /// <summary>
        /// Button twist that springs back (SUSAT, user 2026-11-25): each press the hand pushes out ButtonTwist degrees and
        /// comes back to the posed spot, like a Walther MRS lever press. One way from the first mode, the other way from the
        /// next (leverReverse). Uses the stepped lever's out-and-back curve (leverStart / leverPeak).
        /// </summary>
        private static bool SpringTwist => plan != null && plan.Twists && plan.TwistBack && target != null && target.Kind == PartKind.Button;

        private static void AimTwist(float from)
        {
            leverStart = from;
            leverPeak = leverReverse ? -plan.ButtonTwist : plan.ButtonTwist;
        }

        private static void SetupLeverSteps(ScopeTarget t)
        {
            if (SpringTwist)
            {
                AimTwist(0f);
                return;
            }
            if (plan == null || plan.LeverAngles == null || plan.LeverAngles.Length < 3 || t.Kind != PartKind.Lever) return;
            AimLever(Mathf.Clamp(t.ModeIndex, 0, plan.LeverAngles.Length - 1), 0f);
        }

        // Stance Overhaul: the scroll that asked for a change, replayed once the gun is out of Short Stock
        private static ScopeZoomHandler replayHandler;
        private static float replayZoom;
        private static float weight;          // 0 = normal grip, 1 = on the scope
        private static float lastStepTime;
        private static bool gunTookHand;      // the game's own left-hand action began mid-move: quick return, no more presses
        private const float QuickReturnTime = 0.15f;
        private static float holdTimer;
        private static float actProgress;     // lever / magnifier: 0..1
        private static bool waitForHand;
        // Going home (1.2.1, a player report 2026-11-28: the hand cut through the gun coming back from a turned knob or a
        // thrown lever / flipped magnifier; the way there was fine). The hand's turn on the part is eased back to the one it
        // arrived at while it lifts off, so the rest of the way is the path it came in on. unwind: 0 = as turned, 1 = back.
        private static bool unwindArmed, captureUnwind;
        private static float unwindAngle, unwind;

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
        private static bool lensDarkens;      // this lever press changes magnification: the scope picture darkens / blurs (LensDarken)

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
            try
            {
                return FilterZoomCore(handler, raw);
            }
            finally
            {
                DropStartTicket(); // a waiting scroll whose turn came but did not start a move gives up its place
            }
        }

        private static float FilterZoomCore(ScopeZoomHandler handler, float raw)
        {
            DropStale();

            // Stance Overhaul Short Stock (not aiming): lower the gun first, the input waits for that.
            if (replayHandler == handler && phase == Phase.Idle && !Mathf.Approximately(replayZoom, 0f))
            {
                raw += replayZoom; // the scroll that asked for the change, now that the gun is lowered
                replayZoom = 0f;
                replayHandler = null;
            }
            // A scroll kept while the hand was busy (HandTurns): its turn has come.
            raw += TakeWaitingZoom(handler);
            // Option: the wheel only zooms while aiming (another mod may use the wheel from the hip).
            if (Settings.WheelOnlyAiming.Value && !Mathf.Approximately(raw, 0f) && !AimingNow()) return 0f;
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
                if (!TryGetMainController(out Player p, out Player.FirearmController fc)) return raw;
                // The hand busy with another move: this scroll waits for it, never zooms without the hand.
                if (phase != Phase.Idle) return Settings.WaitForHand.Value ? WaitZoom(fc, handler, raw, $"busy with another move ({phase})") : raw;
                EFT.Firearms firearms = FirearmsOf(fc);
                SightModVisualControllers owner = ScopeTargets.OwnerOf(firearms, handler);
                if (owner == null || !Alive(p)) return raw;
                ScopeTarget t = ScopeTargets.ForKnob(handler, owner);
                if (t == null || !HasHandFor(t)) return raw; // no hand for this knob at all: the vanilla zoom
                if (!HandTurnNow(p, fc, out string why)) return Settings.WaitForHand.Value ? WaitZoom(fc, handler, raw, why) : raw;
                if (!Begin(p, fc, t)) return raw;
                TookHand("mouse wheel");
                wheelMove = true; // gun shake at the hand's four moments
                zoomHandler = handler;
                Log($"zoom input {raw:0.###} on {t.Name}: hand reaching");
            }
            else if (phase == Phase.Return && !Mathf.Approximately(raw, 0f))
            {
                // Someone waits for the hand: this scroll waits behind them instead of turning the hand back.
                if (!NobodyBehind() && Settings.WaitForHand.Value)
                    return TryGetMainController(out _, out Player.FirearmController gun) ? WaitZoom(gun, handler, raw, "others wait for the hand") : 0f;
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

            // Option: the key only works while aiming. The mod's own replays (a press made while aiming) still go through.
            if (Settings.ModeKeyOnlyAiming.Value && ownCalls == 0 && !fc.IsAiming)
            {
                Log("mode key while not aiming: ignored (Zoom / Mode Key Only While Aiming)");
                return false;
            }

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
                return NobodyBehind() ? ModeKeyDuringStrokes(states) : WaitMode(fc, states, "others wait for the hand");

            // Mode key on the variable scope the hand already holds: act now (unless someone waits for the hand first).
            if (phase == Phase.Hold && target != null && target.Kind == PartKind.Knob && controller == fc)
            {
                if (!NobodyBehind()) return WaitMode(fc, states, "others wait for the hand");
                holdTimer = 0f;
                return true;
            }
            if (phase != Phase.Idle)
            {
                // Pressed again while the hand works a lever / button / magnifier on this gun: the hand does it next.
                if (controller == fc && target != null && target.Kind != PartKind.Knob && waitForHand && NobodyBehind()) return QueuePress(fc, states);
                // Busy with another move (or someone waits for the hand first): this press waits its turn.
                if (Settings.WaitForHand.Value) return WaitMode(fc, states, $"busy with another move ({phase})");
                Log($"mode key while the hand is busy with another move ({phase}): vanilla (Wait For Hand off)");
                return true;
            }

            EFT.Firearms firearms = FirearmsOf(fc);
            foreach (ScopeState state in states)
            {
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, state.Id);
                if (vc == null || vc.SightMod == null) continue;
                ScopeZoomHandler zoom = ScopeTargets.ZoomOf(vc);
                if (!HandForMode(vc, state)) continue;
                if (!Alive(p)) return true;
                ScopeTarget t = ScopeTargets.ForMode(vc);
                if (t == null)
                {
                    Log($"mode change on {vc.name}: no knob / lever / magnifier found, vanilla");
                    return true;
                }
                if (!HasHandFor(t))
                {
                    Log($"mode change on {vc.name}: no pose for {t.Name}, vanilla");
                    return true;
                }
                if (!HandTurnNow(p, fc, out string why))
                {
                    // The hand is busy (another Handjob mod, a door, a reload...): wait for it, never switch without it.
                    if (Settings.WaitForHand.Value) return WaitMode(fc, states, why);
                    Log($"mode change on {vc.name}: no hand now, {why}; vanilla (Wait For Hand off)");
                    return true;
                }
                if (!Begin(p, fc, t)) return true;
                TookHand("mode key");

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

        /// <summary>Your player is aiming down sights (no player / gun: counts as aiming, so nothing is blocked).</summary>
        private static bool AimingNow()
        {
            return !TryGetMainController(out _, out Player.FirearmController fc) || fc.IsAiming;
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
            Phase before = phase;
            if (ShouldStop(p, out bool lostHand))
            {
                if (lostHand || target == null || !target.Alive || plan == null || plan.Anchor == null) { LetGoOfKnob(); Stop(); return; }
                if (phase != Phase.Return) Log("interrupted: hand going back, held change dropped");
                EndSwing();
                if (strokes)
                {
                    // A quick twist cut short stops where the knob is now: no hand, no more turning (user 2026-11-26).
                    if (fastTwist && target.Zoom != null) SetKnob(target.Zoom, ZoomFov(target.Zoom));
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
                    if (holdTimer >= Settings.HoldTime.Value || HandOverNow()) phase = Phase.Return; // someone waits: go now
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
                    // The game took the hand (reload, mag check): back within 0.15 s so its magazine does not float.
                    float returnTime = gunTookHand ? Mathf.Min(Settings.ReturnTime.Value, QuickReturnTime) : Settings.ReturnTime.Value;
                    weight = Mathf.MoveTowards(weight, 0f, dt / Mathf.Max(0.01f, returnTime));
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
            // The hand just left the knob (hold over, someone waiting, interrupted): the knob stops right here.
            if (phase == Phase.Return && before != Phase.Return) LetGoOfKnob();
            // Lever scopes: the lens group inside turns, the picture goes dark / blurred, most at half way (mode change).
            LensDarken.Show(phase == Phase.Act && lensDarkens ? Mathf.Sin(Mathf.PI * Mathf.Clamp01(actProgress)) : 0f, target);
            WatchShake(p);
            // Turned back between weight 0.9 (just lifted off) and 0.5 (the in-between point the reach came through). Also
            // while reaching again after letting go, so the hand goes back onto the part the same way, with no jump.
            // Knob: always by way of the posed twist, the first reach too (the zoom key's quick twist from high to low
            // magnification arrived at the far twist limit and cut through the gun both ways; the posed twist is clean).
            bool turnBack = (unwindArmed || target.Kind == PartKind.Knob) && Settings.TurnBack && (phase == Phase.Return || phase == Phase.Reach);
            unwind = turnBack ? Ease(Mathf.InverseLerp(0.9f, 0.5f, weight)) : 0f;

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
                ? RoundTheGun(p, basePos, via, Ease(e / 0.5f)) // grip <-> in-between point: round the gun's side
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
            if (index == 0 && borrowedHand != null)
            {
                // Pistol Patrol stance: the hand starts from / goes back to where Stance Overhaul holds it.
                pos = borrowedHand.transform.position;
                rot = borrowedHand.transform.rotation;
                return;
            }
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
                    turn = Quaternion.AngleAxis(Unwound(Mathf.Clamp(knobTwist, plan.TwistMin, plan.TwistMax)), plan.Axis);
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
                        // 3+ positions (Walther MRS, BOSS Xe): the hand pushes the lever's way each press (MRS: three one
                        // way, the last back) from the posed spot and comes back, a short push, not the lever's full turn.
                        turn = Quaternion.AngleAxis(Unwound(SteppedLever ? SteppedLeverAngle(actProgress) : amount * throw01), pushAxis);
                        pos = pivot + turn * (pos - pivot);
                        rot = turn * rot;
                        approach = turn * approach;
                        return;
                    }
                    turn = Quaternion.AngleAxis(Unwound(plan.LeverAngle * throw01), plan.Axis); // twist: about the scope's line (left / right)
                    break;
                case PartKind.Button when plan.Twists:
                    {
                        // Knob twist (MARS, Tasco): like the Vudu knob, about the chosen line (Button: Twist Axis); it stays
                        // turned until the next mode turns it back. Spring-back twist (SUSAT): out and back each press.
                        float angle = Unwound(SpringTwist
                            ? SteppedLeverAngle(actProgress)
                            : plan.ButtonTwist * (leverReverse ? 1f - Ease(actProgress) : Ease(actProgress)));
                        Vector3 pivot = anchor.TransformPoint(plan.TwistPivotLocal);
                        turn = Quaternion.AngleAxis(angle, anchor.TransformDirection(plan.TwistAxisLocal).normalized);
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
                        hand = Unwound(hand);
                        plan.Flip.Pose(ref pos, ref rot, hand);
                        approach = plan.Flip.Rotation(hand) * approach;
                    }
                    return;
            }
            pos = centre + turn * (pos - centre);
            rot = turn * rot;
            approach = turn * approach;
        }

        /// <summary>The hand's turn on the part (degrees, or the magnifier's 0..1), eased back towards the one it arrived at
        /// (knob: the posed twist) while going home and while reaching.</summary>
        private static float Unwound(float now)
        {
            if (target.Kind == PartKind.Knob) return Mathf.Lerp(now, 0f, unwind); // knob: the posed twist
            if (captureUnwind)
            {
                unwindAngle = now; // first frame on the part: where the hand arrived
                captureUnwind = false;
            }
            return Mathf.Lerp(now, unwindAngle, unwind);
        }

        private static void Arrive()
        {
            if (!unwindArmed)
            {
                // First arrival this move: remember the hand's turn now (Unwound, this frame). Arriving again after letting go
                // keeps the first one, so going home always ends on the path the reach came in on.
                unwindArmed = true;
                captureUnwind = true;
            }
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
                AimLensDarken();
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
                ForgetPatrolToggle(player);
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
            plan.TwistBack = saved.TwistBack;
            SetupLeverSteps(t);
            SetupStrokes();
            return true;
        }

        private static void ResetMove(Player p, Player.FirearmController fc, ScopeTarget t, HandSide wanted, Vector3 startPos, Quaternion startRot)
        {
            ResetStrokes();
            leverReverse = false;
            unwindArmed = captureUnwind = false;
            unwind = unwindAngle = 0f;
            player = p;
            controller = fc;
            target = t;
            side = wanted;
            waitForHand = Settings.WaitForHand.Value;
            gunTookHand = false;
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

        /// <summary>Act starts: does this press throw a lever that changes magnification (Specter DR, ELCAN 1.5/6, PS-320)?</summary>
        private static void AimLensDarken()
        {
            lensDarkens = target != null && target.Kind == PartKind.Lever && pendingStates != null && controller != null
                          && ChangesAnyMagnification(controller, pendingStates);
        }

        private static void Stop()
        {
            LensDarken.Off();
            lensDarkens = false;
            EndSwing();
            if (player != null && side == HandSide.Left && rig != null && rig.IkTarget != null && LeftTarget(player) == rig.IkTarget)
            {
                if (borrowedHand != null)
                {
                    // Back to Stance Overhaul: its target again at full weight (it fades the weight itself if the
                    // patrol stance ended meanwhile).
                    LeftTarget(player) = borrowedHand;
                    ThirdIkWeight(player).Value = 1f;
                }
                else
                {
                    LeftTarget(player) = null;
                    ThirdIkWeight(player).Value = 0f;
                }
            }
            borrowedHand = null;
            rig?.ReleaseFingers();
            ResetStrokes();
            ResetShake();
            ClearQueuedPresses();
            LetGoOfHand(); // the next one waiting (this mod or another Handjob mod) may have the hand now
            gunTookHand = false;
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

        /// <summary>
        /// Pose editor / background grip search: alive and the hand free (HandTurns.HandFree). A door, Stance Overhaul's
        /// pistol patrol...: the hand is theirs only while they really drive it. Stance Overhaul's LeftHandAnimator never
        /// clears its target after the pistol patrol stance, it only fades ThirdIkWeight to 0, so a target left there with
        /// no weight is nobody's (user 2026-11-25: FN-57, then no hand on any scope).
        /// </summary>
        private static bool CanStart(Player p, Player.FirearmController fc) => Alive(p) && HandFree(p, fc, out _);

        private static bool ShouldStop(Player p, out bool lostHand)
        {
            lostHand = false;
            if (!Settings.Active) return true; // switched off (or the other build took over): hand goes home
            if (side == HandSide.Left)
            {
                // Taken by something that drives it (door...). Before our first frame a leftover target with no weight
                // (Stance Overhaul after a pistol) is still there: not taken, ours replaces it.
                GripPose current = LeftTarget(p);
                if (Borrowable(current) && current != borrowedHand && ThirdIkWeight(p).Value > 0.01f)
                {
                    // Stance Overhaul's patrol hand (now, or put back by it mid-move): ours rides from / back to it.
                    // With no weight it is only a leftover after the patrol stance ended: not borrowed, ours replaces it.
                    borrowedHand = current;
                    Log("left hand borrowed from Stance Overhaul's patrol stance: reaching from there");
                }
                else if (current != null && current != rig.IkTarget && current != borrowedHand && ThirdIkWeight(p).Value > 0.01f) { lostHand = true; return true; }
            }
            if (rig.FingersTakenByOther) { lostHand = true; return true; }
            if (target == null || !target.Alive || controller == null || plan == null || plan.Anchor == null) return true;
            if (!ReferenceEquals(p.HandsController, controller)) return true;
            if (p.HealthController == null || !p.HealthController.IsAlive) return true;
            if (GunNeedsHand())
            {
                // The game's own left-hand action began mid-move (reload, mag / chamber check, malfunction...): it needs the
                // hand now, so ours lets go quickly (user 2026-11-26: the magazine floated until the hand came back).
                if (!gunTookHand) Log($"the gun needs the left hand ({controller.CurrentOperation?.GetType().Name}): hand going back quickly");
                gunTookHand = true;
                return true;
            }
            return p.IsSprintEnabled;
        }

        /// <summary>The gun is reloading or doing something else with the left hand (anything but idle / firing).</summary>
        private static bool GunNeedsHand() =>
            controller != null && (controller.IsInReloadOperation() || !FreeOperation(controller.CurrentOperation));

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
