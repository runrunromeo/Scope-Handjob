using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace ScopeHandjob
{
    /// <summary>
    /// Which sight the mode key is for, with a canted (offset 45 deg) sight on the gun:
    /// - aiming through the canted sight: only the canted sight switches (the main scope is left alone);
    /// - not aiming: only the main sight switches, even if the canted sight is the one selected for aiming;
    /// - aiming through the main sight: the main sight.
    /// Vanilla's Firearms.UpdateScopesMode snaps EVERY variable scope on the gun to min / max zoom on any mode change, so
    /// while a mode change goes ahead, ForceChangeScopeState is let through only for the sights the request switches.
    /// </summary>
    internal static partial class HandDriver
    {
        private static readonly HashSet<string> SnapAllowed = new HashSet<string>();
        private static bool requestActive;

        private const float CantedDegrees = 15f;

        /// <summary>
        /// The sight sits rolled about the barrel (a canted / offset mount). First the game's own figure: each aim point
        /// (ProceduralWeaponAnimation.ScopeAimTransforms, SightNBone) keeps its roll against the weapon in Rotation, set by
        /// FindAimTransforms; it is what the game turns the gun by when aiming through it, and what FOVFix Expansion
        /// uses for its canted distances (same 15 deg rule). FOVFix itself only moves the camera (LerpCamera), not the
        /// sights or the weapon, so neither this nor the shape fallback below depends on it.
        /// </summary>
        private static bool IsCanted(Player.FirearmController fc, EFT.Firearms firearms, SightModVisualControllers vc)
        {
            if (fc == null || firearms == null || vc == null) return false;
            string id = vc.SightMod?.Item?.Id;
            var aims = HandsOwner(fc)?.ProceduralWeaponAnimation?.ScopeAimTransforms;
            if (id != null && aims != null)
            {
                bool found = false;
                foreach (var aim in aims)
                {
                    if (aim?.Mod?.Item == null || aim.Mod.Item.Id != id) continue;
                    found = true;
                    float roll = Mathf.Abs(aim.Rotation % 360f);
                    if (roll > CantedDegrees && roll < 360f - CantedDegrees) return true;
                }
                if (found) return false;
            }
            return CantedByShape(fc, firearms, vc);
        }

        /// <summary>No aim point for this sight: its up, off the barrel axis, is not along any axis of the weapon.</summary>
        private static bool CantedByShape(Player.FirearmController fc, EFT.Firearms firearms, SightModVisualControllers vc)
        {
            Transform root = WeaponRoot(fc, firearms);
            Vector3 axis = fc.WeaponDirection;
            if (root == null || axis.sqrMagnitude < 1e-6f) return false;
            axis.Normalize();
            Vector3 up = Vector3.ProjectOnPlane(vc.transform.up, axis);
            if (up.sqrMagnitude < 0.04f) return false; // the sight's up runs along the barrel: cannot tell
            float best = 90f;
            foreach (Vector3 a in new[] { root.up, root.right, root.forward })
            {
                Vector3 line = Vector3.ProjectOnPlane(a, axis);
                if (line.sqrMagnitude < 0.04f) continue; // the barrel axis itself
                float angle = Vector3.Angle(up, line);
                best = Mathf.Min(best, Mathf.Min(angle, 180f - angle));
            }
            return best > CantedDegrees;
        }

        /// <summary>
        /// Not aiming: the mode key is for the main sight. Returns null = leave the request as it is; an empty array =
        /// drop it (canted sight selected, no main sight that can switch); else the request moved onto the main sight.
        /// </summary>
        private static ScopeState[] RedirectFromHip(Player.FirearmController fc, ScopeState[] states)
        {
            EFT.Firearms firearms = FirearmsOf(fc);
            int changed = -1;
            for (int i = 0; i < states.Length && changed < 0; i++)
            {
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, states[i].Id);
                int[] selected = vc?.SightMod?.ScopesSelectedModes;
                int index = states[i].ScopeIndexInsideSight;
                if (selected != null && index >= 0 && index < selected.Length && selected[index] != states[i].ScopeMode) changed = i;
            }
            if (changed < 0) return null;
            SightModVisualControllers chosen = ScopeTargets.ForSight(firearms, states[changed].Id);
            if (chosen == null || chosen.SightMod == null) return null;
            bool canted = IsCanted(fc, firearms, chosen);
            if (!canted && HandForMode(chosen, states[changed])) return null; // the main sight already, as it is

            // Main sight to switch instead: a magnifying one first; for a canted selection also one with reticle modes.
            int best = -1;
            bool bestMagnifies = false;
            for (int j = 0; j < states.Length; j++)
            {
                if (j == changed) continue;
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, states[j].Id);
                int index = states[j].ScopeIndexInsideSight;
                int[] selected = vc?.SightMod?.ScopesSelectedModes;
                if (selected == null || index < 0 || index >= selected.Length || IsCanted(fc, firearms, vc)) continue;
                if (ScopeTargets.IsMagnifying(vc, index))
                {
                    best = j;
                    bestMagnifies = true;
                    break;
                }
                if (canted && best < 0 && ScopeTargets.ModeCount(vc, index) > 1 && ScopeTargets.ShowsModes(vc)) best = j;
            }
            if (best < 0) return canted ? new ScopeState[0] : null;
            if (!canted && !bestMagnifies) return null;

            var copy = (ScopeState[])states.Clone();
            int[] chosenModes = chosen.SightMod.ScopesSelectedModes;
            copy[changed].ScopeMode = chosenModes[states[changed].ScopeIndexInsideSight]; // the selected sight stays as it is
            SightModVisualControllers main = ScopeTargets.ForSight(firearms, states[best].Id);
            copy[best].ScopeMode = main.SightMod.ScopesSelectedModes[states[best].ScopeIndexInsideSight] + 1; // what the game asks of a selected sight
            Log(canted
                ? $"mode key from the hip with the canted sight {chosen.name} selected: switching the main sight {main.name}"
                : $"mode key from the hip with {chosen.name} selected: switching the magnifying scope {main.name}");
            return copy;
        }

        /// <summary>A mode change goes ahead (SetScopeMode prefix let it through): remember which sights it switches.</summary>
        internal static void BeginModeRequest(Player.FirearmController fc, ScopeState[] states)
        {
            SnapAllowed.Clear();
            requestActive = false;
            if (!Settings.Active || states == null || fc == null) return;
            Player p = HandsOwner(fc);
            if (p == null || !p.IsYourPlayer) return;
            EFT.Firearms firearms = FirearmsOf(fc);
            if (firearms == null) return;
            foreach (ScopeState state in states)
            {
                SightModVisualControllers vc = ScopeTargets.ForSight(firearms, state.Id);
                int[] selected = vc?.SightMod?.ScopesSelectedModes;
                int index = state.ScopeIndexInsideSight;
                if (selected == null || index < 0 || index >= selected.Length || selected[index] != state.ScopeMode)
                    SnapAllowed.Add(state.Id);
            }
            requestActive = true;
        }

        internal static void EndModeRequest()
        {
            requestActive = false;
            SnapAllowed.Clear();
        }

        /// <summary>SightModVisualControllers.ForceChangeScopeState prefix: true = skip (this sight was not switched).</summary>
        internal static bool SkipZoomSnap(SightModVisualControllers vc)
        {
            if (!requestActive) return false;
            string id = vc?.SightMod?.Item?.Id;
            if (id == null || SnapAllowed.Contains(id)) return false;
            if (ScopeTargets.ZoomOf(vc) != null) Log($"mode key for another sight: {vc.name} keeps its zoom");
            return true;
        }
    }
}
