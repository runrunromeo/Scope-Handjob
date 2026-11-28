using System;
using EFT;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// 1.2.1 (2026-11-28): the hand's way between its normal grip and the in-between point off the scope part goes round
    /// the gun's side instead of in a straight line, which cut a little through the handguard / rail on many sights
    /// (FLIR RS-32, PSO-1, Specter DR, ECHO1, Walther MRS, Kobra...). The way: down from under the handguard, out to
    /// the hand's side of the gun, up that side to the part's height, then in to the in-between point. Going home is the
    /// same way backwards. One smooth curve (centripetal Catmull-Rom) walked at an even speed.
    /// </summary>
    internal static partial class HandDriver
    {
        private const int PathSamples = 32;
        private static readonly Vector3[] pathPoints = new Vector3[7];   // phantom, grip, under, side low, side high, via, phantom
        private static readonly Vector3[] pathSamples = new Vector3[PathSamples + 1];
        private static readonly float[] pathLengths = new float[PathSamples + 1];

        /// <summary>The hand at t (0 = normal grip, 1 = the in-between point off the part).</summary>
        private static Vector3 RoundTheGun(Player p, Vector3 grip, Vector3 via, float t)
        {
            Vector3 straight = Vector3.Lerp(grip, via, t);
            // The game took the hand (reload, mag check): straight back within 0.15 s, the game's own move follows.
            if (!Settings.RoundGun || gunTookHand || controller == null || plan == null || plan.Anchor == null) return straight;
            if (!GunFrame(p, out Vector3 origin, out Vector3 up, out Vector3 outward)) return straight;

            float sideAt = Mathf.Max(Settings.RoundSide, Vector3.Dot(grip - origin, outward), Vector3.Dot(via - origin, outward));
            Vector3 under = grip - up * Settings.RoundDrop;
            Vector3 sideLow = under + outward * (sideAt - Vector3.Dot(grip - origin, outward));
            Vector3 sideHigh = via + outward * (sideAt - Vector3.Dot(via - origin, outward));

            pathPoints[1] = grip;
            pathPoints[2] = under;
            pathPoints[3] = sideLow;
            pathPoints[4] = sideHigh;
            pathPoints[5] = via;
            pathPoints[0] = grip + (grip - under);   // leave / arrive straight up or down at the grip
            pathPoints[6] = via + (via - sideHigh);  // and level at the in-between point

            // Even speed along the curve: sample it, then walk t of its length.
            int perSegment = PathSamples / 4;
            pathSamples[0] = grip;
            pathLengths[0] = 0f;
            for (int i = 1; i <= PathSamples; i++)
            {
                int segment = Mathf.Min(3, (i - 1) / perSegment);
                float u = (i - segment * perSegment) / (float)perSegment;
                pathSamples[i] = CatmullRom(pathPoints[segment], pathPoints[segment + 1], pathPoints[segment + 2], pathPoints[segment + 3], u);
                pathLengths[i] = pathLengths[i - 1] + Vector3.Distance(pathSamples[i - 1], pathSamples[i]);
            }
            float total = pathLengths[PathSamples];
            if (total < 1e-4f) return straight;
            float want = Mathf.Clamp01(t) * total;
            for (int i = 1; i <= PathSamples; i++)
            {
                if (pathLengths[i] < want) continue;
                float span = pathLengths[i] - pathLengths[i - 1];
                return span < 1e-6f ? pathSamples[i] : Vector3.Lerp(pathSamples[i - 1], pathSamples[i], (want - pathLengths[i - 1]) / span);
            }
            return via;
        }

        /// <summary>The gun's bore line (a point on it), its up (bore -> scope) and the hand's side, this frame.</summary>
        private static bool GunFrame(Player p, out Vector3 origin, out Vector3 up, out Vector3 outward)
        {
            origin = up = outward = Vector3.zero;
            Vector3 forward = controller.WeaponDirection;
            if (forward.sqrMagnitude < 0.5f) return false;
            forward.Normalize();
            try
            {
                origin = controller.FireportPosition;
            }
            catch (Exception)
            {
                return false;
            }
            Transform view = ViewTransform(p);
            up = Vector3.ProjectOnPlane(plan.Centre - origin, forward);
            if (up.sqrMagnitude < 1e-6f || Vector3.Dot(up, view.up) < -0.2f * up.magnitude) up = Vector3.ProjectOnPlane(view.up, forward);
            if (up.sqrMagnitude < 1e-6f) return false;
            up.Normalize();
            Vector3 left = Vector3.Cross(forward, up).normalized;
            if (Vector3.Dot(left, view.right) > 0f) left = -left;
            outward = side == HandSide.Left ? left : -left; // this hand's side of the gun
            return true;
        }

        /// <summary>Centripetal Catmull-Rom between p1 and p2 (no loops or overshoot when the points are unevenly spaced).</summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float t0 = 0f;
            float t1 = t0 + Knot(p0, p1);
            float t2 = t1 + Knot(p1, p2);
            float t3 = t2 + Knot(p2, p3);
            float t = Mathf.Lerp(t1, t2, u);
            Vector3 a1 = Blend(p0, p1, t0, t1, t), a2 = Blend(p1, p2, t1, t2, t), a3 = Blend(p2, p3, t2, t3, t);
            Vector3 b1 = Blend(a1, a2, t0, t2, t), b2 = Blend(a2, a3, t1, t3, t);
            return Blend(b1, b2, t1, t2, t);
        }

        private static float Knot(Vector3 a, Vector3 b) => Mathf.Max(1e-3f, Mathf.Sqrt(Vector3.Distance(a, b)));

        private static Vector3 Blend(Vector3 a, Vector3 b, float ta, float tb, float t) =>
            tb - ta < 1e-6f ? a : (tb - t) / (tb - ta) * a + (t - ta) / (tb - ta) * b;
    }
}
