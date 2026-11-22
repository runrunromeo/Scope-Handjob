using System;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>A rigid swing of a part about a hinge, stored in the part's parent space.</summary>
    internal sealed class Hinge
    {
        internal Transform Space;
        internal Vector3 AxisLocal, PivotLocal;
        internal float Angle, Slide;

        internal Quaternion Rotation(float f) =>
            Space.rotation * Quaternion.AngleAxis(Angle * f, AxisLocal) * Quaternion.Inverse(Space.rotation);

        internal Vector3 Point(Vector3 world, float f)
        {
            Vector3 local = Space.InverseTransformPoint(world);
            local = Quaternion.AngleAxis(Angle * f, AxisLocal) * (local - PivotLocal) + PivotLocal + AxisLocal * (Slide * f);
            return Space.TransformPoint(local);
        }

        internal void Pose(ref Vector3 pos, ref Quaternion rot, float f)
        {
            pos = Point(pos, f);
            rot = Rotation(f) * rot;
        }

        internal void PoseLocal(Vector3 basePos, Quaternion baseRot, float f, out Vector3 pos, out Quaternion rot)
        {
            Quaternion r = Quaternion.AngleAxis(Angle * f, AxisLocal);
            pos = r * (basePos - PivotLocal) + PivotLocal + AxisLocal * (Slide * f);
            rot = r * baseRot;
        }

        /// <summary>Rigid motion world points a -> b (same vertex order), as a hinge in space's frame.</summary>
        internal static Hinge FromPoints(Vector3[] a, Vector3[] b, Transform space)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length < 3) return null;
            int i0 = 0, i1 = 0, i2 = 0;
            float best = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                float d = (a[i] - a[i0]).sqrMagnitude;
                if (d > best) { best = d; i1 = i; }
            }
            if (best < 1e-6f) return null;
            Vector3 line = (a[i1] - a[i0]).normalized;
            best = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                float d = Vector3.Cross(a[i] - a[i0], line).sqrMagnitude;
                if (d > best) { best = d; i2 = i; }
            }
            if (best < 1e-7f) return null;
            Quaternion qa = Frame(a[i0], a[i1], a[i2]), qb = Frame(b[i0], b[i1], b[i2]);
            Quaternion r = qb * Quaternion.Inverse(qa);
            Vector3 t = b[i0] - r * a[i0];
            int step = Math.Max(1, a.Length / 300);
            for (int i = 0; i < a.Length; i += step)
                if ((r * a[i] + t - b[i]).sqrMagnitude > 0.002f * 0.002f) return null; // not the same rigid part
            return FromMotion(r, t, space);
        }

        internal static Hinge FromMotion(Quaternion r, Vector3 t, Transform space)
        {
            r.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) { angle = 360f - angle; axis = -axis; }
            if (angle < 1f || float.IsNaN(axis.x)) return null;
            axis.Normalize();
            float slide = Vector3.Dot(t, axis);
            Vector3 flat = t - axis * slide;
            // Solve (I - R) p = flat in the plane across the axis.
            Vector3 e1 = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 e2 = Vector3.Cross(axis, e1);
            float c = Mathf.Cos(angle * Mathf.Deg2Rad), s = Mathf.Sin(angle * Mathf.Deg2Rad);
            float t1 = Vector3.Dot(flat, e1), t2 = Vector3.Dot(flat, e2);
            float det = (1f - c) * (1f - c) + s * s;
            float p1 = ((1f - c) * t1 - s * t2) / det;
            float p2 = (s * t1 + (1f - c) * t2) / det;
            Vector3 pivot = e1 * p1 + e2 * p2;
            return new Hinge
            {
                Space = space,
                AxisLocal = space.InverseTransformDirection(axis).normalized,
                PivotLocal = space.InverseTransformPoint(pivot),
                Angle = angle,
                Slide = slide / Mathf.Max(1e-4f, space.lossyScale.x),
            };
        }

        private static Quaternion Frame(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            Vector3 x = (p1 - p0).normalized;
            Vector3 z = Vector3.Cross(x, p2 - p0).normalized;
            return Quaternion.LookRotation(z, Vector3.Cross(z, x));
        }

        /// <summary>
        /// The same end position reached the other way round (121 deg one way = 239 deg the other). A part hinged at the
        /// top rear of a sight (Aimpoint CS rear lens cover) would cut through the sight on the short way.
        /// </summary>
        internal Hinge OtherWay() => new Hinge
        {
            Space = Space,
            AxisLocal = AxisLocal,
            PivotLocal = PivotLocal,
            Angle = Angle > 0f ? Angle - 360f : Angle + 360f,
            Slide = Slide,
        };

        internal static Hinge GuessFlip(Vector3 a, Vector3 b, Transform space, Vector3 weaponDirection)
        {
            // Unknown shape: assume a quarter turn about the gun's axis, whichever pivot keeps it nearer the gun.
            Vector3 axis = weaponDirection.normalized;
            Hinge best = null;
            float bestScore = float.MaxValue;
            foreach (float angle in new[] { 90f, -90f })
            {
                Quaternion r = Quaternion.AngleAxis(angle, axis);
                Hinge h = Hinge.FromMotion(r, b - r * a, space);
                if (h == null) continue;
                Vector3 pivot = space.TransformPoint(h.PivotLocal) - (a + b) * 0.5f;
                float score = pivot.sqrMagnitude;
                if (Vector3.Dot(pivot, Vector3.Cross(axis, b - a)) > 0f) score += 1f;
                if (score < bestScore) { bestScore = score; best = h; }
            }
            HandDriver.Log("magnifier swing guessed (mesh shapes not readable)");
            return best;
        }
    }
}
