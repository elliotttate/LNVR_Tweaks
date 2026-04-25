using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

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

        public void Initialize()
        {
            _cat = MelonPreferences.CreateCategory("LNVR_Tweaks_Graphics");
            _cat.SetFilePath("UserData/LNVR_Tweaks.cfg");

            _enabled = _cat.CreateEntry("Enabled", true,
                description: "Master switch for the graphics tweaks. Set to false to leave the game's stock graphics settings alone.");

            // The two big visual quality knobs.
            _renderScale = _cat.CreateEntry("RenderScale", 1.5f,
                description: "URP render scale. 1.0 = native, 1.5 = ~2.25x pixels (sharp), 2.0 = 4x pixels (very sharp / heavy). Applied to UniversalRenderPipelineAsset.renderScale.");
            _eyeTextureScale = _cat.CreateEntry("EyeTextureScale", 1.0f,
                description: "XR eye texture supersampling factor (XRSettings.eyeTextureResolutionScale). Multiplies on top of RenderScale. 1.0 = default, raise carefully — both this AND RenderScale stack.");

            // MSAA. URP also exposes msaaSampleCount = 1/2/4/8.
            _msaa = _cat.CreateEntry("MSAA", 4,
                description: "MSAA sample count: 1 = off, 2 = 2x, 4 = 4x, 8 = 8x. URP MSAA. Cheaper than supersampling for jagged-edge cleanup.");

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
        }

        public void Tick()
        {
            if (!_enabled.Value) return;

            try
            {
                ApplyXR();
                ApplyURP();
                ApplyQualitySettings();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Graphics tick failed: {ex.Message}");
            }
        }

        private void ApplyXR()
        {
            var desired = Mathf.Clamp(_eyeTextureScale.Value, 0.5f, 2.5f);
            if (Mathf.Abs(XRSettings.eyeTextureResolutionScale - desired) > 0.001f)
            {
                XRSettings.eyeTextureResolutionScale = desired;
                if (Mathf.Abs(_lastEyeTexScale - desired) > 0.001f)
                {
                    MelonLogger.Msg($"XRSettings.eyeTextureResolutionScale = {desired:F2}");
                    _lastEyeTexScale = desired;
                }
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

            var rs = Mathf.Clamp(_renderScale.Value, 0.5f, 2.0f);
            if (Mathf.Abs(urp.renderScale - rs) > 0.001f)
            {
                urp.renderScale = rs;
                if (Mathf.Abs(_lastRenderScale - rs) > 0.001f)
                {
                    MelonLogger.Msg($"URP.renderScale = {rs:F2}");
                    _lastRenderScale = rs;
                }
            }

            var msaa = ClampMsaa(_msaa.Value);
            if (urp.msaaSampleCount != msaa)
            {
                urp.msaaSampleCount = msaa;
                if (_lastMsaa != msaa)
                {
                    MelonLogger.Msg($"URP.msaaSampleCount = {msaa}");
                    _lastMsaa = msaa;
                }
            }

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
