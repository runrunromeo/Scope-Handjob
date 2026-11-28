using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// The left hand is shared with other mods that use HandQueue and with the game's own left-hand moves
    /// (doors, reloads, checking ammo). A request that finds the hand busy waits its turn in HandQueue instead of going
    /// through without the hand (user 2026-11-26: first come first served, never a change without the hand); one that
    /// cannot start within 2 s is dropped. With Wait For Hand off the hand is only for show, so a busy hand means the
    /// vanilla change at once, as before.
    /// </summary>
    internal static partial class HandDriver
    {
        internal const string QueueOwner = "ScopeHandjob"; // the same name for the dev and the public build
        private const int MaxWaiting = 3;

        private sealed class Waiting
        {
            internal object[] Ticket;
            internal Player.FirearmController Controller;
            internal string What;
            internal ScopeState[] Template; // mode key: the request, kept as steps from the modes shown when it was made
            internal int[] Steps;
            internal ScopeZoomHandler Zoom; // mouse wheel: the knob and the scroll kept for it
            internal float Amount;
        }

        private static readonly List<Waiting> waiting = new List<Waiting>();
        private static object[] moveTicket;  // the hand move in progress holds the hand with this
        private static object[] startTicket; // a waiting request starting now: its own call passes the gate

        private static bool Alive(Player p) => p.HealthController != null && p.HealthController.IsAlive;

        /// <summary>Nobody waits behind the move in progress, so more presses may join it.</summary>
        private static bool NobodyBehind()
        {
            if (gunTookHand) return false; // the game's own left-hand action waits: new presses go behind it, not into this move
            if (waiting.Count > 0) return false;
            if (moveTicket != null && HandQueue.Holds(moveTicket)) return HandQueue.IsLast(moveTicket);
            return !HandQueue.OthersWaiting(QueueOwner);
        }

        /// <summary>
        /// May a new hand move start now: nobody holds or waits for the hand (or this is a waiting request whose turn
        /// came), and the hand is free.
        /// </summary>
        private static bool HandTurnNow(Player p, Player.FirearmController fc, out string why)
        {
            why = null;
            if (startTicket != null) return true; // checked when its turn came
            if (waiting.Count > 0)
            {
                why = "earlier presses are waiting";
                return false;
            }
            if (!HandQueue.Empty())
            {
                why = "the hand belongs to " + HandQueue.Front();
                return false;
            }
            return HandFree(p, fc, out why);
        }

        /// <summary>
        /// Is the left hand free for a move: not sprinting / reloading, no one else driving the hand (door, another
        /// mod; Stance Overhaul's pistol patrol hand is borrowed, so it does not count), the gun idle or firing, and no
        /// left-hand animation of the game playing.
        /// </summary>
        private static bool HandFree(Player p, Player.FirearmController fc, out string why)
        {
            why = null;
            if (p.IsSprintEnabled) why = "sprinting";
            else if (fc.IsInReloadOperation()) why = "reloading";
            else if (Settings.HandToUse == HandSide.Left && ForeignLeftHand(p, out GripPose other)) why = "the left hand is in use by " + other.name;
            else if (!FreeOperation(fc.CurrentOperation)) why = "the gun is busy (" + fc.CurrentOperation.GetType().Name + ")";
            else if (fc.IsInInteractionStrictCheck() && !PatrolPoseOnly(p)) why = "a left-hand animation is playing";
            return why == null;
        }

        private const int CompassAction = 404; // ObjectInHandsAnimator.ShowCompass(true): UseLeftHand on, LActionIndex 404

        /// <summary>
        /// Stance Overhaul's pistol patrol holds the left hand off the gun with its own target AND the game's compass pose
        /// (ShowCompass(true): UseLeftHand + the left-actions layer), which IsInInteractionStrictCheck counts as a left-hand
        /// animation. Only that pose with the patrol hand borrowable: the hand is free, the move borrows it as usual (1.2.1,
        /// 2026-11-28: the mode key in pistol patrol waited 2 s and was dropped, no hand, no change).
        /// </summary>
        private static bool PatrolPoseOnly(Player p)
        {
            if (!Borrowable(LeftTarget(p)) || ThirdIkWeight(p).Value <= 0.01f) return false;
            try
            {
                return AnimationControllerParametersTable.GetIntLActionIndex(p.HandsAnimator.Animator) == CompassAction;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The game's own mode change (FirearmOperation.SetScopeMode -> FirearmsAnimator.ModToggleTrigger) sets the animator's
        /// "mod switch" trigger, a little gun move. In Stance Overhaul's pistol patrol the left-actions layer sits in the
        /// compass pose, so the trigger is not taken and stays set until the stance ends, then plays: a jolt long after the
        /// press (2026-11-28; the press jolt itself is ours, GunShake). In patrol it is cleared right away.
        /// </summary>
        private static void ForgetPatrolToggle(Player p)
        {
            if (p == null || (borrowedHand == null && !PatrolPoseOnly(p))) return;
            try
            {
                AnimationControllerParametersTable.ResetTriggerModSwitch(p.HandsAnimator.Animator);
                Log("pistol patrol: the game's mod-switch trigger cleared (it would play when the stance ends)");
            }
            catch (System.Exception error)
            {
                Log("mod-switch trigger not cleared: " + error.Message);
                return;
            }
            // That move also carried the click ("Sound" event SndModSwitch, one of the weapon's AdditionalSounds, e.g. Glock
            // 17): play it now through the same path the animation event takes (else no click in patrol).
            try
            {
                if (p.HandsController is Player.FirearmController fc && fc.WeaponSoundPlayer is IEventsConsumer sounds)
                    sounds.OnSound(ModSwitchSound);
            }
            catch (System.Exception error)
            {
                Log("mod-switch click not played: " + error.Message);
            }
        }

        private const string ModSwitchSound = "SndModSwitch";

        /// <summary>Someone else drives the left hand now (a target with weight that is not ours and not borrowable).</summary>
        private static bool ForeignLeftHand(Player p, out GripPose current)
        {
            current = LeftTarget(p);
            return current != null && (rig == null || current != rig.IkTarget) && ThirdIkWeight(p).Value > 0.01f && !Borrowable(current);
        }

        /// <summary>Idle or firing: free. Checking ammo / chamber / fire mode, reloading, malfunction, stock, bipod, mods: busy.</summary>
        private static bool FreeOperation(Player.FirearmController.FirearmOperation op)
        {
            if (op == null || op is Player.FirearmController.FireOperationBase) return true;
            if (op is Player.FirearmController.UtilityOperation || op is Player.FirearmController.LauncherUtilityOperation) return false;
            return op is Player.FirearmController.Idling || op is Player.FirearmController.LauncherIdling;
        }

        /// <summary>The hand move just started: it holds the hand until Stop.</summary>
        private static void TookHand(string what)
        {
            if (moveTicket != null && moveTicket != startTicket) HandQueue.Leave(moveTicket);
            moveTicket = startTicket ?? HandQueue.Join(QueueOwner, what);
            startTicket = null;
            Log($"left hand taken ({what})");
        }

        /// <summary>Stop: the hand is back, the next one in the queue may have it.</summary>
        private static void LetGoOfHand()
        {
            if (moveTicket == null) return;
            HandQueue.Leave(moveTicket);
            moveTicket = null;
            Log("left hand given back");
        }

        /// <summary>A waiting request that was being started did not start after all: it gives up its place.</summary>
        private static void DropStartTicket()
        {
            if (startTicket == null) return;
            HandQueue.Leave(startTicket);
            startTicket = null;
        }

        /// <summary>The hand is busy: this mode change waits for it. False for the SetScopeMode prefix (nothing now).</summary>
        private static bool WaitMode(Player.FirearmController fc, ScopeState[] states, string why)
        {
            EFT.Firearms firearms = FirearmsOf(fc);
            var steps = new int[states.Length];
            for (int i = 0; i < states.Length; i++)
            {
                int current = SelectedMode(firearms, states[i]);
                steps[i] = current >= 0 ? states[i].ScopeMode - current : 0;
            }
            if (!AddWaiting(fc, (ScopeState[])states.Clone(), steps))
            {
                Log($"mode key while the hand is busy ({why}): {MaxWaiting} presses already waiting, dropped");
                return false;
            }
            Log($"mode key while the hand is busy ({why}): waiting (hand: {HandQueue.Front()}, {waiting.Count} of ours waiting)");
            return false;
        }

        private static bool AddWaiting(Player.FirearmController fc, ScopeState[] template, int[] steps)
        {
            if (waiting.Count >= MaxWaiting) return false;
            waiting.Add(new Waiting
            {
                Ticket = HandQueue.Join(QueueOwner, "mode key"), Controller = fc, What = "mode key", Template = template, Steps = steps,
            });
            return true;
        }

        /// <summary>The hand is busy: this scroll waits for it (added to the last one when it is for the same knob). The game gets 0.</summary>
        private static float WaitZoom(Player.FirearmController fc, ScopeZoomHandler handler, float raw, string why)
        {
            Waiting last = waiting.Count > 0 ? waiting[waiting.Count - 1] : null;
            if (last != null && last.Zoom == handler)
            {
                last.Amount += raw;
                return 0f;
            }
            if (waiting.Count >= MaxWaiting)
            {
                Log($"mouse wheel while the hand is busy ({why}): {MaxWaiting} presses already waiting, dropped");
                return 0f;
            }
            waiting.Add(new Waiting
            {
                Ticket = HandQueue.Join(QueueOwner, "mouse wheel"), Controller = fc, What = "mouse wheel", Zoom = handler, Amount = raw,
            });
            Log($"mouse wheel while the hand is busy ({why}): waiting (hand: {HandQueue.Front()}, {waiting.Count} of ours waiting)");
            return 0f;
        }

        /// <summary>FilterZoom: the scroll kept for this knob once its turn has come and the hand is free (else 0).</summary>
        private static float TakeWaitingZoom(ScopeZoomHandler handler)
        {
            if (phase != Phase.Idle || waiting.Count == 0 || waiting[0].Zoom != handler) return 0f;
            Waiting w = waiting[0];
            if (!HandQueue.IsFront(w.Ticket) || !TryGetMainController(out Player p, out Player.FirearmController fc) || fc != w.Controller ||
                !HandFree(p, fc, out _)) return 0f;
            waiting.RemoveAt(0);
            DropStartTicket();
            startTicket = w.Ticket;
            Log($"mouse wheel: the hand is free, going after {HandQueue.Waited(w.Ticket):0.00} s");
            return w.Amount;
        }

        /// <summary>Plugin.Update, every frame: heartbeats, waiting requests dropped or started when their turn comes.</summary>
        internal static void TickQueue()
        {
            if (moveTicket != null)
            {
                if (phase == Phase.Idle) LetGoOfHand();
                else HandQueue.Beat(moveTicket);
            }
            if (waiting.Count == 0) return;
            if (!Settings.Active)
            {
                foreach (Waiting w in waiting) HandQueue.Leave(w.Ticket);
                waiting.Clear();
                return;
            }
            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                Waiting w = waiting[i];
                string drop = DropReason(w);
                if (drop == null)
                {
                    HandQueue.Beat(w.Ticket);
                    continue;
                }
                HandQueue.Leave(w.Ticket);
                waiting.RemoveAt(i);
                Log($"{w.What} {drop}: dropped");
            }
            if (waiting.Count == 0 || phase != Phase.Idle) return;

            Waiting first = waiting[0];
            if (first.Template == null) return; // the mouse wheel goes on in FilterZoom
            if (!HandQueue.IsFront(first.Ticket) || !TryGetMainController(out Player p, out Player.FirearmController fc) || !HandFree(p, fc, out _)) return;
            waiting.RemoveAt(0);
            ScopeState[] states = Rebuild(first);
            startTicket = first.Ticket;
            Log($"mode key: the hand is free, going after {HandQueue.Waited(first.Ticket):0.00} s");
            try
            {
                OwnSetScopeMode(fc, states); // through the SetScopeMode prefix: a new hand move with this ticket
            }
            finally
            {
                DropStartTicket();
            }
        }

        /// <summary>Why a waiting request is dropped now (null = it keeps waiting).</summary>
        private static string DropReason(Waiting w)
        {
            if (!HandQueue.Holds(w.Ticket)) return "lost its place in the queue";
            if (!TryGetMainController(out Player p, out Player.FirearmController fc) || fc != w.Controller) return "the gun changed";
            if (!Alive(p)) return "not alive";
            EFT.Firearms firearms = FirearmsOf(fc);
            if (w.Zoom != null)
            {
                if (ScopeTargets.OwnerOf(firearms, w.Zoom) == null) return "the scope is gone";
            }
            else
            {
                foreach (ScopeState state in w.Template)
                    if (ScopeTargets.ForSight(firearms, state.Id) == null) return "the sight is gone";
            }
            if (HandQueue.Waited(w.Ticket) > HandQueue.MaxWait)
            {
                string holder = !HandQueue.IsFront(w.Ticket) ? "hand: " + HandQueue.Front() : HandFree(p, fc, out string why) ? "?" : why;
                return $"waited {HandQueue.MaxWait:0} s for the hand ({holder})";
            }
            return null;
        }

        /// <summary>The waiting mode change, from the modes the sights show now.</summary>
        private static ScopeState[] Rebuild(Waiting w)
        {
            EFT.Firearms firearms = FirearmsOf(w.Controller);
            var states = (ScopeState[])w.Template.Clone();
            for (int i = 0; i < states.Length; i++)
            {
                int current = SelectedMode(firearms, states[i]);
                if (current >= 0) states[i].ScopeMode = current + w.Steps[i];
            }
            return states;
        }

        /// <summary>Hold on a knob: someone waits for the hand, so it goes back now instead of waiting the Hold time.</summary>
        private static bool HandOverNow()
        {
            if (releaseZoom || (strokes && (fastTwist || fastPending || regrip || sweeping))) return false;
            return !NobodyBehind();
        }

        /// <summary>Does this part get a hand at all (a saved pose; dev build: also the editor / automatic grip)?</summary>
        private static bool HasHandFor(ScopeTarget t)
        {
            return PoseStore.Find(t, Settings.HandToUse, out _) != null;
        }
    }
}
