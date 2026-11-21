using EFT;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// A small jolt of the gun at the moments the hand works the scope. Pushes EFT's own hands-rotation spring
    /// (ProceduralWeaponAnimation.HandsContainer.HandsRotation), the same spring hit reactions use, so it settles by itself.
    /// - Mouse-wheel zoom: hand leaves the handguard, touches the scope, leaves the scope, back on the handguard.
    /// - Mode key (button / lever / magnifier / quick twist): hand leaves the handguard, each press (when the mode
    ///   switches), back on the handguard.
    /// Both use the same Shake Strength.
    /// </summary>
    internal static partial class HandDriver
    {
        private enum ShakeMoment { LeaveGrip, OnScope, LeaveScope, BackOnGrip, Press }

        private static bool wheelMove;       // this hand move was started by the mouse wheel
        private static bool keyMove;         // this hand move was started by the mode key
        private static Phase shakeSeen = Phase.Idle;

        private static bool ShakeFor(bool wheel) => wheel ? Settings.ShakeEnabled.Value : Settings.ShakeModeKey.Value;

        /// <summary>Called each step after the phase update: jolts on the phase changes that are the moments.</summary>
        private static void WatchShake(Player p)
        {
            Phase now = phase;
            Phase before = shakeSeen;
            shakeSeen = now;
            if (now == before || (!wheelMove && !keyMove)) return;
            bool leftGrip = now == Phase.Reach && before == Phase.Idle;
            if (keyMove)
            {
                if (leftGrip && ShakeFor(false)) Shake(p, ShakeMoment.LeaveGrip);
                return;
            }
            if (!ShakeFor(true)) return;
            if (leftGrip) Shake(p, ShakeMoment.LeaveGrip);
            else if (now == Phase.Hold && before == Phase.Reach) Shake(p, ShakeMoment.OnScope);
            else if (now == Phase.Return && before != Phase.Reach && weight >= 0.99f) Shake(p, ShakeMoment.LeaveScope);
        }

        /// <summary>The hand has come all the way back (not an interrupted stop).</summary>
        private static void BackOnGripShake(Player p)
        {
            if ((wheelMove && ShakeFor(true)) || (keyMove && ShakeFor(false))) Shake(p, ShakeMoment.BackOnGrip);
        }

        /// <summary>The hand pressed the button / threw the lever / flipped the magnifier: the mode switched just now.</summary>
        private static void PressShake(Player p)
        {
            if (ShakeFor(false)) Shake(p, ShakeMoment.Press);
        }

        private static void ResetShake()
        {
            wheelMove = false;
            keyMove = false;
            shakeSeen = Phase.Idle;
        }

        private static void Shake(Player p, ShakeMoment moment)
        {
            if (Settings.ShakeStrength.Value <= 0f || p == null) return;
            var springs = p.ProceduralWeaponAnimation != null ? p.ProceduralWeaponAnimation.HandsContainer : null;
            if (springs == null || springs.HandsRotation == null) return;
            float hand = side == HandSide.Left ? 1f : -1f; // roll / turn towards the side the hand works on
            // x = tip up / down, y = turn, z = roll (degrees of the spring per kick, before its own damping).
            Vector3 kick;
            switch (moment)
            {
                case ShakeMoment.LeaveGrip:  kick = new Vector3(0.25f, 0.05f * hand, 0.35f * hand); break;  // support gone: muzzle dips, gun rolls
                case ShakeMoment.OnScope:    kick = new Vector3(0.30f, -0.05f * hand, -0.15f * hand); break; // fingers tap the scope
                case ShakeMoment.Press:      kick = new Vector3(0.35f, -0.05f * hand, -0.20f * hand); break; // finger pushes the button in
                case ShakeMoment.LeaveScope: kick = new Vector3(-0.20f, 0.05f * hand, 0.15f * hand); break;  // fingers let go
                default:                     kick = new Vector3(-0.25f, 0f, -0.30f * hand); break;           // hand settles on the handguard
            }
            kick += Random.insideUnitSphere * (0.15f * kick.magnitude); // no two jolts the same
            springs.HandsRotation.AddAcceleration(kick * Settings.ShakeStrength.Value);
        }
    }
}
