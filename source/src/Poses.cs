using System;
using System.Collections.Generic;
using System.IO;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace ScopeHandjob
{
#pragma warning disable CS0649 // filled in from the poses JSON file
    /// <summary>A hand pose the player made with the pose editor, for one scope part and one hand.</summary>
    internal sealed class SavedPose
    {
        public string Scope;
        public string Part;
        public string Hand;
        public string Saved;
        public float[] Values;    // Settings.PoseSliders order
        public string[] Bones;    // hand-copy bone names, for Base
        public float[][] Base;    // starting finger shape (local rotation x, y, z, w per bone)
        public bool LeverPush;    // lever thrown forward / back instead of twisted
        public string ButtonFace; // button scopes: face of the body the buttons are on (null = Top)
        public float ButtonTwist; // button scopes: 0 = press; else a knob twist of this many degrees (SUSAT, MARS)
        public string TwistAxis;  // button scopes: the line the twist turns about (null = ScopeLine)
        public bool TwistBack;    // button twist: out and back each press (Walther MRS lever style), else it stays turned until the next mode
        public float? FlipFollow; // flip parts: how much of the flip the hand rides along (null = all of it)
        public bool FlipThumbOnly; // flip parts: only the thumb rides the flip, the hand stays put
        public bool FlipIndexOnly; // flip parts: only the index finger curls with the flip, the hand stays put
        public float? KnobScale;   // knob scopes: the zoom ring turns this many times its normal turn, zoom unchanged (null = 1)

        internal ButtonFace Face => System.Enum.TryParse(ButtonFace, out ButtonFace f) ? f : ScopeHandjob.ButtonFace.Top;
        internal TwistAxis Axis => System.Enum.TryParse(TwistAxis, out TwistAxis a) ? a : ScopeHandjob.TwistAxis.ScopeLine;
        internal float Follow => FlipFollow ?? 1f;
        internal FlipMover Mover => FlipIndexOnly ? FlipMover.IndexOnly : FlipThumbOnly ? FlipMover.ThumbOnly : FlipMover.WholeHand;
        internal float RingScale => KnobScale.HasValue && KnobScale.Value >= 0.05f ? KnobScale.Value : 1f;
    }
#pragma warning restore CS0649

    internal sealed class PoseFile
    {
        public int Version = 1;
        public Dictionary<string, SavedPose> Poses = new Dictionary<string, SavedPose>();
    }

    /// <summary>The hand poses, built into the DLL (poses.json, one per scope part and hand), read-only.</summary>
    internal static class PoseStore
    {
        private static PoseFile file;

        private static PoseFile Data
        {
            get
            {
                if (file != null) return file;
                try
                {
                    using (Stream s = typeof(PoseStore).Assembly.GetManifestResourceStream("ScopeHandjob.poses.json"))
                    using (var reader = s != null ? new StreamReader(s) : null)
                        file = reader != null ? JsonConvert.DeserializeObject<PoseFile>(reader.ReadToEnd()) : null;
                    if (file == null) Plugin.Log.LogError(Plugin.LogTag + " built-in poses missing: no hand moves");
                }
                catch (Exception error)
                {
                    Plugin.Log.LogError(Plugin.LogTag + " could not read the poses: " + error.Message);
                }
                if (file == null) file = new PoseFile();
                if (file.Poses == null) file.Poses = new Dictionary<string, SavedPose>();
                return file;
            }
        }

        internal static int Count => Data.Poses.Count;

        internal static string KeyFor(ScopeTarget t, HandSide side) => t.Key + "|" + side;

        /// <summary>
        /// A flip part's separate closing pose (lens cover: a thumb flicks it open from below, a finger pushes it shut from
        /// the outside). Filed next to the opening pose with "#close" on the part name. Null = the part has none and
        /// closes with the opening pose, as before.
        /// </summary>
        internal static SavedPose FindClose(ScopeTarget t, HandSide side, out string key)
        {
            key = t.Key + "#close|" + side;
            return Get(key);
        }

        /// <summary>The saved pose for this part (also one saved under another mode's lever); key = where it is filed.</summary>
        internal static SavedPose Find(ScopeTarget t, HandSide side, out string key)
        {
            key = KeyFor(t, side);
            SavedPose pose = Get(key);
            if (pose != null) return pose;
            if (t.KeyAlternates != null)
                foreach (string other in t.KeyAlternates)
                {
                    pose = Get(other + "|" + side);
                    if (pose != null)
                    {
                        key = other + "|" + side;
                        return pose;
                    }
                }
            return FindColourVariant(t, side, ref key);
        }

        private static readonly string[] ColourSuffixes =
            { "_tan", "_fde", "_black", "_blk", "_od", "_olive", "_sand", "_grey", "_gray", "_white", "_green", "_ranger" };

        /// <summary>
        /// Scope model name without its colour ("scope_all_eotech_hhs_1_tan" -> "scope_all_eotech_hhs_1"; Epic's AIO names
        /// it with a space: "Eleanor 1-10 FDE" / "Eleanor 1-10 BLK" -> "eleanor 1-10").
        /// </summary>
        internal static string ModelName(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return scope;
            string s = scope.Replace("(Clone)", "").Trim().ToLowerInvariant();
            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                foreach (string suffix in ColourSuffixes)
                {
                    string spaced = " " + suffix.Substring(1);
                    string found = s.EndsWith(suffix, StringComparison.Ordinal) ? suffix : s.EndsWith(spaced, StringComparison.Ordinal) ? spaced : null;
                    if (found == null || s.Length <= found.Length) continue;
                    s = s.Substring(0, s.Length - found.Length).TrimEnd();
                    stripped = true;
                }
            }
            return s;
        }

        /// <summary>
        /// Same scope in another colour (Specter DR black / FDE, HHS-1 black / tan): same model and part names, so the
        /// pose saved on one is used for the other. Key = the other colour's entry (saving edits that shared pose).
        /// </summary>
        private static SavedPose FindColourVariant(ScopeTarget t, HandSide side, ref string key)
        {
            string model = ModelName(t.ScopeName);
            if (string.IsNullOrEmpty(model) || t.Key == null) return null;
            var parts = new List<string> { PartOf(t.Key) };
            if (t.KeyAlternates != null) foreach (string other in t.KeyAlternates) parts.Add(PartOf(other));
            string own = key;
            foreach (KeyValuePair<string, SavedPose> entry in Data.Poses)
            {
                if (entry.Key == own || entry.Value == null || ModelName(entry.Value.Scope) != model) continue;
                foreach (string part in parts)
                {
                    if (part == null || !entry.Key.EndsWith("|" + part + "|" + side, StringComparison.Ordinal)) continue;
                    SavedPose pose = Get(entry.Key);
                    if (pose == null) continue;
                    key = entry.Key;
                    return pose;
                }
            }
            return null;
        }

        private static string PartOf(string key)
        {
            int bar = key != null ? key.IndexOf('|') : -1;
            return bar >= 0 ? key.Substring(bar + 1) : null;
        }

        internal static SavedPose Get(string key)
        {
            if (key == null || !Data.Poses.TryGetValue(key, out SavedPose p) || p.Values == null || p.Values.Length == 0) return null;
            int count = Settings.PoseValueCount;
            if (p.Values.Length > count) return null; // saved by a newer version
            if (p.Values.Length < count)
            {
                // Saved before a slider was added: the new sliders start at their default.
                var values = new float[count];
                for (int i = 0; i < count; i++)
                    values[i] = i < p.Values.Length ? p.Values[i] : Settings.PoseValueDefault(i);
                // Before 0.2.0 value 11 bent all joints of the other three fingers: keep that look.
                if (p.Values.Length <= 15 && p.Values.Length > 11) values[15] = p.Values[11];
                // Before 0.4.0 value 8 bent both outer thumb joints: the new tip bend starts the same.
                if (p.Values.Length <= 17 && p.Values.Length > 8) values[17] = p.Values[8];
                p.Values = values;
            }
            return p;
        }

        /// <summary>The saved starting finger shape, lined up with this hand copy's bones (null if it does not fit).</summary>
        internal static Quaternion[] BaseFor(SavedPose pose, HandRig rig)
        {
            if (pose?.Bones == null || pose.Base == null || pose.Bones.Length != pose.Base.Length) return null;
            Transform[] bones = rig.CopyBones;
            var byName = new Dictionary<string, float[]>();
            for (int i = 0; i < pose.Bones.Length; i++) byName[pose.Bones[i]] = pose.Base[i];
            var result = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (i == 0) { result[i] = bones[i].localRotation; continue; }
                if (!byName.TryGetValue(bones[i].name, out float[] q) || q == null || q.Length != 4) return null;
                result[i] = new Quaternion(q[0], q[1], q[2], q[3]);
            }
            return result;
        }
    }

    /// <summary>
    /// The scope part's own frame the pose sliders are measured in: knob centre, its axis (pointing to the muzzle),
    /// "up" (away from the barrel) and the hand's side. Stored in the part's parent space, so it follows the gun.
    /// </summary>
    internal sealed class PoseFrame
    {
        internal Transform Anchor;
        internal Vector3 CentreLocal, AxisLocal, UpLocal, SideLocal;
        internal Vector3 RightLocal; // to the right as you look down the gun (whichever hand)
        internal Hinge Flip;
        internal Hinge LeverSwing; // lever: pose position -> the other position, about the lever's own pivot
        // Lever with 3+ positions (Walther MRS: 4): where each lever (ScopeTarget.Levers order) sits, in degrees from the
        // pose's lever about LeverSwing's axis (+ = LeverSwing's way). Null for two-position levers or when the steps are
        // too big for a hand to ride (BOSS Xe dial: 100+ deg).
        internal float[] LeverAngles;
        internal bool FlipOtherWay; // flip part: the short way cut through the sight, it goes the other way round
        internal string LeverSwingSource;
        internal Vector3[] BodyCornersLocal; // button scope: the body's box corners (Anchor space), to find each face
        internal Vector3 RingCentreLocal;    // button scope: centre on the sight line (Anchor space), for a ring twist
        internal bool HasRing;

        /// <summary>A part's measured flip / lever motion, in its anchor's space (see Create).</summary>
        private sealed class Motion
        {
            internal Transform Space;
            internal Hinge Flip, LeverSwing;
            internal bool FlipOtherWay;
            internal string LeverSwingSource;
            internal float[] LeverAngles;
        }

        private static readonly System.Collections.Generic.Dictionary<int, Motion> Motions = new System.Collections.Generic.Dictionary<int, Motion>();

        internal static PoseFrame Create(ScopeTarget t, Player.FirearmController fc, Transform view, HandSide side, Transform poseLever = null)
        {
            // Flip magnifier: everything is measured at Home (in line), whichever way it swings this time.
            // Lever: measured at the lever position the pose was made on.
            bool magnifier = t.Kind == PartKind.Magnifier && t.Home != null && t.Away != null;
            bool lever = t.Kind == PartKind.Lever && poseLever != null;
            Transform measured = magnifier ? t.Home : lever ? poseLever : t.Part;
            Transform anchor = measured.parent != null ? measured.parent : measured;
            Vector3 muzzle = fc.WeaponDirection;
            if (muzzle.sqrMagnitude < 0.5f) muzzle = view.forward;
            muzzle.Normalize();

            Vector3 axis = muzzle;
            if (t.Kind == PartKind.Knob && t.Zoom != null && t.Zoom.StartPivot != null && t.Zoom.EndPivot != null)
            {
                (t.Zoom.EndPivot.rotation * Quaternion.Inverse(t.Zoom.StartPivot.rotation)).ToAngleAxis(out float angle, out Vector3 a);
                if (angle > 1f && angle < 359f && !float.IsNaN(a.x) && a.sqrMagnitude > 0.5f) axis = a.normalized;
            }
            if (Vector3.Dot(axis, muzzle) < 0f) axis = -axis;

            Vector3 centre = t.Kind == PartKind.Knob
                ? t.Part.position + axis * Vector3.Dot(t.Center - t.Part.position, axis)
                : magnifier ? t.HomeCenter
                : lever ? poseLever.TransformPoint(ScopeTargets.LocalMeshCenter(poseLever)) : t.Center;

            Vector3 up = Vector3.zero;
            try
            {
                up = Vector3.ProjectOnPlane(centre - fc.FireportPosition, axis); // away from the barrel
            }
            catch (Exception)
            {
                // no fireport: use the view
            }
            if (up.sqrMagnitude < 1e-6f || Vector3.Dot(up, view.up) < -0.2f * up.magnitude) up = Vector3.ProjectOnPlane(view.up, axis);
            up.Normalize();
            Vector3 left = Vector3.Cross(axis, up).normalized;
            if (Vector3.Dot(left, view.right) > 0f) left = -left;
            Vector3 sideDir = side == HandSide.Left ? left : -left;

            var frame = new PoseFrame
            {
                Anchor = anchor,
                CentreLocal = anchor.InverseTransformPoint(centre),
                AxisLocal = anchor.InverseTransformDirection(axis).normalized,
                UpLocal = anchor.InverseTransformDirection(up).normalized,
                SideLocal = anchor.InverseTransformDirection(sideDir).normalized,
                RightLocal = anchor.InverseTransformDirection(-left).normalized,
            };
            // The flip / lever motion is fitted from the part's meshes (every vertex) and only depends on the part, so it is
            // worked out once per part and reused on every later press.
            int motionKey = (magnifier || lever) ? measured.GetInstanceID() : 0;
            if (motionKey != 0 && Motions.TryGetValue(motionKey, out Motion known) && known != null && known.Space == anchor)
            {
                frame.Flip = known.Flip;
                frame.FlipOtherWay = known.FlipOtherWay;
                frame.LeverSwing = known.LeverSwing;
                frame.LeverSwingSource = known.LeverSwingSource;
                frame.LeverAngles = known.LeverAngles;
                magnifier = lever = false; // already known
            }
            else if (motionKey != 0 && Motions.Count > 128) Motions.Clear();
            if (magnifier)
            {
                // The swing Home -> Away (in line -> aside); the way back runs it backwards.
                // Best source: one part that exists in both modes, same mesh, moved (HHS-1: backLens / backLens (1)).
                string source = "a part placed in both modes";
                frame.Flip = HingeFromPair(t.Home.parent, t.Away.parent, anchor);
                if (frame.Flip == null)
                {
                    source = "the two housing shapes";
                    frame.Flip = Hinge.FromPoints(WeaponPoints.Vertices(t.HomeHousing ?? t.Home, Matrix4x4.identity, out bool exactA),
                        WeaponPoints.Vertices(t.AwayHousing ?? t.Away, Matrix4x4.identity, out bool exactB), anchor);
                    if (frame.Flip != null && (!exactA || !exactB)) frame.Flip = null;
                }
                if (frame.Flip == null)
                {
                    source = "GUESSED";
                    frame.Flip = Hinge.GuessFlip(t.HomeCenter, t.AwayCenter, anchor, muzzle);
                }
                // The fitted swing is the short way round. If that cuts through the sight body and the other way does not
                // as much, the part really goes the other way (Aimpoint CS rear cover: 239 deg back over the top).
                if (frame.Flip != null)
                {
                    Hinge other = frame.Flip.OtherWay();
                    int shortIn = ScopeTargets.SamplesInsideBody(t, frame.Flip), longIn = ScopeTargets.SamplesInsideBody(t, other);
                    if (shortIn > 0 && longIn < shortIn)
                    {
                        HandDriver.Log($"flip: the short way ({frame.Flip.Angle:0} deg) passes through the sight body ({shortIn}/9), the other way ({other.Angle:0} deg) {longIn}/9: going the other way");
                        frame.Flip = other;
                        frame.FlipOtherWay = true;
                    }
                }
                HandDriver.Log($"magnifier swing from {source}: " +
                               $"{frame.Flip?.Angle ?? 0f:0} deg, {(t.Reverse ? "back in line" : "aside")}, moving {t.Pieces?.Length ?? 0} parts: " +
                               string.Join(", ", Array.ConvertAll(t.Pieces ?? new Transform[0], p => p.name)));
            }
            if (lever) LeverSwingFrom(frame, t, poseLever, anchor);
            if (magnifier || lever)
                Motions[motionKey] = new Motion
                {
                    Space = anchor, Flip = frame.Flip, FlipOtherWay = frame.FlipOtherWay, LeverSwing = frame.LeverSwing,
                    LeverSwingSource = frame.LeverSwingSource, LeverAngles = frame.LeverAngles,
                };
            if (t.Kind == PartKind.Button)
            {
                MeshFilter body = t.Part.GetComponent<MeshFilter>();
                if (body != null && body.sharedMesh != null)
                {
                    Bounds b = body.sharedMesh.bounds;
                    frame.BodyCornersLocal = new Vector3[8];
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        frame.BodyCornersLocal[i] = anchor.InverseTransformPoint(t.Part.TransformPoint(corner));
                    }
                }
                if (t.HasRing)
                {
                    // On the scope's line through the lens, level with the button centre along the scope (like a knob ring).
                    Vector3 ring = t.Part.TransformPoint(t.RingLocal);
                    ring += axis * Vector3.Dot(centre - ring, axis);
                    frame.RingCentreLocal = anchor.InverseTransformPoint(ring);
                    frame.HasRing = true;
                }
            }
            return frame;
        }

        /// <summary>
        /// How the lever itself moves between its two positions (Specter DR: 90 deg, PS-320: 103 deg, both about an
        /// upright pivot at the bottom of the scope), so a forward / back push can follow it.
        /// </summary>
        private static void LeverSwingFrom(PoseFrame frame, ScopeTarget t, Transform poseLever, Transform anchor)
        {
            Transform other = null;
            if (t.Levers != null)
                foreach (Transform l in t.Levers)
                    if (l != null && l != poseLever) { other = l; break; }
            if (other == null) { frame.LeverSwingSource = "GUESSED (only one lever)"; return; }
            try
            {
                Vector3[] a = WeaponPoints.Vertices(poseLever, Matrix4x4.identity, out bool exactA);
                Vector3[] b = WeaponPoints.Vertices(other, Matrix4x4.identity, out bool exactB);
                if (exactA && exactB) frame.LeverSwing = Hinge.FromPoints(a, b, anchor);
                if (frame.LeverSwing != null) frame.LeverSwingSource = "the two lever shapes";
            }
            catch (Exception e)
            {
                HandDriver.Log("lever shapes: " + e.Message);
            }
            if (frame.LeverSwing == null)
            {
                Quaternion r = other.rotation * Quaternion.Inverse(poseLever.rotation);
                frame.LeverSwing = Hinge.FromMotion(r, other.position - r * poseLever.position, anchor);
                if (frame.LeverSwing != null) frame.LeverSwingSource = "the two lever positions";
            }
            if (frame.LeverSwing == null) frame.LeverSwingSource = "GUESSED";
            HandDriver.Log($"lever swing from {frame.LeverSwingSource}: {frame.LeverSwing?.Angle ?? 0f:0} deg, {poseLever.name} -> {other.name}");
            if (frame.LeverSwing != null && t.Levers.Length >= 3) frame.LeverAngles = LeverAnglesFrom(t.Levers, poseLever, frame.LeverSwing, anchor);
        }

        /// <summary>
        /// A lever / dial with more than two positions (Walther MRS: 23, 3.6, -17, -39 deg about its pivot, so three
        /// presses one way and the fourth all the way back; BOSS Xe dial): each position's angle from the pose's lever
        /// about the same pivot. Only the direction of each press is used (the hand pushes a short way, HandDriver
        /// .AimLever), so big steps are fine. Null when a position turns about another axis.
        /// </summary>
        private static float[] LeverAnglesFrom(Transform[] levers, Transform poseLever, Hinge swing, Transform anchor)
        {
            Vector3[] a;
            bool exactA;
            try
            {
                a = WeaponPoints.Vertices(poseLever, Matrix4x4.identity, out exactA);
            }
            catch (Exception)
            {
                a = null;
                exactA = false;
            }
            var angles = new float[levers.Length];
            for (int i = 0; i < levers.Length; i++)
            {
                Transform l = levers[i];
                if (l == null) return null;
                if (l == poseLever) continue;
                Hinge h = null;
                try
                {
                    if (exactA)
                    {
                        Vector3[] b = WeaponPoints.Vertices(l, Matrix4x4.identity, out bool exactB);
                        if (exactB) h = Hinge.FromPoints(a, b, anchor);
                    }
                }
                catch (Exception)
                {
                    // the transforms below
                }
                if (h == null)
                {
                    Quaternion r = l.rotation * Quaternion.Inverse(poseLever.rotation);
                    h = Hinge.FromMotion(r, l.position - r * poseLever.position, anchor);
                }
                if (h == null) continue; // same place as the pose's lever: 0
                float along = Vector3.Dot(h.AxisLocal, swing.AxisLocal);
                if (Mathf.Abs(along) < 0.95f) return null; // not the same pivot
                angles[i] = along > 0f ? h.Angle : -h.Angle;
            }
            HandDriver.Log("lever positions " + string.Join(" / ", Array.ConvertAll(angles, x => x.ToString("0"))) + " deg: the hand pushes each press's way");
            return angles;
        }

        /// <summary>
        /// The magnifier's rigid swing from a part that both modes contain with the same mesh but in a different place
        /// (its transform moved, not its vertices), e.g. the HHS-1's rear lens. Null if there is no such part.
        /// </summary>
        private static Hinge HingeFromPair(Transform homeMode, Transform awayMode, Transform space)
        {
            if (homeMode == null || awayMode == null || homeMode == awayMode) return null;
            MeshFilter[] away = awayMode.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter a in homeMode.GetComponentsInChildren<MeshFilter>(true))
            {
                if (a.sharedMesh == null) continue;
                foreach (MeshFilter b in away)
                {
                    if (b.sharedMesh != a.sharedMesh) continue;
                    if ((a.transform.lossyScale - b.transform.lossyScale).sqrMagnitude > 1e-6f) continue;
                    Quaternion r = b.transform.rotation * Quaternion.Inverse(a.transform.rotation);
                    Vector3 moved = b.transform.position - r * a.transform.position;
                    if (Quaternion.Angle(r, Quaternion.identity) < 1f) continue; // did not swing (the sight body)
                    Hinge h = Hinge.FromMotion(r, moved, space);
                    if (h != null)
                    {
                        HandDriver.Log($"magnifier hinge from {a.name} -> {b.name}");
                        return h;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>Turns the pose slider values into a hand placement + finger shape (as a GripPlan).</summary>
    internal static class PoseBuilder
    {
        internal static GripPlan Build(HandRig rig, PoseFrame frame, float[] v, bool leverPush = false, ButtonFace face = ButtonFace.Top,
            float buttonTwist = 0f, TwistAxis twistAxis = TwistAxis.ScopeLine, float flipFollow = 1f, FlipMover flipMover = FlipMover.WholeHand)
        {
            Transform a = frame.Anchor;
            Vector3 centre = a.TransformPoint(frame.CentreLocal);
            Vector3 axis = a.TransformDirection(frame.AxisLocal).normalized;
            Vector3 up = a.TransformDirection(frame.UpLocal).normalized;
            Vector3 side = a.TransformDirection(frame.SideLocal).normalized;
            Vector3 right = a.TransformDirection(frame.RightLocal).normalized;

            // "Around" is measured about the part; a button scope measures it about the chosen face's outward line.
            // Ring twist (button scope, Twist not 0): placed and turned like the Vudu knob, about the scope's own line.
            bool ring = frame.BodyCornersLocal != null && frame.HasRing && Mathf.Abs(buttonTwist) > 0.5f;
            if (ring) centre = a.TransformPoint(frame.RingCentreLocal);
            Vector3 outUp = up, outSide = side;
            if (frame.BodyCornersLocal != null && !ring)
            {
                Vector3 normal = face == ButtonFace.Right ? right : face == ButtonFace.Left ? -right
                    : face == ButtonFace.Front ? axis : face == ButtonFace.Rear ? -axis : up;
                Vector3 boxCentre = Vector3.zero;
                foreach (Vector3 c in frame.BodyCornersLocal) boxCentre += a.TransformPoint(c);
                boxCentre /= frame.BodyCornersLocal.Length;
                float reach = 0f;
                foreach (Vector3 c in frame.BodyCornersLocal) reach = Mathf.Max(reach, Vector3.Dot(a.TransformPoint(c) - boxCentre, normal));
                centre = boxCentre + normal * reach;
                Vector3 line = face == ButtonFace.Front || face == ButtonFace.Rear ? up : axis; // the "knob axis" lying on that face
                outUp = normal;
                outSide = Vector3.Cross(line, normal).normalized;
                float towardHand = Vector3.Dot(outSide, side);
                if (towardHand < -0.1f || (Mathf.Abs(towardHand) <= 0.1f && Vector3.Dot(outSide, up) < 0f)) outSide = -outSide;
            }

            float around = v[0] * Mathf.Deg2Rad;
            Vector3 radial = (outUp * Mathf.Cos(around) + outSide * Mathf.Sin(around)).normalized;    // from the knob out to the palm
            Vector3 tangent = (outUp * Mathf.Sin(around) - outSide * Mathf.Cos(around)).normalized;   // where the fingers point at 0 turn
            float upDown = v.Length > 12 ? v[12] / 1000f : 0f;
            float leftRight = v.Length > 16 ? v[16] / 1000f : 0f;
            Vector3 palm = centre + radial * (v[1] / 1000f) + axis * (v[2] / 1000f) + up * upDown + right * leftRight;
            Quaternion facing = Quaternion.LookRotation(tangent, -radial) * Quaternion.Euler(v[3], v[4], v[5]);
            Quaternion rootRot = facing * Quaternion.Inverse(rig.PalmBasisLocal);
            Vector3 rootPos = palm - rootRot * Vector3.Scale(rig.PalmCentreLocal, rig.Root.lossyScale);
            rig.Root.SetPositionAndRotation(rootPos, rootRot);

            if (rig.FingersFound)
            {
                // "Forward / Back" places the index finger (not the palm centre) along the scope: 0 = over the knob.
                // (A button on the front / rear face: line the knuckle up across the face instead.)
                Vector3 knuckleLine = frame.BodyCornersLocal != null && !ring && (face == ButtonFace.Front || face == ButtonFace.Rear) ? up : axis;
                float shift = Vector3.Dot(rig.Index.Knuckle - (centre + axis * (v[2] / 1000f)), knuckleLine);
                rootPos -= knuckleLine * shift;
                rig.Root.position = rootPos;

                rig.ResetFingers();
                rig.ThumbSwing(v[6], v[7]);
                rig.ThumbTwist(v.Length > 18 ? v[18] : 0f);
                HandRig.CurlJoints(rig.Thumb, 1, 2, v[8]);                                            // middle joint
                HandRig.CurlJoints(rig.Thumb, 2, rig.Thumb.Joints.Length, v.Length > 17 ? v[17] : v[8]); // tip joint
                HandRig.CurlJoints(rig.Index, 0, 1, v[9]);
                rig.IndexLift(v.Length > 19 ? v[19] : 0f);
                HandRig.CurlJoints(rig.Index, 1, rig.Index.Joints.Length, v[10]);
                foreach (Finger f in rig.Others)
                {
                    HandRig.CurlJoints(f, 0, 1, v[11]);
                    HandRig.CurlJoints(f, 1, f.Joints.Length, v.Length > 15 ? v[15] : v[11]);
                }
            }

            // Lever push: about the lever's own pivot towards its other position; guess = upright through the lever, + to the muzzle.
            Vector3 pushAxis, pushPivot;
            bool pushFromLever = frame.LeverSwing != null && frame.LeverSwing.Space != null;
            if (pushFromLever)
            {
                pushAxis = frame.LeverSwing.Space.TransformDirection(frame.LeverSwing.AxisLocal).normalized;
                pushPivot = frame.LeverSwing.Space.TransformPoint(frame.LeverSwing.PivotLocal);
            }
            else
            {
                pushAxis = up;
                pushPivot = centre;
                if (Vector3.Dot(Vector3.Cross(pushAxis, rootPos - pushPivot), axis) < 0f) pushAxis = -pushAxis;
            }

            // Knob twist on a button scope: about the scope's line through the sight line, or a dial on top / on the side
            // held between the thumb and index tips.
            Vector3 twistAxisDir = twistAxis == TwistAxis.UpDown ? up : twistAxis == TwistAxis.LeftRight ? right : axis;
            Vector3 twistPivot = twistAxis == TwistAxis.ScopeLine || !rig.FingersFound ? centre : (rig.Thumb.Tip + rig.Index.Tip) * 0.5f;

            return new GripPlan
            {
                Anchor = a,
                PushAxisLocal = a.InverseTransformDirection(pushAxis).normalized,
                PushPivotLocal = a.InverseTransformPoint(pushPivot),
                PushFromLever = pushFromLever,
                LeverAngles = pushFromLever ? frame.LeverAngles : null,
                PressDirLocal = a.InverseTransformDirection(-outUp).normalized,
                ButtonTwist = ring ? buttonTwist : 0f,
                TwistAxisLocal = a.InverseTransformDirection(twistAxisDir).normalized,
                TwistPivotLocal = a.InverseTransformPoint(twistPivot),
                RootPos = a.InverseTransformPoint(rootPos),
                RootRot = Quaternion.Inverse(a.rotation) * rootRot,
                Fingers = rig.SaveFingers(),
                CentreLocal = a.InverseTransformPoint(centre),
                AxisLocal = frame.AxisLocal,
                TwistMin = Mathf.Min(0f, v[13]), // values always come padded to the full count (PoseStore.Get)
                TwistMax = Mathf.Max(0f, v[14]),
                FromPose = true,
                LeverAngle = Settings.LeverTwistDegrees,
                LeverPush = leverPush,
                ApproachLocal = a.InverseTransformVector(radial * 0.07f),
                Flip = frame.Flip,
                FlipFollow = Mathf.Clamp01(flipFollow),
                FlipMover = flipMover,
            };
        }
    }
}
