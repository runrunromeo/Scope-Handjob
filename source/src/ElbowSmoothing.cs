using EFT;
using RootMotion.FinalIK;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// Elbow pop fix. The left elbow bends towards the arm solver's bend goal, which Player.AdjustElbows places every
    /// frame. Stance Overhaul (Realism) then moves it in its own AdjustElbows postfix (ElbowIKPatch): a fixed "down,
    /// out, back" direction that switches to another one at once when the shoulder -> hand line comes within about
    /// 25 deg of it. Our hand going up to a scope (HHS-1 magnifier on top) can cross that line, so the elbow jumped.
    /// While our hand moves (and until the elbow has caught up afterwards), the bend goal's direction from the
    /// shoulder, measured in the weapon-tracking frame, may only turn ElbowTurnSpeed degrees a second: the elbow
    /// follows the same goal, but smoothly. The hand idle: the elbow is never touched.
    /// </summary>
    internal static partial class HandDriver
    {
        private const float ElbowTurnSpeed = 360f; // degrees per second
        private static bool elbowSmoothing;
        private static Player elbowPlayer;
        private static Vector3 elbowLocal;        // smoothed shoulder -> bend goal direction, in the tracking frame

        /// <summary>Player.AdjustElbows postfix, after everyone else's (Stance Overhaul's included).</summary>
        internal static void AfterAdjustElbows(Player p)
        {
            bool moving = phase != Phase.Idle && p == player && side == HandSide.Left;
            if (!moving && (!elbowSmoothing || p != elbowPlayer)) return;
            LimbIK[] limbs = Limbs(p);
            if (limbs == null || limbs.Length == 0 || limbs[0] == null) return;
            IKSolverLimb solver = limbs[0].solver;
            Transform goal = solver?.bendGoal;
            if (goal == null || solver.bone1?.transform == null) return;

            Transform frame = p.ProceduralWeaponAnimation?.HandsContainer?.TrackingTransform;
            if (frame == null) frame = p.gameObject.transform;
            Vector3 shoulder = solver.bone1.transform.position;
            Vector3 toGoal = goal.position - shoulder;
            float distance = toGoal.magnitude;
            if (distance < 1e-4f) return;
            Vector3 wanted = frame.InverseTransformDirection(toGoal / distance);

            if (!elbowSmoothing)
            {
                // First frame of the move: start from where the elbow is aimed now.
                elbowSmoothing = true;
                elbowPlayer = p;
                elbowLocal = wanted;
                return;
            }
            elbowLocal = Vector3.RotateTowards(elbowLocal, wanted, ElbowTurnSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f).normalized;
            goal.position = shoulder + frame.TransformDirection(elbowLocal) * distance;

            // Hand back on the handguard and the elbow caught up with the goal again: hand the elbow back.
            if (!moving && Vector3.Angle(elbowLocal, wanted) < 1f) elbowSmoothing = false;
        }
    }
}
