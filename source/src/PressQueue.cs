using EFT;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// Mode key pressed again while the hand is already on its way to / working a lever, button or flip magnifier:
    /// the press is kept and done by the hand after the current one, never ahead of it (it used to go through the
    /// vanilla way at once, so the scope switched while the hand was still elsewhere). Also: the mode key on a sight
    /// with nothing to switch (one mode) is dropped entirely, no press animation.
    /// </summary>
    internal static partial class HandDriver
    {
        private const int MaxQueuedPresses = 3;
        private static int queuedPresses;
        private static ScopeState[] queuedTemplate; // the last extra request
        private static int[] queuedSteps;           // per state: how many modes on from the mode it was asked from
        private static float pressFrom;             // button: where along the press line this press starts (m, - = off the button)

        /// <summary>
        /// Button finger offset along the press line at press progress p: from where it starts to fully in at 0.5 (the
        /// mode changes there), then off the button by about the press depth, so every press is a separate tap.
        /// </summary>
        private static float ButtonOffset(float p)
        {
            float depth = Settings.ButtonPressMm / 1000f;
            float lift = Mathf.Max(depth, 0.003f);
            p = Mathf.Clamp01(p);
            return p < 0.5f
                ? Mathf.Lerp(pressFrom, depth, Ease(p / 0.5f))
                : Mathf.Lerp(depth, -lift, Ease((p - 0.5f) / 0.5f));
        }

        private const float MinPressInterval = 0.25f; // the mode key counts at most 4 presses a second
        private static float lastPressAt = -10f;
        private static int ownCalls;                  // SetScopeMode calls made by this mod (not key presses)
        private static bool repeatPress;              // this press was kept: it runs at the capped speed

        /// <summary>A key press within 0.25 s of the last one that counted: dropped, the hand could not keep up.</summary>
        private static bool PressTooSoon()
        {
            if (ownCalls > 0) return false;
            float now = Time.time;
            if (now - lastPressAt < MinPressInterval) return true;
            lastPressAt = now;
            return false;
        }

        /// <summary>SetScopeMode from this mod (redirect, Short Stock replay, magnifier replay): not a key press.</summary>
        private static void OwnSetScopeMode(Player.FirearmController fc, ScopeState[] states)
        {
            ownCalls++;
            try
            {
                fc.SetScopeMode(states);
            }
            finally
            {
                ownCalls--;
            }
        }

        /// <summary>Seconds for one press: Switch Time, kept presses at most 0.25 s so 4 a second keep pace.</summary>
        private static float PressDuration()
        {
            float time = Mathf.Max(0.01f, Settings.SwitchTime.Value);
            return repeatPress ? Mathf.Min(time, MinPressInterval) : time;
        }

        /// <summary>Speed-up of the flick itself (Act phase only, not the reach / return): lens covers have their own
        /// (2. Timing / Lens Cover Speed), everything else 1.</summary>
        private static float Speed() => target != null && target.IsCover ? Mathf.Max(0.1f, Settings.CoverSpeed.Value) : 1f;

        /// <summary>How far into a press the next kept one may begin: the finger back off the button / lever nearly over.</summary>
        private static float NextPressAt() => target != null && Turns() && !SteppedLever && !SpringTwist ? 0.9f : 0.75f;

        /// <summary>Lever, or a rotary knob on a button scope: each press turns to the other position and the next turns back.</summary>
        private static bool Turns() => target.Kind == PartKind.Lever || (target.Kind == PartKind.Button && plan != null && plan.Twists);

        /// <summary>Keeps one more press for the hand. Always false: the game must not switch now.</summary>
        private static bool QueuePress(Player.FirearmController fc, ScopeState[] states)
        {
            if (queuedPresses >= MaxQueuedPresses) return false; // hammering the key: the hand can only press so fast
            EFT.Firearms firearms = FirearmsOf(fc);
            var steps = new int[states.Length];
            for (int i = 0; i < states.Length; i++)
            {
                int current = SelectedMode(firearms, states[i]);
                steps[i] = current >= 0 ? states[i].ScopeMode - current : 0;
            }
            queuedTemplate = (ScopeState[])states.Clone();
            queuedSteps = steps;
            queuedPresses++;
            // Hand already letting go of a lever / button: back to it from where it is (a magnifier starts over at the end).
            if (phase == Phase.Return && target.Kind != PartKind.Magnifier) phase = Phase.Reach;
            Log($"mode key again while the hand is busy: press kept ({queuedPresses} waiting)");
            return false;
        }

        private static int SelectedMode(EFT.Firearms firearms, ScopeState state)
        {
            SightModVisualControllers vc = ScopeTargets.ForSight(firearms, state.Id);
            int[] selected = vc?.SightMod?.ScopesSelectedModes;
            int index = state.ScopeIndexInsideSight;
            return selected != null && index >= 0 && index < selected.Length ? selected[index] : -1;
        }

        /// <summary>The next kept press, as a request from the modes the scope shows now.</summary>
        private static ScopeState[] TakeQueuedPress()
        {
            if (queuedPresses <= 0 || queuedTemplate == null || controller == null) return null;
            queuedPresses--;
            EFT.Firearms firearms = FirearmsOf(controller);
            var states = (ScopeState[])queuedTemplate.Clone();
            for (int i = 0; i < states.Length; i++)
            {
                int current = SelectedMode(firearms, states[i]);
                if (current >= 0) states[i].ScopeMode = current + queuedSteps[i];
            }
            return states;
        }

        /// <summary>Lever / button: the hand is on the part and there is a kept press. Starts it in place.</summary>
        private static bool StartQueuedPressInPlace()
        {
            if (target == null || target.Kind == PartKind.Magnifier || target.Kind == PartKind.Knob) return false;
            ScopeState[] next = TakeQueuedPress();
            if (next == null) return false;
            pendingStates = next;
            modeDone = false;
            repeatPress = true;
            AimLensDarken();
            if (SteppedLever)
            {
                // 3+ positions: push again towards the next one, from where the hand is now (on its way back).
                AimLever(leverAt + 1, SteppedLeverAngle(actProgress));
                actProgress = 0f;
            }
            else if (SpringTwist)
            {
                // Spring-back twist: the next mode pushes the other way, from where the hand is now.
                float now = SteppedLeverAngle(actProgress);
                leverReverse = !leverReverse;
                AimTwist(now);
                actProgress = 0f;
            }
            else if (Turns())
            {
                // The lever / knob now sits at (or almost at) its other position: run back from where the hand is.
                leverReverse = !leverReverse;
                actProgress = 1f - Mathf.Clamp01(actProgress);
            }
            else
            {
                pressFrom = ButtonOffset(actProgress); // tap again from where the finger is now
                actProgress = 0f;
            }
            phase = Phase.Act;
            Log($"kept press: hand presses again ({queuedPresses} still waiting)");
            return true;
        }

        /// <summary>Magnifier: the hand is back on the handguard and a press is kept; flip it again from the start.</summary>
        private static void ReplayQueuedPressAfterStop(Player.FirearmController fc, ScopeState[] next, int left, ScopeState[] template, int[] steps)
        {
            if (fc == null || next == null || player == null || !ReferenceEquals(player.HandsController, fc)) return;
            Log("kept press: hand goes again");
            OwnSetScopeMode(fc, next); // through the SetScopeMode prefix: a new hand move
            if (phase == Phase.Idle || left <= 0) return;
            queuedPresses = left; // still more kept presses: they follow this move
            queuedTemplate = template;
            queuedSteps = steps;
        }

        private static void ClearQueuedPresses()
        {
            queuedPresses = 0;
            queuedTemplate = null;
            queuedSteps = null;
        }

        /// <summary>Does this request switch anything at all (zoom, reticle, palette)? Unknown sights count as yes.</summary>
        private static bool ChangesAnything(Player.FirearmController fc, ScopeState[] states)
        {
            EFT.Firearms firearms = FirearmsOf(fc);
            if (firearms == null) return true;
            foreach (ScopeState state in states)
            {
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, state.Id);
                if (vc == null || vc.SightMod == null) return true;
                if (ScopeTargets.ChangesZoom(vc.SightMod, state, ScopeTargets.ZoomOf(vc))) return true;
                if (ScopeTargets.ChangesVisibleMode(vc, state)) return true;
            }
            return false;
        }
    }
}
