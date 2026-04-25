using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace LNVR_Tweaks
{
    // Owns all graphics-quality tweaks. Reads from the LNVR_Tweaks.Graphics MelonPreferences
    // category and applies the values to URP, QualitySettings, and XR.
    //
    // Why we re-apply on a timer:
    //   - URP's UniversalRenderPipelineAsset is a ScriptableObject in the asset bundle. The
    //     game can swap pipelines on chapter load, and a fresh asset arrives with stock values
    //     each time. We re-apply continuously (cheap — early-out when values match).
    //   - XRSettings.eyeTextureResolutionScale is a runtime-only property; it persists, but
    //     reapplying is harmless and guards against the game stomping it during XR session reset.
    //   - QualitySettings.{anisotropic,softParticles,shadowResolution,...} are global; the game
    //     might reset them on quality-level switch.
    public class GraphicsModule
    {
        private MelonPreferences_Category _cat;

        private MelonPreferences_Entry<bool> _enabled;
        private MelonPreferences_Entry<float> _renderScale;
        private MelonPreferences_Entry<float> _eyeTextureScale;
        private MelonPreferences_Entry<int> _msaa;
        private MelonPreferences_Entry<float> _xrRenderScale;
        private MelonPreferences_Entry<int> _xrMsaaLevel;
        private MelonPreferences_Entry<float> _shadowDistance;
        private MelonPreferences_Entry<int> _mainShadowResolution;
        private MelonPreferences_Entry<int> _additionalShadowResolution;
        private MelonPreferences_Entry<int> _shadowCascades;
        private MelonPreferences_Entry<bool> _softShadows;
        private MelonPreferences_Entry<bool> _hdr;
        private MelonPreferences_Entry<bool> _anisotropicForce;
        private MelonPreferences_Entry<bool> _softParticles;
        private MelonPreferences_Entry<bool> _realtimeReflectionProbes;
        private MelonPreferences_Entry<float> _lodBias;

        // Last-applied values — used to detect drift caused by URP asset swaps mid-session
        // and to log when we actually push a change (instead of every tick).
        private float _lastRenderScale = -1f;
        private float _lastEyeTexScale = -1f;
        private int _lastMsaa = -1;
        private float _lastShadowDistance = -1f;
        private int _lastMainShadowRes = -1;
        private float _lastXrRenderScale = -1f;
        private int _lastXrMsaaLevel = -1;
        private XRDisplaySubsystem _cachedDisplay;

        public void Initialize()
        {
            _cat = MelonPreferences.CreateCategory("LNVR_Tweaks_Graphics");
            _cat.SetFilePath("UserData/LNVR_Tweaks.cfg");

            _enabled = _cat.CreateEntry("Enabled", true,
                description: "Master switch for the graphics tweaks. Set to false to leave the game's stock graphics settings alone. NOTE: with VR-safe defaults (RenderScale=1.0, MSAA=1) we don't touch the URP asset — only XR supersampling, shadows, anisotropic, soft particles, LOD bias.");

            // Supersampling. EyeTextureScale is the VR-correct way (it scales the XR swapchain
            // before URP renders). RenderScale on the URP asset works in non-VR but in VR forces
            // a framebuffer rebuild that the XR pipeline doesn't always tolerate — leave it at 1.0.
            _renderScale = _cat.CreateEntry("RenderScale", 1.0f,
                description: "URP render scale. KEEP AT 1.0 IN VR — changing the URP asset's renderScale mid-session can cause a black screen because the XR swapchain doesn't rebuild to match. Use EyeTextureScale instead for VR supersampling.");
            _eyeTextureScale = _cat.CreateEntry("EyeTextureScale", 1.0f,
                description: "XR eye-texture supersampling (XRSettings.eyeTextureResolutionScale). 1.0 = native HMD resolution. Some Meta XR Simulator builds black-screen when this is changed mid-session, so the mod's default keeps it at 1.0. Set to 1.2–1.5 to enable supersampling at your own risk; if the view goes black, restore to 1.0 in the cfg and restart.");

            // MSAA on the URP asset rebuilds the multisample render target — also unsafe to flip
            // mid-session in VR. Leave off by default; user can opt in.
            _msaa = _cat.CreateEntry("MSAA", 1,
                description: "MSAA sample count: 1 = off, 2/4/8 = enable. WARNING: flipping this on the live URP asset in VR has caused black screens in testing. Safe path is to leave it 1 and rely on EyeTextureScale for AA. If you want MSAA, set the value here, restart the game, and only let the mod apply it once at startup (it will).");

            // SAFE runtime XR scaling — Unity's XRDisplaySubsystem routes these through the XR
            // runtime so the swapchain stays consistent. This is the path that doesn't black-screen.
            _xrRenderScale = _cat.CreateEntry("XRRenderScale", 1.0f,
                description: "Safe runtime VR supersampling. Sets XRDisplaySubsystem.scaleOfAllRenderTargets. 1.0 = native HMD resolution; 1.3 ≈ 1.7× pixels; 1.5 ≈ 2.25×; 2.0 ≈ 4×. Unlike the URP/XRSettings paths, this one coordinates with the XR runtime so it's safe to change live. Recommended starting point if you have GPU headroom.");
            _xrMsaaLevel = _cat.CreateEntry("XRMSAALevel", 1,
                description: "Safe runtime MSAA via XRDisplaySubsystem.SetMSAALevel. 1 = off, 2 = 2×, 4 = 4×, 8 = 8×. Goes through the XR runtime's swapchain creation path, so it's safe to change live (unlike the URP MSAA setting).");

            // Shadows
            _shadowDistance = _cat.CreateEntry("ShadowDistance", 60f,
                description: "Distance (meters) from the camera at which shadows still draw. Game default is 25m. 60-100m looks much better in open areas.");
            _mainShadowResolution = _cat.CreateEntry("MainShadowResolution", 4096,
                description: "Main directional light shadowmap resolution. 1024/2048/4096. Game default is 1024. 4096 is the URP cap.");
            _additionalShadowResolution = _cat.CreateEntry("AdditionalShadowResolution", 4096,
                description: "Additional (point/spot) light shadowmap resolution. Game default is 4096 already.");
            _shadowCascades = _cat.CreateEntry("ShadowCascades", 4,
                description: "Number of shadow cascades for the main light: 1, 2, or 4. More = better quality at a perf cost. Game default is 4.");
            _softShadows = _cat.CreateEntry("SoftShadows", true,
                description: "Enable soft shadow filtering on URP. Game default is true.");

            _hdr = _cat.CreateEntry("HDR", true,
                description: "Use HDR rendering. Required for proper bloom/tonemap; game ships true.");

            _anisotropicForce = _cat.CreateEntry("ForceAnisotropic", true,
                description: "Force anisotropic filtering (sharper textures at angle). Sets QualitySettings.anisotropicFiltering = ForceEnable.");
            _softParticles = _cat.CreateEntry("SoftParticles", true,
                description: "Soft particles fade where they intersect geometry instead of clipping. Game default is false.");
            _realtimeReflectionProbes = _cat.CreateEntry("RealtimeReflectionProbes", true,
                description: "Enable realtime reflection probe updates. Game default is true.");
            _lodBias = _cat.CreateEntry("LODBias", 2.0f,
                description: "Global LOD bias — distance multiplier before the engine swaps to a lower-detail mesh. Higher = full-detail meshes stay further away. Default 2.0.");

            MelonLogger.Msg($"Graphics module initialized — enabled={_enabled.Value}, renderScale={_renderScale.Value}, msaa={_msaa.Value}, shadowDist={_shadowDistance.Value}m, mainShadowRes={_mainShadowResolution.Value}.");

            // CRITICAL: write URP.renderScale and msaaSampleCount NOW, while we're still in the
            // OnInitializeMelon window — before the XR plugin loader starts and creates the
            // swapchain. The swapchain reads these values once during creation. Mutating them
            // mid-session breaks the Meta XR Simulator (and likely other runtimes too) because
            // their swapchain doesn't gracefully resize. So this is one-shot, pre-XR-init only.
            ApplyPreXRInit();
        }

        private void ApplyPreXRInit()
        {
            if (!_enabled.Value) return;

            try
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline == null) return;
                UniversalRenderPipelineAsset urp;
                try { urp = pipeline.Cast<UniversalRenderPipelineAsset>(); }
                catch { return; }
                if (urp == null) return;

                var rs = Mathf.Clamp(_renderScale.Value, 0.5f, 2.0f);
                if (Mathf.Abs(urp.renderScale - rs) > 0.001f)
                {
                    urp.renderScale = rs;
                    MelonLogger.Msg($"URP.renderScale = {rs:F2} (pre-XR-init, baked into swapchain on creation)");
                    _lastRenderScale = rs;
                }
                else
                {
                    _lastRenderScale = urp.renderScale;
                }

                var msaa = ClampMsaa(_msaa.Value);
                if (urp.msaaSampleCount != msaa)
                {
                    urp.msaaSampleCount = msaa;
                    MelonLogger.Msg($"URP.msaaSampleCount = {msaa} (pre-XR-init, baked into swapchain on creation)");
                    _lastMsaa = msaa;
                }
                else
                {
                    _lastMsaa = urp.msaaSampleCount;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Pre-XR-init URP apply failed: {ex.Message}");
            }
        }

        public void Tick()
        {
            if (!_enabled.Value) return;

            try
            {
                // NOTE: Do NOT call ApplyXRDisplaySubsystem() or ApplyXR() during runtime.
                // Both APIs (XRDisplaySubsystem.scaleOfAllRenderTargets and
                // XRSettings.eyeTextureResolutionScale) cause black screens on the Meta XR
                // Simulator when changed after the XR session is already running. The
                // RenderScale/MSAA values are applied once in OnInitializeMelon via
                // ApplyPreXRInit() which runs before the XR session is created.
                ApplyURP();
                ApplyQualitySettings();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Graphics tick failed: {ex.Message}");
            }
        }

        // The XR-runtime-aware path. Unlike URP.renderScale or XRSettings.eyeTextureResolutionScale,
        // these go through the XR display subsystem which coordinates with the swapchain.
        private void ApplyXRDisplaySubsystem()
        {
            var disp = GetDisplaySubsystem();
            if (disp == null) return;

            var scale = Mathf.Clamp(_xrRenderScale.Value, 0.5f, 2.5f);
            if (Mathf.Abs(disp.scaleOfAllRenderTargets - scale) > 0.001f)
            {
                disp.scaleOfAllRenderTargets = scale;
                if (Mathf.Abs(_lastXrRenderScale - scale) > 0.001f)
                {
                    MelonLogger.Msg($"XRDisplaySubsystem.scaleOfAllRenderTargets = {scale:F2}");
                    _lastXrRenderScale = scale;
                }
            }

            var msaa = ClampMsaa(_xrMsaaLevel.Value);
            if (_lastXrMsaaLevel != msaa)
            {
                disp.SetMSAALevel(msaa);
                MelonLogger.Msg($"XRDisplaySubsystem.SetMSAALevel({msaa})");
                _lastXrMsaaLevel = msaa;
            }
        }

        private XRDisplaySubsystem GetDisplaySubsystem()
        {
            if (_cachedDisplay != null) return _cachedDisplay;
            try
            {
                var settings = XRGeneralSettings.Instance;
                if (settings == null) return null;
                var manager = settings.Manager;
                if (manager == null || manager.activeLoader == null) return null;
                _cachedDisplay = manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"XR display subsystem lookup failed: {ex.Message}");
            }
            return _cachedDisplay;
        }

        private void ApplyXR()
        {
            var desired = Mathf.Clamp(_eyeTextureScale.Value, 0.5f, 2.5f);
            // SAFETY: leave the XR scale alone if user wants stock (1.0). Mutating it on a live
            // XR session in some runtimes (notably Meta XR Simulator) can drop the swapchain
            // and leave a black mirror window. Only apply once at startup if user explicitly
            // wants a non-1.0 value.
            if (Mathf.Abs(desired - 1.0f) < 0.001f)
            {
                _lastEyeTexScale = 1.0f;
                return;
            }
            if (_lastEyeTexScale > 0f) return; // one-shot
            if (Mathf.Abs(XRSettings.eyeTextureResolutionScale - desired) > 0.001f)
            {
                XRSettings.eyeTextureResolutionScale = desired;
                MelonLogger.Msg($"XRSettings.eyeTextureResolutionScale = {desired:F2} (one-shot; revert to 1.0 in cfg if VR mirror goes black)");
                _lastEyeTexScale = desired;
            }
        }

        private void ApplyURP()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return;
            UniversalRenderPipelineAsset urp;
            try { urp = pipeline.Cast<UniversalRenderPipelineAsset>(); }
            catch { return; }
            if (urp == null) return;

            // RenderScale and msaaSampleCount are intentionally NOT applied here — they are
            // baked into the URP asset by ApplyPreXRInit() during OnInitializeMelon, before
            // the XR swapchain is created. Mutating them at runtime causes a black-screen.

            var sd = Mathf.Clamp(_shadowDistance.Value, 5f, 500f);
            if (Mathf.Abs(urp.shadowDistance - sd) > 0.01f)
            {
                urp.shadowDistance = sd;
                if (Mathf.Abs(_lastShadowDistance - sd) > 0.01f)
                {
                    MelonLogger.Msg($"URP.shadowDistance = {sd:F0}m");
                    _lastShadowDistance = sd;
                }
            }

            var mainRes = ClampShadowRes(_mainShadowResolution.Value);
            if (urp.mainLightShadowmapResolution != mainRes)
            {
                urp.mainLightShadowmapResolution = mainRes;
                if (_lastMainShadowRes != mainRes)
                {
                    MelonLogger.Msg($"URP.mainLightShadowmapResolution = {mainRes}");
                    _lastMainShadowRes = mainRes;
                }
            }

            var addRes = ClampShadowRes(_additionalShadowResolution.Value);
            if (urp.additionalLightsShadowmapResolution != addRes)
            {
                urp.additionalLightsShadowmapResolution = addRes;
            }

            var cascades = _shadowCascades.Value;
            if (cascades != 1 && cascades != 2 && cascades != 4) cascades = 4;
            if (urp.shadowCascadeCount != cascades)
            {
                urp.shadowCascadeCount = cascades;
            }

            if (urp.supportsSoftShadows != _softShadows.Value)
            {
                urp.supportsSoftShadows = _softShadows.Value;
            }
            if (urp.supportsHDR != _hdr.Value)
            {
                urp.supportsHDR = _hdr.Value;
            }
        }

        private void ApplyQualitySettings()
        {
            var desiredAniso = _anisotropicForce.Value ? AnisotropicFiltering.ForceEnable : AnisotropicFiltering.Enable;
            if (QualitySettings.anisotropicFiltering != desiredAniso)
            {
                QualitySettings.anisotropicFiltering = desiredAniso;
            }

            if (QualitySettings.softParticles != _softParticles.Value)
            {
                QualitySettings.softParticles = _softParticles.Value;
            }

            if (QualitySettings.realtimeReflectionProbes != _realtimeReflectionProbes.Value)
            {
                QualitySettings.realtimeReflectionProbes = _realtimeReflectionProbes.Value;
            }

            var lodBias = Mathf.Clamp(_lodBias.Value, 0.5f, 10f);
            if (Mathf.Abs(QualitySettings.lodBias - lodBias) > 0.01f)
            {
                QualitySettings.lodBias = lodBias;
            }
        }

        private static int ClampMsaa(int v)
        {
            if (v <= 1) return 1;
            if (v <= 2) return 2;
            if (v <= 4) return 4;
            return 8;
        }

        private static int ClampShadowRes(int v)
        {
            if (v <= 1024) return 1024;
            if (v <= 2048) return 2048;
            return 4096;
        }
    }
}
