using System;
using System.Collections.Generic;
using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>One finger of the hand copy, proximal joint first.</summary>
    internal sealed class Finger
    {
        internal int Number;              // 1 = thumb, 2 = index ... 5 = little finger (EFT "Digit" naming)
        internal Transform[] Joints;
        internal Quaternion[] BaseLocal;  // pose copied from the real hand
        internal Vector3[] AxisParent;    // bend axis in the joint parent's space; zero = free joint
        internal float[] Angle;           // current bend relative to BaseLocal (planar joints)
        internal Vector3 TipLocal;        // fingertip in the last joint's space
        internal float Length;

        internal Vector3 Tip => Joints[Joints.Length - 1].TransformPoint(TipLocal);
        internal Vector3 Knuckle => Joints[0].position;
    }

    /// <summary>
    /// A copy of the player's hand bones (same order as EFT's HandPoser children) inside a GripPose.
    /// EFT's HandPoser copies the copy's finger rotations onto the real hand (that is how door handles are
    /// gripped), so posing this copy poses the real fingers. The copy also stands in for the real hand
    /// when checking a grip against the weapon's shape.
    /// </summary>
    internal sealed class HandRig
    {
        private static readonly AccessTools.FieldRef<HandPoser, IWeaponGripPose> IkPalm =
            AccessTools.FieldRefAccess<HandPoser, IWeaponGripPose>("_ikPalm");

        internal const float PalmRadius = 0.013f;
        internal const float FingerRadius = 0.0085f;
        internal const float ThumbRadius = 0.0095f;

        internal readonly HandSide Side;
        internal readonly Player Owner;
        internal readonly HandPoser Poser;
        internal readonly GripPose IkTarget;   // the hand bone (IK end) target
        internal readonly GripPose FingerPose; // the bone copy, root = HandPoser transform
        private readonly Transform[] real;
        private readonly Transform[] mirror;

        internal Finger Thumb, Index;
        internal readonly List<Finger> Others = new List<Finger>();
        internal readonly List<Finger> Fingers = new List<Finger>();
        internal bool FingersFound;

        // Palm frame, in the copy root's space
        internal Vector3 PalmCentreLocal;
        internal Quaternion PalmBasisLocal = Quaternion.identity; // forward = fingers, up = palm normal
        internal Vector3 ForwardLocal, NormalLocal, LateralLocal;

        // Hand bone (IK end) relative to the HandPoser transform
        private Vector3 boneOffsetPos;
        private Quaternion boneOffsetRot = Quaternion.identity;

        internal Transform Root => FingerPose.transform;
        internal Transform[] RealBones => real;
        internal Transform[] CopyBones => mirror;

        private HandRig(Player owner, HandSide side, HandPoser poser, Transform[] real, Transform[] mirror, GripPose ikTarget, GripPose fingerPose)
        {
            Owner = owner;
            Side = side;
            Poser = poser;
            this.real = real;
            this.mirror = mirror;
            IkTarget = ikTarget;
            FingerPose = fingerPose;
        }

        internal static HandRig Build(Player p, HandSide side)
        {
            int index = side == HandSide.Right ? 1 : 0;
            HandPoser[] posers = p.HandPosers;
            if (posers == null || posers.Length <= index || posers[index] == null) return Fail("no HandPoser for " + side);
            HandPoser poser = posers[index];
            Transform[] real = poser.Children;
            if (real == null || real.Length < 4 || real.Length > 400 || real[0] != poser.transform) return Fail("unexpected hand bones");

            Transform holder = p.gameObject.transform;
            GripPose ikTarget = NewGrip("NoMoreScopeRemote_IkTarget_" + side, holder, side);
            ikTarget.gameObject.SetActive(true);

            var rootGo = new GameObject("NoMoreScopeRemote_Fingers_" + side);
            rootGo.SetActive(false); // GripPose.Awake must see DontCache and the finished bone copy
            rootGo.transform.SetParent(holder, false);
            var mirror = new Transform[real.Length];
            var map = new Dictionary<Transform, Transform> { { real[0], rootGo.transform } };
            mirror[0] = rootGo.transform;
            for (int i = 1; i < real.Length; i++)
            {
                if (real[i] == null || !map.TryGetValue(real[i].parent, out Transform parent))
                {
                    UnityEngine.Object.Destroy(rootGo);
                    UnityEngine.Object.Destroy(ikTarget.gameObject);
                    return Fail("hand bone list is not a single tree");
                }
                var t = new GameObject(real[i].name).transform;
                t.SetParent(parent, false);
                t.localPosition = real[i].localPosition;
                t.localRotation = real[i].localRotation;
                t.localScale = real[i].localScale;
                map[real[i]] = t;
                mirror[i] = t;
            }
            GripPose fingerPose = rootGo.AddComponent<GripPose>();
            fingerPose.Hand = side == HandSide.Right ? GripPose.EHand.Right : GripPose.EHand.Left;
            fingerPose.GripType = GripPose.EGripType.Common;
            fingerPose.DontCache = true;
            rootGo.SetActive(true);

            Transform[] cached = fingerPose.FingerTransforms;
            if (cached == null || cached.Length != real.Length)
            {
                UnityEngine.Object.Destroy(rootGo);
                UnityEngine.Object.Destroy(ikTarget.gameObject);
                return Fail("bone copy order does not match (" + (cached?.Length ?? 0) + " vs " + real.Length + ")");
            }
            for (int i = 0; i < cached.Length; i++)
            {
                if (cached[i] != mirror[i])
                {
                    UnityEngine.Object.Destroy(rootGo);
                    UnityEngine.Object.Destroy(ikTarget.gameObject);
                    return Fail("bone copy order does not match at " + i);
                }
            }

            var rig = new HandRig(p, side, poser, real, mirror, ikTarget, fingerPose);
            rig.FindFingers();
            HandDriver.Log($"{side} hand copy: {real.Length} bones, fingers {(rig.FingersFound ? "found" : "NOT found")}" +
                (rig.FingersFound ? $" (thumb {rig.Thumb.Joints[0].name}, index {rig.Index.Joints[0].name})" : ""));
            return rig;
        }

        private static GripPose NewGrip(string name, Transform holder, HandSide side)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetParent(holder, false);
            GripPose grip = go.AddComponent<GripPose>();
            grip.Hand = side == HandSide.Right ? GripPose.EHand.Right : GripPose.EHand.Left;
            grip.GripType = GripPose.EGripType.Common;
            grip.DontCache = true;
            return grip;
        }

        private static HandRig Fail(string why)
        {
            HandDriver.Log("hand copy failed: " + why);
            return null;
        }

        internal bool Alive => Owner != null && Poser != null && FingerPose != null && IkTarget != null;

        internal void Destroy()
        {
            ReleaseFingers();
            if (FingerPose != null) UnityEngine.Object.Destroy(FingerPose.gameObject);
            if (IkTarget != null) UnityEngine.Object.Destroy(IkTarget.gameObject);
        }

        // ------------------------------------------------------------------ finger mapping

        private void FindFingers()
        {
            var chains = new SortedDictionary<int, SortedDictionary<int, Transform>>();
            for (int i = 1; i < mirror.Length; i++)
            {
                string n = mirror[i].name;
                int at = n.LastIndexOf("Digit", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || n.Length < at + 7) continue;
                char f = n[at + 5], j = n[at + 6];
                if (!char.IsDigit(f) || !char.IsDigit(j) || n.Length != at + 7) continue;
                int finger = f - '0', joint = j - '0';
                if (!chains.TryGetValue(finger, out var joints)) chains[finger] = joints = new SortedDictionary<int, Transform>();
                joints[joint] = mirror[i];
            }
            foreach (var pair in chains)
            {
                if (pair.Value.Count < 2) continue;
                var joints = new List<Transform>(pair.Value.Values);
                var finger = new Finger
                {
                    Number = pair.Key,
                    Joints = joints.ToArray(),
                    BaseLocal = new Quaternion[joints.Count],
                    AxisParent = new Vector3[joints.Count],
                    Angle = new float[joints.Count],
                };
                Fingers.Add(finger);
                if (pair.Key == 1) Thumb = finger;
                else if (pair.Key == 2) Index = finger;
                else Others.Add(finger);
            }
            FingersFound = Thumb != null && Index != null && Others.Count >= 2;
        }

        // ------------------------------------------------------------------ per move

        /// <summary>Copies the real hand's current pose into the copy and works out palm frame and bend axes.</summary>
        internal void Sync(Transform ikBone, Quaternion[] baseOverride = null)
        {
            for (int i = 1; i < real.Length; i++)
            {
                if (real[i] == null) continue;
                mirror[i].localPosition = real[i].localPosition;
                mirror[i].localRotation = real[i].localRotation;
            }
            // A saved pose brings its own starting finger shape, so it looks the same on every gun.
            if (baseOverride != null && baseOverride.Length == mirror.Length)
                for (int i = 1; i < mirror.Length; i++) mirror[i].localRotation = baseOverride[i];
            Transform poserT = Poser.transform;
            if (ikBone != null)
            {
                boneOffsetRot = Quaternion.Inverse(poserT.rotation) * ikBone.rotation;
                boneOffsetPos = poserT.InverseTransformVector(ikBone.position - poserT.position);
            }
            else
            {
                boneOffsetRot = Quaternion.identity;
                boneOffsetPos = Vector3.zero;
            }
            Transform holder = Root.parent;
            Vector3 holderScale = holder != null ? holder.lossyScale : Vector3.one;
            Vector3 want = poserT.lossyScale;
            Root.localScale = new Vector3(want.x / Mathf.Max(1e-4f, holderScale.x), want.y / Mathf.Max(1e-4f, holderScale.y), want.z / Mathf.Max(1e-4f, holderScale.z));
            Root.SetPositionAndRotation(poserT.position, poserT.rotation);
            if (!FingersFound) return;

            foreach (Finger f in Fingers)
                for (int k = 0; k < f.Joints.Length; k++)
                {
                    f.BaseLocal[k] = f.Joints[k].localRotation;
                    f.Angle[k] = 0f;
                }

            // Palm frame from the knuckles (index .. little finger).
            Vector3 knuckles = Vector3.zero;
            int count = 0;
            Finger little = Index;
            foreach (Finger f in Fingers)
            {
                if (f == Thumb) continue;
                knuckles += Local(f.Knuckle);
                count++;
                if (f.Number > little.Number) little = f;
            }
            knuckles /= count;
            Vector3 forward = knuckles.normalized;
            Vector3 lateral = (Local(Index.Knuckle) - Local(little.Knuckle)).normalized;
            Vector3 normal = Vector3.Cross(forward, lateral).normalized;

            foreach (Finger f in Fingers) f.TipLocal = ComputeTip(f);
            Finger middle = Others[0];
            if (Vector3.Dot(Local(middle.Tip) - Local(middle.Knuckle), normal) < 0f) normal = -normal; // curled fingers are on the palm side
            ForwardLocal = forward;
            NormalLocal = normal;
            LateralLocal = lateral;
            PalmBasisLocal = Quaternion.LookRotation(forward, normal);
            PalmCentreLocal = knuckles * 0.5f;

            foreach (Finger f in Fingers)
            {
                f.Length = 0f;
                for (int k = 0; k < f.Joints.Length; k++)
                {
                    Vector3 here = Local(f.Joints[k].position);
                    Vector3 next = k + 1 < f.Joints.Length ? Local(f.Joints[k + 1].position) : Local(f.Tip);
                    Vector3 prev = k == 0 ? Vector3.zero : Local(f.Joints[k - 1].position);
                    Vector3 cur = next - here;
                    f.Length += Root.TransformVector(cur).magnitude;
                    if (f == Thumb && k == 0)
                    {
                        f.AxisParent[k] = Vector3.zero; // thumb base: free joint
                        continue;
                    }
                    Vector3 axis = Vector3.Cross((here - prev).normalized, cur.normalized);
                    if (axis.magnitude < 0.2f) axis = f == Thumb ? Vector3.Cross(cur.normalized, normal) : lateral;
                    axis.Normalize();
                    if (f != Thumb && Vector3.Dot(Vector3.Cross(axis, cur), normal) < 0f) axis = -axis; // + = curl into the palm
                    Vector3 world = Root.TransformDirection(axis);
                    f.AxisParent[k] = f.Joints[k].parent.InverseTransformDirection(world).normalized;
                }
            }
        }

        private Vector3 Local(Vector3 world) => Root.InverseTransformPoint(world);

        /// <summary>Fingertip: the last bone is assumed to point the same way (in its own space) as the one before it.</summary>
        private static Vector3 ComputeTip(Finger f)
        {
            int n = f.Joints.Length;
            Transform last = f.Joints[n - 1], prev = f.Joints[n - 2];
            float length = (last.position - prev.position).magnitude;
            Vector3 axisInPrev = prev.InverseTransformDirection(last.position - prev.position).normalized;
            Vector3 dirWorld = last.TransformDirection(axisInPrev);
            return last.InverseTransformPoint(last.position + dirWorld * length * 0.8f);
        }

        internal void ResetFingers()
        {
            if (!FingersFound) return;
            foreach (Finger f in Fingers) ResetFinger(f);
        }

        internal static void ResetFinger(Finger f)
        {
            for (int k = 0; k < f.Joints.Length; k++)
            {
                f.Joints[k].localRotation = f.BaseLocal[k];
                f.Angle[k] = 0f;
            }
        }

        /// <summary>Bends joints from..to-1 of a finger to angle (+ = into the palm), the others stay as they are.</summary>
        internal static void CurlJoints(Finger f, int from, int to, float angle)
        {
            for (int k = from; k < to && k < f.Joints.Length; k++)
            {
                if (f.AxisParent[k] == Vector3.zero) continue;
                f.Angle[k] = angle;
                f.Joints[k].localRotation = Quaternion.AngleAxis(angle, f.AxisParent[k]) * f.BaseLocal[k];
            }
        }

        /// <summary>Thumb base: out = away from the index finger in the palm plane, forward = across the palm.</summary>
        internal void ThumbSwing(float outward, float forward)
        {
            if (!FingersFound) return;
            Transform j = Thumb.Joints[0];
            Vector3 normal = j.parent.InverseTransformDirection(Root.TransformDirection(NormalLocal)).normalized;
            Vector3 lateral = j.parent.InverseTransformDirection(Root.TransformDirection(LateralLocal)).normalized;
            j.localRotation = Quaternion.AngleAxis(outward, normal) * Quaternion.AngleAxis(forward, lateral) * Thumb.BaseLocal[0];
        }

        /// <summary>
        /// Index finger lifted / lowered at the knuckle about the knuckle line (pure up / down against the palm):
        /// + = up (towards the back of the hand), - = down (towards the palm).
        /// </summary>
        internal void IndexLift(float angle)
        {
            if (!FingersFound || Mathf.Approximately(angle, 0f)) return;
            Transform j = Index.Joints[0];
            Vector3 lateral = Root.TransformDirection(LateralLocal).normalized;
            Vector3 palmSide = Root.TransformDirection(NormalLocal).normalized;
            Vector3 along = (Index.Joints.Length > 1 ? Index.Joints[1].position : Index.Tip) - j.position;
            // + must move the finger away from the palm side.
            float sign = Vector3.Dot(Vector3.Cross(lateral, along), -palmSide) >= 0f ? 1f : -1f;
            Vector3 axisParent = j.parent.InverseTransformDirection(lateral).normalized;
            j.localRotation = Quaternion.AngleAxis(angle * sign, axisParent) * j.localRotation;
        }

        /// <summary>Thumb turned about its own length (which way the thumb pad faces).</summary>
        internal void ThumbTwist(float angle)
        {
            if (!FingersFound || Mathf.Approximately(angle, 0f)) return;
            Transform j = Thumb.Joints[0];
            Vector3 along = (Thumb.Joints.Length > 1 ? Thumb.Joints[1].position : Thumb.Tip) - j.position;
            if (along.sqrMagnitude < 1e-10f) return;
            Vector3 axisParent = j.parent.InverseTransformDirection(along).normalized;
            j.localRotation = Quaternion.AngleAxis(angle, axisParent) * j.localRotation;
        }

        private static readonly Quaternion[] curlFrom = new Quaternion[8];
        private const float BeckonBaseRate = 2f; // Base Up / Down drops twice as fast as Tip Bend

        /// <summary>
        /// "Come here" flick of the index finger, towards / away from the palm only (the user's recipe): from its current
        /// shape "Index: Base Up / Down" goes down by 2 * a (the knuckle turns about the palm's knuckle line towards the palm)
        /// while "Index: Tip Bend" goes down by a (the other joints straighten about their own hinges). a (min..max degrees)
        /// is picked so the tip gets as close to target as it can. Returns a.
        /// </summary>
        internal float IndexBeckon(Vector3 target, float min, float max)
        {
            if (!FingersFound) return 0f;
            Finger f = Index;
            int n = Mathf.Min(f.Joints.Length, curlFrom.Length);
            for (int k = 0; k < n; k++) curlFrom[k] = f.Joints[k].localRotation;

            // Knuckle: the palm's knuckle line, signed so + moves the finger towards the palm side.
            Transform j0 = f.Joints[0];
            Vector3 lateral = Root.TransformDirection(LateralLocal).normalized;
            Vector3 palmSide = Root.TransformDirection(NormalLocal).normalized;
            Vector3 along = (n > 1 ? f.Joints[1].position : f.Tip) - j0.position;
            float sign = Vector3.Dot(Vector3.Cross(lateral, along), palmSide) >= 0f ? 1f : -1f;
            Vector3 knuckleAxis = j0.parent.InverseTransformDirection(lateral * sign).normalized;

            float Miss(float angle)
            {
                j0.localRotation = Quaternion.AngleAxis(BeckonBaseRate * angle, knuckleAxis) * curlFrom[0]; // Base Up / Down - 2a
                for (int k = 1; k < n; k++)
                    if (f.AxisParent[k] != Vector3.zero)
                        f.Joints[k].localRotation = Quaternion.AngleAxis(-angle, f.AxisParent[k]) * curlFrom[k]; // Tip Bend - a
                return (f.Tip - target).sqrMagnitude;
            }

            // Coarse sweep, then narrow down around the best step (golden section).
            const float step = 10f;
            float best = 0f, bestMiss = Miss(0f);
            for (float a = min; a <= max + 0.01f; a += step)
            {
                float m = Miss(a);
                if (m < bestMiss) { bestMiss = m; best = a; }
            }
            float lo = Mathf.Max(min, best - step), hi = Mathf.Min(max, best + step);
            const float golden = 0.618034f;
            float x1 = hi - golden * (hi - lo), x2 = lo + golden * (hi - lo);
            float m1 = Miss(x1), m2 = Miss(x2);
            for (int i = 0; i < 12; i++)
            {
                if (m1 < m2) { hi = x2; x2 = x1; m2 = m1; x1 = hi - golden * (hi - lo); m1 = Miss(x1); }
                else { lo = x1; x1 = x2; m1 = m2; x2 = lo + golden * (hi - lo); m2 = Miss(x2); }
            }
            float angle = (lo + hi) * 0.5f;
            if (Miss(angle) > bestMiss) Miss(angle = best);
            return angle;
        }

        internal static void SaveFinger(Finger f, Quaternion[] into)
        {
            for (int k = 0; k < f.Joints.Length; k++) into[k] = f.Joints[k].localRotation;
        }

        internal static void LoadFinger(Finger f, Quaternion[] from, float[] angles)
        {
            for (int k = 0; k < f.Joints.Length; k++)
            {
                f.Joints[k].localRotation = from[k];
                f.Angle[k] = angles[k];
            }
        }

        internal void IkBonePose(Vector3 rootPos, Quaternion rootRot, out Vector3 pos, out Quaternion rot)
        {
            rot = rootRot * boneOffsetRot;
            pos = rootPos + rootRot * Vector3.Scale(boneOffsetPos, Poser.transform.lossyScale);
        }

        internal Quaternion[] SaveFingers()
        {
            var saved = new Quaternion[mirror.Length];
            for (int i = 0; i < mirror.Length; i++) saved[i] = mirror[i].localRotation;
            return saved;
        }

        internal void LoadFingers(Quaternion[] saved)
        {
            if (saved == null || saved.Length != mirror.Length) return;
            for (int i = 1; i < mirror.Length; i++) mirror[i].localRotation = saved[i];
        }

        // ------------------------------------------------------------------ EFT finger hookup

        internal void HoldFingers() => Poser.MapIkPalm(FingerPose);

        internal void ReleaseFingers()
        {
            if (Poser != null && FingerPose != null && ReferenceEquals(IkPalm(Poser), FingerPose)) Poser.MapIkPalm(null);
        }

        internal bool FingersTakenByOther
        {
            get
            {
                IWeaponGripPose current = IkPalm(Poser);
                return current != null && !ReferenceEquals(current, FingerPose);
            }
        }
    }
}
