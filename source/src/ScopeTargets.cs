using System;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace ScopeHandjob
{
    internal enum PartKind
    {
        Knob,       // variable zoom ring (ScopeZoomHandler.ScopeSwitcher)
        Lever,      // mode lever swapped per mode (Specter DR, PS-320)
        Magnifier,  // flip magnifier moved between modes (HHS-1)
        Button,     // no separate part (buttons moulded into the body, e.g. Zeus-Pro): a press on top of the body
    }

    /// <summary>The scope part the hand goes to.</summary>
    internal sealed class ScopeTarget
    {
        internal PartKind Kind;
        internal bool IsCover; // flip part that is a lens cover (Aimpoint CS): its own speed (2. Timing / Lens Cover Speed)
        internal string Name;
        internal Transform Part;
        internal Vector3 LocalCenter;
        internal Transform EndPart;       // magnifier only: where it ends up
        internal Vector3 EndLocalCenter;
        internal bool FollowsKnob;        // Part is a ScopeSwitcher that really rotates
        internal ScopeZoomHandler Zoom;   // knob only: gives the turning axis (StartPivot -> EndPivot)
        internal string Key;              // scope template + part name: where a saved pose is filed
        internal string ScopeName;

        // Flip magnifier: one pose for both directions, measured at Home (the magnifier in line, first mode).
        internal Transform Home, Away;     // the part found in the "in line" mode / in the "flipped aside" mode (pose is filed under Home)
        internal Transform HomeHousing, AwayHousing; // the biggest magnifier part of each mode: gives the exact swing
        internal Vector3 HomeLocalCenter, AwayLocalCenter;
        internal bool Reverse;             // the magnifier is aside now and swings back to Home
        internal Transform[] Pieces;       // everything of the magnifier visible now (housing, lenses): moves together
        internal Transform[] EndPieces;    // the same in the mode it switches to (HHS-1 aside -> in line: the in-line 3x pieces)
        internal Transform BodyRoot;       // flip parts: the sight root and its body box in that space (which way round to flip)
        internal Vector3 BodyMin, BodyMax;

        // Lever: the lever object of every mode (Specter DR: Switch_000_LOD0 / Switch_001_LOD0); one pose serves both ways.
        internal Transform[] Levers;
        internal string[] KeyAlternates;   // other keys a pose for this part may have been saved under

        // Button: which mode object is shown now (0 = first). A rotary knob is at its posed spot in even modes, turned in odd ones.
        internal int ModeIndex;
        // Button: a point on the sight line (lens centre) in Part space. A ring twist (like the Vudu knob) turns about this line.
        internal Vector3 RingLocal;
        internal bool HasRing;

        internal bool Alive => Part != null && (Kind != PartKind.Magnifier || EndPart != null);
        internal Vector3 Center => Part.TransformPoint(LocalCenter);
        internal Vector3 EndCenter => EndPart.TransformPoint(EndLocalCenter);
        internal Vector3 HomeCenter => Home.TransformPoint(HomeLocalCenter);
        internal Vector3 AwayCenter => Away.TransformPoint(AwayLocalCenter);
    }

    internal static class ScopeTargets
    {
        // Parts that flip between the modes (a magnifier swung aside, a lens cover flipped open: WTT-Armory Aimpoint CS).
        // A scan of every sight bundle (2026-11-22) found "cover" inside mode objects only on the Aimpoint CS.
        private static readonly string[] MagnifierNames = { "g33", "magnifier", "cover" };
        private static readonly string[] LeverNames = { "switch", "lever" };

        private static readonly AccessTools.FieldRef<EFT.Firearms, SightModVisualControllers[]> VisualControllers =
            AccessTools.FieldRefAccess<EFT.Firearms, SightModVisualControllers[]>("_sightModVisualControllers");
        private static readonly AccessTools.FieldRef<SightModVisualControllers, ScopeZoomHandler> ZoomHandler =
            AccessTools.FieldRefAccess<SightModVisualControllers, ScopeZoomHandler>("_scopeZoomHandler");
        private static readonly AccessTools.FieldRef<SightModVisualControllers, ScopePrefabCache> PrefabCache =
            AccessTools.FieldRefAccess<SightModVisualControllers, ScopePrefabCache>("_scopePrefabCache");
        private static readonly AccessTools.FieldRef<SightComponent, ISightComponentTemplate> SightTemplate =
            AccessTools.FieldRefAccess<SightComponent, ISightComponentTemplate>("_template");

        internal static SightModVisualControllers Find(EFT.Firearms firearms, Func<SightModVisualControllers, bool> match)
        {
            if (firearms == null) return null;
            SightModVisualControllers[] all = VisualControllers(firearms);
            if (all == null) return null;
            foreach (SightModVisualControllers vc in all)
                if (vc != null && match(vc)) return vc;
            return null;
        }

        internal static SightModVisualControllers OwnerOf(EFT.Firearms firearms, ScopeZoomHandler handler) =>
            Find(firearms, vc => ZoomHandler(vc) == handler || vc.GetComponent<ScopeZoomHandler>() == handler);

        internal static SightModVisualControllers ForSight(EFT.Firearms firearms, string itemId) =>
            Find(firearms, vc => vc.SightMod != null && vc.SightMod.Item != null && vc.SightMod.Item.Id == itemId);

        /// <summary>
        /// How many modes the game cycles this scope through: the item's ModesCount, which is what
        /// SightComponent.SetScopeMode wraps with (mode + 1 reaching it becomes 0). Some mod items list a different
        /// number of Zooms (Eukyre's RMR HD: ModesCount 2, one zoom; Vortex M157: ModesCount 1, two zooms), so the
        /// Zooms length is only the fallback when ModesCount is missing. 0 = unknown.
        /// </summary>
        private static int GameModeCount(SightComponent sight, int index)
        {
            ISightComponentTemplate template = SightTemplate(sight);
            int[] counts = template?.ModesCount;
            if (counts != null && index >= 0 && index < counts.Length && counts[index] > 0) return counts[index];
            float[][] zooms = template?.Zooms;
            return zooms != null && index >= 0 && index < zooms.Length && zooms[index] != null ? zooms[index].Length : 0;
        }

        /// <summary>The mode the game ends up on for a requested mode (SetScopeMode: at or past the count = 0).</summary>
        private static int Wrapped(int mode, int count) => count > 0 && mode >= count ? 0 : mode;

        /// <summary>The magnification of one mode (a list shorter than the modes repeats its last value).</summary>
        private static float ZoomOfMode(float[] z, int mode) => z[Mathf.Clamp(mode, 0, z.Length - 1)];

        /// <summary>Does this mode change alter the magnification (not just the reticle)?</summary>
        internal static bool ChangesZoom(SightComponent sight, ScopeState state, ScopeZoomHandler zoom)
        {
            int index = state.ScopeIndexInsideSight;
            int[] selected = sight.ScopesSelectedModes;
            // Variable scope: the mode key snaps it to min / max zoom, but only when the key is meant for it (its mode is
            // asked to move on). Another sight's key (e.g. a canted red dot while aiming through it) leaves it alone.
            if (zoom != null) return selected == null || index < 0 || index >= selected.Length || selected[index] != state.ScopeMode;
            if (selected == null || index < 0 || index >= selected.Length) return false;
            int count = GameModeCount(sight, index);
            int from = Wrapped(selected[index], count), to = Wrapped(state.ScopeMode, count); // a one-mode sight stays as it is
            if (from == to) return false;
            float[][] zooms = SightTemplate(sight)?.Zooms;
            if (zooms == null || index >= zooms.Length || zooms[index] == null || zooms[index].Length == 0) return true;
            float[] z = zooms[index];
            return !Mathf.Approximately(ZoomOfMode(z, from), ZoomOfMode(z, to));
        }

        /// <summary>Does this request change the scope's mode at all (palette / reticle too)? Wraps like the game.</summary>
        internal static bool ChangesMode(SightComponent sight, ScopeState state)
        {
            int index = state.ScopeIndexInsideSight;
            int[] selected = sight.ScopesSelectedModes;
            if (selected == null || index < 0 || index >= selected.Length) return false;
            int count = GameModeCount(sight, index);
            if (count == 0) return selected[index] != state.ScopeMode; // unknown: take the request as it is
            return Wrapped(selected[index], count) != Wrapped(state.ScopeMode, count);
        }

        /// <summary>How many modes this scope of the sight has (a knob counts as switchable: 2).</summary>
        internal static int ModeCount(SightModVisualControllers vc, int index)
        {
            if (vc == null || vc.SightMod == null) return 0;
            if (ZoomOf(vc) != null) return 2;
            return GameModeCount(vc.SightMod, index);
        }

        /// <summary>Can this scope of the sight change magnification at all (knob, or modes with different zooms)?</summary>
        internal static bool IsMagnifying(SightModVisualControllers vc, int index)
        {
            if (vc == null || vc.SightMod == null) return false;
            if (ZoomOf(vc) != null) return true;
            float[][] zooms = SightTemplate(vc.SightMod)?.Zooms;
            if (zooms == null || index < 0 || index >= zooms.Length || zooms[index] == null || zooms[index].Length == 0) return false;
            float[] z = zooms[index];
            int count = GameModeCount(vc.SightMod, index);
            for (int i = 1; i < count; i++)
                if (!Mathf.Approximately(ZoomOfMode(z, i), z[0])) return true;
            return false;
        }
        internal static ScopeZoomHandler ZoomOf(SightModVisualControllers vc)
        {
            ScopeZoomHandler zoom = ZoomHandler(vc);
            return zoom != null ? zoom : vc.GetComponent<ScopeZoomHandler>();
        }

        internal static ScopeTarget ForKnob(ScopeZoomHandler zoom, SightModVisualControllers vc) => Tag(ForKnobPart(zoom, vc), vc);

        internal static ScopeTarget ForMode(SightModVisualControllers vc) => Tag(ForModePart(vc), vc);

        /// <summary>Which scope this is (item template), so a saved pose can be found again.</summary>
        private static ScopeTarget Tag(ScopeTarget t, SightModVisualControllers vc)
        {
            if (t == null) return null;
            string template = null;
            try
            {
                template = vc != null && vc.SightMod != null && vc.SightMod.Item != null ? vc.SightMod.Item.TemplateId.ToString() : null;
            }
            catch (Exception)
            {
                // keep the part name only
            }
            string scope = vc != null ? vc.name.Replace("(Clone)", "") : "scope";
            t.ScopeName = scope;
            // A flip magnifier is filed under its in-line housing, so both directions use the same pose.
            // A lever is filed under the first mode's lever, but a pose saved under another mode's lever is found too.
            Transform filed = t.Kind == PartKind.Magnifier && t.Home != null ? t.Home
                : t.Kind == PartKind.Lever && t.Levers != null && t.Levers.Length > 0 ? t.Levers[0] : t.Part;
            string prefix = (template ?? scope) + "|";
            t.Key = prefix + filed.name;
            if (t.Kind == PartKind.Lever && t.Levers != null)
            {
                var others = new System.Collections.Generic.List<string>();
                foreach (Transform l in t.Levers)
                    if (l != null && l != filed) others.Add(prefix + l.name);
                t.KeyAlternates = others.ToArray();
            }
            return t;
        }

        private static ScopeTarget ForKnobPart(ScopeZoomHandler zoom, SightModVisualControllers vc)
        {
            if (zoom != null && zoom.ScopeSwitcher != null)
            {
                return new ScopeTarget
                {
                    Kind = PartKind.Knob,
                    Name = zoom.ScopeSwitcher.name,
                    Part = zoom.ScopeSwitcher,
                    LocalCenter = LocalMeshCenter(zoom.ScopeSwitcher),
                    FollowsKnob = true,
                    Zoom = zoom,
                };
            }

            // No knob part (Nightforce NXS): use the lens centre so the hand still goes to the scope.
            ScopePrefabCache cache = vc != null ? CacheOf(vc) : null;
            Transform fallback = cache != null ? cache.GetLensCenter() : null;
            if (fallback == null) fallback = zoom != null ? zoom.transform : vc != null ? vc.transform : null;
            if (fallback == null) return null;
            return new ScopeTarget { Kind = PartKind.Knob, Name = fallback.name + " (no knob part)", Part = fallback };
        }

        private static ScopeTarget ForModePart(SightModVisualControllers vc)
        {
            ScopeZoomHandler zoom = ZoomOf(vc);
            if (zoom != null) return ForKnobPart(zoom, vc);

            ScopePrefabCache cache = CacheOf(vc);
            Transform root = cache != null ? cache.transform : vc.transform;
            // The mode objects sit right under the prefab root in game models; some mod models (WTT-Armory SUSAT, MARS:
            // SUSAT/SUSAT/Mode_1) put them one level down, under the body.
            Transform modes = ModeHolder(root);
            if (modes == null) return null;
            Transform current = null;
            int modeIndex = 0;
            for (int i = 0; i < modes.childCount; i++)
            {
                Transform child = modes.GetChild(i);
                if (!child.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) continue;
                if (child.gameObject.activeSelf)
                {
                    current = child;
                    break;
                }
                modeIndex++;
            }
            if (current == null) return null;

            Transform magnifier = FindNamed(current, MagnifierNames);
            if (magnifier != null)
            {
                // Home = the housing in the first mode that has one (HHS-1: mode_000, 3x, in line).
                Transform home = null, homeMode = null;
                for (int i = 0; i < modes.childCount && home == null; i++)
                {
                    Transform mode = modes.GetChild(i);
                    if (!mode.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) continue;
                    home = FindNamed(mode, MagnifierNames);
                    if (home != null) homeMode = mode;
                }
                for (int i = 0; i < modes.childCount; i++)
                {
                    Transform other = modes.GetChild(i);
                    if (other == current || !other.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) continue;
                    Transform end = FindNamed(other, MagnifierNames);
                    if (end == null) continue;
                    bool reverse = home != null && magnifier != home;
                    Transform away = reverse ? magnifier : end;
                    if (home == null) home = magnifier;
                    MeshFilter body = BodyMesh(root);
                    Vector3 bodyMin = Vector3.zero, bodyMax = Vector3.zero;
                    if (body != null) BodyBox(root, body, out bodyMin, out bodyMax);
                    return new ScopeTarget
                    {
                        BodyRoot = body != null ? root : null,
                        BodyMin = bodyMin,
                        BodyMax = bodyMax,
                        Kind = PartKind.Magnifier,
                        IsCover = magnifier.name.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0,
                        Name = magnifier.name + " -> " + end.name,
                        Part = magnifier,
                        LocalCenter = LocalMeshCenter(magnifier),
                        EndPart = end,
                        EndLocalCenter = LocalMeshCenter(end),
                        Home = home,
                        Away = away,
                        HomeLocalCenter = LocalMeshCenter(home),
                        AwayLocalCenter = LocalMeshCenter(away),
                        Reverse = reverse,
                        HomeHousing = Housing(home.parent, home),
                        AwayHousing = Housing(away.parent, away),
                        Pieces = MagnifierPieces(current, other, magnifier),
                        EndPieces = MagnifierPieces(other, current, end),
                    };
                }
            }

            Transform lever = FindNamed(current, LeverNames);
            if (lever != null)
            {
                var levers = new System.Collections.Generic.List<Transform>();
                for (int i = 0; i < modes.childCount; i++)
                {
                    Transform mode = modes.GetChild(i);
                    if (!mode.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) continue;
                    Transform l = FindNamed(mode, LeverNames);
                    if (l != null) levers.Add(l);
                }
                return new ScopeTarget
                {
                    Kind = PartKind.Lever,
                    Name = lever.name,
                    Part = lever,
                    LocalCenter = LocalMeshCenter(lever),
                    Levers = levers.ToArray(),
                };
            }
            ScopeTarget button = ForButton(root);
            if (button != null)
            {
                button.ModeIndex = modeIndex;
                Transform lens = null;
                try
                {
                    lens = cache != null ? cache.GetLensCenter() : null;
                }
                catch (Exception)
                {
                    // no lens: the body box centre below
                }
                if (lens != null)
                {
                    button.RingLocal = button.Part.InverseTransformPoint(lens.position);
                    button.HasRing = true;
                }
                else
                {
                    MeshFilter body = button.Part.GetComponent<MeshFilter>();
                    if (body != null && body.sharedMesh != null)
                    {
                        button.RingLocal = body.sharedMesh.bounds.center;
                        button.HasRing = true;
                    }
                }
            }
            return button;
        }

        /// <summary>
        /// Buttons moulded into the body (Zeus-Pro thermal: the mode buttons are on top): the target is the top centre
        /// of the scope's biggest body mesh; the pose editor sliders put the finger on the real button.
        /// </summary>
        private static ScopeTarget ForButton(Transform root)
        {
            MeshFilter body = BodyMesh(root);
            if (body == null) return null;

            // Top centre in the scope's own space (scope prefabs: y up, the sight line along z).
            BodyBox(root, body, out Vector3 min, out Vector3 max);
            Vector3 top = new Vector3((min.x + max.x) * 0.5f, max.y, (min.z + max.z) * 0.5f);
            return new ScopeTarget
            {
                Kind = PartKind.Button,
                Name = body.name + " (top button)",
                Part = body.transform,
                LocalCenter = body.transform.InverseTransformPoint(root.TransformPoint(top)),
            };
        }

        /// <summary>The sight's biggest body mesh outside the mode objects (not glass / lens / low LOD).</summary>
        private static MeshFilter BodyMesh(Transform root)
        {
            MeshFilter body = null;
            float biggest = -1f;
            foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null) continue;
                string n = f.name;
                if (n.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("linza", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("lens", StringComparison.OrdinalIgnoreCase) >= 0 || IsLowLod(n)) continue;
                if (InModeChild(f.transform, root)) continue;
                Renderer r = f.GetComponent<Renderer>();
                if (r != null && !r.enabled) continue;
                float size = Vector3.Scale(f.sharedMesh.bounds.size, f.transform.lossyScale).sqrMagnitude;
                if (size > biggest) { biggest = size; body = f; }
            }
            return body;
        }

        /// <summary>That body mesh's box in the sight root's space.</summary>
        private static void BodyBox(Transform root, MeshFilter body, out Vector3 min, out Vector3 max)
        {
            Bounds local = body.sharedMesh.bounds;
            min = Vector3.positiveInfinity;
            max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = root.InverseTransformPoint(body.transform.TransformPoint(corner));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        /// <summary>
        /// Does the flip part's centre pass through the sight body on this swing? Counts swing samples whose centre lies
        /// inside the body box (shrunk 2 mm, so a part resting against the body is not counted).
        /// </summary>
        internal static int SamplesInsideBody(ScopeTarget t, Hinge flip)
        {
            if (t.BodyRoot == null || flip == null || flip.Space == null || t.Home == null) return 0;
            Vector3 start = t.HomeCenter;
            Vector3 min = t.BodyMin + Vector3.one * 0.002f, max = t.BodyMax - Vector3.one * 0.002f;
            int inside = 0;
            for (int i = 1; i < 10; i++)
            {
                Vector3 p = t.BodyRoot.InverseTransformPoint(flip.Point(start, i / 10f));
                if (p.x > min.x && p.x < max.x && p.y > min.y && p.y < max.y && p.z > min.z && p.z < max.z) inside++;
            }
            return inside;
        }

        /// <summary>The object whose children are the mode_* objects: the root itself, else the nearest one below it.</summary>
        private static Transform ModeHolder(Transform root)
        {
            var queue = new System.Collections.Generic.Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Transform t = queue.Dequeue();
                for (int i = 0; i < t.childCount; i++)
                    if (t.GetChild(i).name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) return t;
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform child = t.GetChild(i);
                    // Do not look inside a mode object for more modes.
                    if (!child.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) queue.Enqueue(child);
                }
            }
            return null;
        }

        private static bool IsLowLod(string name) =>
            name.IndexOf("LOD1", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("LOD2", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("LOD3", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool InModeChild(Transform t, Transform root)
        {
            for (Transform c = t; c != null && c != root; c = c.parent)
                if (c.name.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Everything in the current mode that belongs to the magnifier: its housing plus each other visible part (the
        /// lenses) that sits inside the housing and has no identical twin in the other mode. The sight itself (same
        /// part, same place in both modes) and its own lens (outside the housing) stay put.
        /// </summary>
        private static Transform[] MagnifierPieces(Transform mode, Transform otherMode, Transform found)
        {
            Transform housing = Housing(mode, found);
            var pieces = new System.Collections.Generic.List<Transform> { housing };
            Renderer housingRenderer = housing.GetComponent<Renderer>();
            if (housingRenderer == null) return pieces.ToArray();
            Bounds area = housingRenderer.bounds;
            area.Expand(0.02f);
            foreach (Renderer r in mode.GetComponentsInChildren<Renderer>(true))
            {
                Transform t = r.transform;
                if (t == housing || t.IsChildOf(housing)) continue;
                if (HasTwin(t, otherMode)) continue;
                bool magnifierPart = NameMatches(t.name, MagnifierNames) && t.name.IndexOf("mount", StringComparison.OrdinalIgnoreCase) < 0;
                if (!magnifierPart && !area.Contains(r.bounds.center)) continue;
                // Parent already listed: it carries this one along.
                bool carried = false;
                foreach (Transform p in pieces)
                    if (t.IsChildOf(p)) { carried = true; break; }
                if (!carried) pieces.Add(t);
            }
            return pieces.ToArray();
        }

        /// <summary>
        /// The magnifier housing of a mode: the biggest magnifier-named mesh (the mode may also hold a lens glass with a
        /// similar name, e.g. "..._g33_glass_LOD0" in newer models, which can be what is found first). Uses the mesh's
        /// own size, so it also works on the inactive mode.
        /// </summary>
        private static Transform Housing(Transform mode, Transform fallback)
        {
            if (mode == null) return fallback;
            Transform housing = fallback;
            float biggest = -1f;
            foreach (MeshFilter f in mode.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null || !NameMatches(f.name, MagnifierNames) || f.name.IndexOf("mount", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                float size = Vector3.Scale(f.sharedMesh.bounds.size, f.transform.lossyScale).sqrMagnitude;
                if (size > biggest) { biggest = size; housing = f.transform; }
            }
            return housing;
        }

        private static bool NameMatches(string name, string[] keys)
        {
            foreach (string key in keys)
                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Same part in the same place in the other mode (e.g. the sight body under both modes).</summary>
        private static bool HasTwin(Transform t, Transform otherMode)
        {
            MeshFilter mine = t.GetComponent<MeshFilter>();
            foreach (Transform o in otherMode.GetComponentsInChildren<Transform>(true))
            {
                if (o == otherMode || o.name != t.name) continue;
                if ((o.localPosition - t.localPosition).sqrMagnitude > 1e-8f || Quaternion.Angle(o.localRotation, t.localRotation) > 0.1f) continue;
                MeshFilter theirs = o.GetComponent<MeshFilter>();
                if (mine == null || theirs == null || mine.sharedMesh == theirs.sharedMesh) return true;
            }
            return false;
        }

        /// <summary>
        /// The game shows a mode change only on a sight with at least two mode objects: ScopePrefabCache.SetMode returns
        /// at once otherwise. Mod sights such as the WW2 pack's ZF39 / ZF-4 / Filotecnica list two equal-zoom modes in
        /// their item data but hold one (or no) mode object, so the key switches nothing there. Unknown = yes.
        /// </summary>
        internal static bool ShowsModes(SightModVisualControllers vc)
        {
            ScopePrefabCache cache = vc != null ? CacheOf(vc) : null;
            return cache == null || cache.ModesCount >= 2;
        }

        /// <summary>A mode change (palette / reticle) that the sight actually shows.</summary>
        internal static bool ChangesVisibleMode(SightModVisualControllers vc, ScopeState state) =>
            vc != null && vc.SightMod != null && ChangesMode(vc.SightMod, state) && ShowsModes(vc);

        private static ScopePrefabCache CacheOf(SightModVisualControllers vc)
        {
            ScopePrefabCache cache = PrefabCache(vc);
            return cache != null ? cache : vc.GetComponent<ScopePrefabCache>();
        }

        private static Transform FindNamed(Transform parent, string[] keys)
        {
            foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            {
                if (t == parent) continue;
                foreach (string key in keys)
                    if (t.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return t;
            }
            return null;
        }

        /// <summary>Centre of the part's mesh in its own space (pivots are often far from the visible part).</summary>
        internal static Vector3 LocalMeshCenter(Transform t)
        {
            MeshFilter filter = t.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null) return filter.sharedMesh.bounds.center;
            SkinnedMeshRenderer skinned = t.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null && skinned.sharedMesh != null) return skinned.sharedMesh.bounds.center;
            foreach (MeshFilter child in t.GetComponentsInChildren<MeshFilter>(true))
                if (child.sharedMesh != null)
                    return t.InverseTransformPoint(child.transform.TransformPoint(child.sharedMesh.bounds.center));
            return Vector3.zero;
        }
    }
}
