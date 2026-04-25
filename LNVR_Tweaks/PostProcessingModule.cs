using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LNVR_Tweaks
{
    // Owns the post-processing and SSAO tweaks.
    //
    // Strategy:
    //   * SSAO: it's a ScriptableRendererFeature, lives on the URP renderer asset. We add (or
    //     enable) one in OnInitializeMelon, before the XR plugin loader runs, so the renderer
    //     is built fresh with SSAO included. Toggling it later would force a renderer rebuild
    //     that's risky on Meta XR Simulator (same swapchain class of bug as renderScale).
    //   * Bloom / Vignette / ChromaticAberration / FilmGrain: standard URP volume overrides.
    //     We spawn our own DontDestroyOnLoad global Volume with priority 9999 so it wins over
    //     scene volumes. Per-frame parameter changes are safe — they go through URP's volume
    //     stack interpolation, no swapchain involvement.
    public class PostProcessingModule
    {
        private MelonPreferences_Category _cat;

        // SSAO
        private MelonPreferences_Entry<bool> _ssaoEnabled;
        private MelonPreferences_Entry<float> _ssaoIntensity;
        private MelonPreferences_Entry<float> _ssaoRadius;
        private MelonPreferences_Entry<float> _ssaoFalloff;
        private MelonPreferences_Entry<float> _ssaoDirectLightingStrength;
        private MelonPreferences_Entry<int> _ssaoSampleCount;
        private MelonPreferences_Entry<int> _ssaoNormalQuality;
        private MelonPreferences_Entry<bool> _ssaoDownsample;
        private MelonPreferences_Entry<bool> _ssaoAfterOpaque;

        // Bloom override
        private MelonPreferences_Entry<bool> _bloomOverride;
        private MelonPreferences_Entry<float> _bloomIntensity;
        private MelonPreferences_Entry<float> _bloomThreshold;
        private MelonPreferences_Entry<float> _bloomScatter;
        private MelonPreferences_Entry<bool> _bloomHighQuality;

        // Vignette override
        private MelonPreferences_Entry<bool> _vignetteOverride;
        private MelonPreferences_Entry<float> _vignetteIntensity;

        // Chromatic aberration override
        private MelonPreferences_Entry<bool> _caOverride;
        private MelonPreferences_Entry<float> _caIntensity;

        // Film grain override
        private MelonPreferences_Entry<bool> _grainOverride;
        private MelonPreferences_Entry<float> _grainIntensity;

        // Live state.
        private ScreenSpaceAmbientOcclusion _ssaoFeature;
        private bool _ssaoFeatureEnabled;
        private VolumeProfile _profile;
        private Volume _volume;
        private Bloom _bloom;
        private Vignette _vignette;
        private ChromaticAberration _ca;
        private FilmGrain _grain;

        public void Initialize(MelonPreferences_Category cat)
        {
            _cat = cat;

            // SSAO
            _ssaoEnabled = _cat.CreateEntry("SSAOEnabled", true,
                description: "Enable Screen-Space Ambient Occlusion (URP renderer feature). Adds shadow contact-darkening in creases. Game ships without SSAO.");
            _ssaoIntensity = _cat.CreateEntry("SSAOIntensity", 1.0f,
                description: "SSAO intensity. 0.0–4.0. URP default is ~1.0.");
            _ssaoRadius = _cat.CreateEntry("SSAORadius", 0.35f,
                description: "SSAO sample radius (world-space metres). 0.05–1.0. Lower = tighter, more localised AO.");
            _ssaoFalloff = _cat.CreateEntry("SSAOFalloff", 100.0f,
                description: "Distance at which SSAO fades out. URP default 100.");
            _ssaoDirectLightingStrength = _cat.CreateEntry("SSAODirectLightingStrength", 0.25f,
                description: "How much SSAO darkens directly-lit pixels. 0–1. Lower = AO only affects ambient.");
            _ssaoSampleCount = _cat.CreateEntry("SSAOSampleCount", 12,
                description: "SSAO samples per pixel. 4–32. Higher = smoother / less dithered, more GPU.");
            _ssaoNormalQuality = _cat.CreateEntry("SSAONormalQuality", 2,
                description: "SSAO normal sampling quality. 0=Low, 1=Medium, 2=High.");
            _ssaoDownsample = _cat.CreateEntry("SSAODownsample", false,
                description: "Render SSAO at half resolution. true=cheaper but blurrier; false=full-res.");
            _ssaoAfterOpaque = _cat.CreateEntry("SSAOAfterOpaque", false,
                description: "Apply SSAO after opaque rendering instead of inlined. Game-engine-y trick — usually false.");

            // Bloom
            _bloomOverride = _cat.CreateEntry("BloomOverride", false,
                description: "Override the game's Bloom intensity / threshold / scatter via a global volume. false = use the scene's existing bloom values.");
            _bloomIntensity = _cat.CreateEntry("BloomIntensity", 0.6f,
                description: "Bloom intensity when override is on. 0–10.");
            _bloomThreshold = _cat.CreateEntry("BloomThreshold", 0.9f,
                description: "Bloom luminance threshold (gamma space). Higher = only the brightest highlights bloom.");
            _bloomScatter = _cat.CreateEntry("BloomScatter", 0.7f,
                description: "Bloom scatter / softness. 0–1.");
            _bloomHighQuality = _cat.CreateEntry("BloomHighQuality", true,
                description: "Bicubic upsampling on bloom passes. Slightly more expensive, smoother result.");

            // Vignette
            _vignetteOverride = _cat.CreateEntry("VignetteOverride", false,
                description: "Override scene Vignette intensity via a global volume. false = leave the scene's vignette alone.");
            _vignetteIntensity = _cat.CreateEntry("VignetteIntensity", 0.0f,
                description: "Vignette intensity when override is on. 0 = off, 1 = full corners-black.");

            // Chromatic aberration
            _caOverride = _cat.CreateEntry("ChromaticAberrationOverride", true,
                description: "Override scene Chromatic Aberration via a global volume. CA in VR causes coloured fringing on edges; default override forces it OFF.");
            _caIntensity = _cat.CreateEntry("ChromaticAberrationIntensity", 0.0f,
                description: "Chromatic aberration intensity when override is on. 0 = off, 1 = max.");

            // Film grain
            _grainOverride = _cat.CreateEntry("FilmGrainOverride", true,
                description: "Override scene Film Grain via a global volume. Grain in VR reduces clarity of fine detail; default override forces it OFF.");
            _grainIntensity = _cat.CreateEntry("FilmGrainIntensity", 0.0f,
                description: "Film grain intensity when override is on. 0 = off, 1 = max.");

            MelonLogger.Msg($"PP module initialized — ssao={_ssaoEnabled.Value}, bloomOverride={_bloomOverride.Value}, caOverride={_caOverride.Value}, grainOverride={_grainOverride.Value}.");
        }

        // Run during OnInitializeMelon, before the XR plugin loader creates the swapchain.
        // Adds the SSAO renderer feature to the URP renderer asset so the rendering graph is
        // built with it from the first frame.
        public void ApplyPreXRInit()
        {
            if (!_ssaoEnabled.Value) return;

            try
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                if (pipeline == null) return;
                UniversalRenderPipelineAsset urp;
                try { urp = pipeline.Cast<UniversalRenderPipelineAsset>(); }
                catch { return; }
                if (urp == null) return;

                var rendererData = urp.scriptableRendererData;
                if (rendererData == null)
                {
                    MelonLogger.Warning("URP scriptableRendererData null — can't add SSAO.");
                    return;
                }

                // Look for an existing SSAO feature first.
                ScreenSpaceAmbientOcclusion existing = null;
                foreach (var f in rendererData.rendererFeatures)
                {
                    if (f == null) continue;
                    var ssao = f.TryCast<ScreenSpaceAmbientOcclusion>();
                    if (ssao != null) { existing = ssao; break; }
                }

                if (existing != null)
                {
                    _ssaoFeature = existing;
                    _ssaoFeature.SetActive(true);
                    MelonLogger.Msg("SSAO renderer feature found on URP renderer; enabling.");
                }
                else
                {
                    _ssaoFeature = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                    _ssaoFeature.name = "LNVR_SSAO";
                    _ssaoFeature.hideFlags = HideFlags.DontSave;
                    rendererData.rendererFeatures.Add(_ssaoFeature);
                    rendererData.SetDirty();
                    MelonLogger.Msg("SSAO renderer feature added to URP renderer.");
                }

                ConfigureSSAO();
                _ssaoFeatureEnabled = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"SSAO setup failed: {ex.Message}");
            }
        }

        private void ConfigureSSAO()
        {
            if (_ssaoFeature == null) return;
            try
            {
                // Use m_Settings (regular get/set on the Il2Cpp wrapper) rather than the
                // ref-returning `settings` property — the latter throws "Object was garbage
                // collected in IL2CPP domain" because the wrapper for the ref return gets GC'd
                // before we read it.
                var s = _ssaoFeature.m_Settings;
                if (s == null)
                {
                    s = new ScreenSpaceAmbientOcclusionSettings();
                    _ssaoFeature.m_Settings = s;
                }
                s.Intensity = Mathf.Clamp(_ssaoIntensity.Value, 0f, 4f);
                s.Radius = Mathf.Clamp(_ssaoRadius.Value, 0.05f, 1.0f);
                s.Falloff = Mathf.Clamp(_ssaoFalloff.Value, 1f, 1000f);
                s.DirectLightingStrength = Mathf.Clamp01(_ssaoDirectLightingStrength.Value);
                s.SampleCount = Mathf.Clamp(_ssaoSampleCount.Value, 4, 32);
                s.NormalSamples = (ScreenSpaceAmbientOcclusionSettings.NormalQuality)Mathf.Clamp(_ssaoNormalQuality.Value, 0, 2);
                s.Downsample = _ssaoDownsample.Value;
                s.AfterOpaque = _ssaoAfterOpaque.Value;
                _ssaoFeature.Create();
                MelonLogger.Msg($"SSAO configured — intensity={s.Intensity}, radius={s.Radius}, falloff={s.Falloff}, samples={s.SampleCount}, normals={s.NormalSamples}, downsample={s.Downsample}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"SSAO configure failed: {ex.Message}");
            }
        }

        public void Tick()
        {
            EnsureVolume();
            ApplyBloomOverride();
            ApplyVignetteOverride();
            ApplyCAOverride();
            ApplyGrainOverride();
            ApplySSAOToggle();
        }

        private void EnsureVolume()
        {
            if (_volume != null) return;
            try
            {
                _profile = ScriptableObject.CreateInstance<VolumeProfile>();
                _profile.name = "LNVR_Tweaks_Profile";
                _profile.hideFlags = HideFlags.DontSave;

                _bloom = _profile.Add<Bloom>(false);
                _vignette = _profile.Add<Vignette>(false);
                _ca = _profile.Add<ChromaticAberration>(false);
                _grain = _profile.Add<FilmGrain>(false);

                var go = new GameObject("LNVR_Tweaks_Volume");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _volume = go.AddComponent<Volume>();
                _volume.isGlobal = true;
                _volume.priority = 9999f;
                _volume.weight = 1f;
                _volume.profile = _profile;
                MelonLogger.Msg("PP volume spawned (DontDestroyOnLoad, priority=9999).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"PP volume setup failed: {ex.Message}");
                _volume = null;
                _profile = null;
            }
        }

        private void ApplyBloomOverride()
        {
            if (_bloom == null) return;
            try
            {
                if (!_bloomOverride.Value)
                {
                    _bloom.active = false;
                    return;
                }
                _bloom.active = true;
                _bloom.intensity.overrideState = true;
                _bloom.intensity.value = Mathf.Max(0f, _bloomIntensity.Value);
                _bloom.threshold.overrideState = true;
                _bloom.threshold.value = Mathf.Max(0f, _bloomThreshold.Value);
                _bloom.scatter.overrideState = true;
                _bloom.scatter.value = Mathf.Clamp01(_bloomScatter.Value);
                _bloom.highQualityFiltering.overrideState = true;
                _bloom.highQualityFiltering.value = _bloomHighQuality.Value;
            }
            catch (Exception ex) { MelonLogger.Warning($"Bloom apply failed: {ex.Message}"); }
        }

        private void ApplyVignetteOverride()
        {
            if (_vignette == null) return;
            try
            {
                if (!_vignetteOverride.Value)
                {
                    _vignette.active = false;
                    return;
                }
                _vignette.active = true;
                _vignette.intensity.overrideState = true;
                _vignette.intensity.value = Mathf.Clamp01(_vignetteIntensity.Value);
            }
            catch (Exception ex) { MelonLogger.Warning($"Vignette apply failed: {ex.Message}"); }
        }

        private void ApplyCAOverride()
        {
            if (_ca == null) return;
            try
            {
                if (!_caOverride.Value)
                {
                    _ca.active = false;
                    return;
                }
                _ca.active = true;
                _ca.intensity.overrideState = true;
                _ca.intensity.value = Mathf.Clamp01(_caIntensity.Value);
            }
            catch (Exception ex) { MelonLogger.Warning($"CA apply failed: {ex.Message}"); }
        }

        private void ApplyGrainOverride()
        {
            if (_grain == null) return;
            try
            {
                if (!_grainOverride.Value)
                {
                    _grain.active = false;
                    return;
                }
                _grain.active = true;
                _grain.intensity.overrideState = true;
                _grain.intensity.value = Mathf.Clamp01(_grainIntensity.Value);
            }
            catch (Exception ex) { MelonLogger.Warning($"FilmGrain apply failed: {ex.Message}"); }
        }

        // SSAO toggle — only toggles SetActive on a feature we added pre-XR. We do NOT add or
        // remove it from the renderer features list at runtime; that would force a renderer
        // rebuild and potentially blackscreen.
        private void ApplySSAOToggle()
        {
            if (_ssaoFeature == null) return;
            try
            {
                var want = _ssaoEnabled.Value;
                if (_ssaoFeatureEnabled != want)
                {
                    _ssaoFeature.SetActive(want);
                    _ssaoFeatureEnabled = want;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"SSAO toggle failed: {ex.Message}"); }
        }
    }
}
