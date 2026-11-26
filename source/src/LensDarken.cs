using System;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace ScopeHandjob
{
    /// <summary>
    /// Lever scopes that change magnification (Specter DR, ELCAN 1.5/6, PS-320): a real one turns a lens group inside
    /// about an upright axis when the lever is thrown, so mid-throw the picture goes dark and blurred, then comes back at
    /// the new magnification. Done on EFT's optic camera picture (only what you see through the scope): a command buffer
    /// at the end of that camera blends in a blurred copy (three bilinear halvings, blown back up), then darkens it.
    /// Strength follows the hand: 0 at the start, most half way (where the mode changes), 0 at the end.
    /// Any error switches the effect off for the rest of the session, the hand carries on.
    /// </summary>
    internal static class LensDarken
    {
        private const CameraEvent When = CameraEvent.AfterEverything;
        private static readonly int Half = Shader.PropertyToID("_ScopeHandjobBlur2"),
            Quarter = Shader.PropertyToID("_ScopeHandjobBlur4"),
            Eighth = Shader.PropertyToID("_ScopeHandjobBlur8"),
            GuiZTest = Shader.PropertyToID("unity_GUIZTestMode");

        private static CommandBuffer buffer;
        private static Camera attached;
        private static Material darkMaterial, blurMaterial;
        private static Mesh quad;
        private static bool broken;
        private static ScopeTarget rootOf;
        private static Transform root;

        /// <summary>Every frame of a move: strength 0..1 of the effect now (0 = off).</summary>
        internal static void Show(float strength, ScopeTarget target)
        {
            if (broken) return;
            try
            {
                float dark = Mathf.Clamp01(Settings.LeverDarken.Value) * strength;
                float blur = Mathf.Clamp01(Settings.LeverBlur.Value) * strength;
                Camera camera = target != null && (dark > 0.002f || blur > 0.002f) ? OpticCameraOf(target) : null;
                if (camera == null)
                {
                    Off();
                    return;
                }
                if (!Ready()) return;
                if (attached != camera)
                {
                    Off();
                    camera.AddCommandBuffer(When, buffer);
                    attached = camera;
                }
                Record(camera, dark, blur);
            }
            catch (Exception error)
            {
                broken = true;
                Off();
                Plugin.Log.LogWarning(Plugin.LogTag + " scope picture darkening switched off: " + error.Message);
            }
        }

        internal static void Off()
        {
            if (attached == null) return; // nothing on a camera (every frame of a move that does not darken)
            try
            {
                if (attached != null && buffer != null) attached.RemoveCommandBuffer(When, buffer);
                buffer?.Clear();
            }
            catch (Exception)
            {
                // camera already gone
            }
            attached = null;
        }

        /// <summary>The optic camera, while it draws a scope picture of this sight (not a backup sight, not a red dot).</summary>
        private static Camera OpticCameraOf(ScopeTarget target)
        {
            OpticCameraManager optics = CameraManager.Instance?.OpticCameraManager;
            if (optics == null) return null;
            OpticSight sight = optics.CurrentOpticSight;
            Camera camera = optics.Camera;
            if (sight == null || camera == null || !camera.isActiveAndEnabled) return null;
            Transform scope = ScopeRoot(target);
            return scope != null && sight.transform.IsChildOf(scope) ? camera : null;
        }

        private static Transform ScopeRoot(ScopeTarget target)
        {
            if (!ReferenceEquals(rootOf, target))
            {
                rootOf = target;
                ScopePrefabCache cache = target.Part != null ? target.Part.GetComponentInParent<ScopePrefabCache>(true) : null;
                root = cache != null ? cache.transform : null;
            }
            return root;
        }

        private static bool Ready()
        {
            if (buffer != null && darkMaterial != null && blurMaterial != null && quad != null) return true;
            Shader colored = Shader.Find("Hidden/Internal-Colored");
            Shader textured = Shader.Find("UI/Default");
            if (colored == null || textured == null) throw new InvalidOperationException("built-in shader missing");

            // Multiply: picture * colour (alpha kept), no depth test.
            darkMaterial = new Material(colored) { hideFlags = HideFlags.HideAndDontSave };
            darkMaterial.SetInt("_SrcBlend", (int)BlendMode.Zero);
            darkMaterial.SetInt("_DstBlend", (int)BlendMode.SrcColor);
            darkMaterial.SetInt("_Cull", (int)CullMode.Off);
            darkMaterial.SetInt("_ZWrite", 0);
            darkMaterial.SetInt("_ZTest", (int)CompareFunction.Always);

            // Blurred copy blended over the picture by its alpha, colour channels only.
            blurMaterial = new Material(textured) { hideFlags = HideFlags.HideAndDontSave };
            blurMaterial.SetInt("_ColorMask", 14);

            quad = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            const float z = 0.5f; // inside the depth range whichever way the platform maps it (no depth test anyway)
            quad.vertices = new[] { new Vector3(-1f, -1f, z), new Vector3(-1f, 1f, z), new Vector3(1f, 1f, z), new Vector3(1f, -1f, z) };
            quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);

            buffer = new CommandBuffer { name = "ScopeHandjob lever scope picture" };
            return true;
        }

        private static void Record(Camera camera, float dark, float blur)
        {
            buffer.Clear();
            RenderTargetIdentifier screen = BuiltinRenderTextureType.CameraTarget;
            if (blur > 0.002f)
            {
                RenderTextureFormat format = camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
                buffer.GetTemporaryRT(Half, -2, -2, 0, FilterMode.Bilinear, format);
                buffer.GetTemporaryRT(Quarter, -4, -4, 0, FilterMode.Bilinear, format);
                buffer.GetTemporaryRT(Eighth, -8, -8, 0, FilterMode.Bilinear, format);
                buffer.Blit(screen, Half);
                buffer.Blit(Half, Quarter);
                buffer.Blit(Quarter, Eighth);
                blurMaterial.color = new Color(1f, 1f, 1f, blur);
                buffer.SetGlobalFloat(GuiZTest, (float)CompareFunction.Always);
                buffer.Blit(Eighth, screen, blurMaterial);
                buffer.ReleaseTemporaryRT(Half);
                buffer.ReleaseTemporaryRT(Quarter);
                buffer.ReleaseTemporaryRT(Eighth);
            }
            if (dark > 0.002f)
            {
                float keep = 1f - dark;
                darkMaterial.color = new Color(keep, keep, keep, 1f);
                buffer.SetRenderTarget(screen);
                buffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                buffer.DrawMesh(quad, Matrix4x4.identity, darkMaterial, 0, 0);
            }
            buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        }
    }
}
