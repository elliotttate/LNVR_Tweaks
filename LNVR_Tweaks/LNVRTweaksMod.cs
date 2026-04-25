using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using Il2CppBNG;
using Il2CppFramework.VR.WithDependencies.SOArchitecture.Save;
using Il2CppLone.Menu;
using Il2CppTMPro;

namespace LNVR_Tweaks
{
    public class LNVRTweaksMod : MelonMod
    {
        private const string HoodMeshPath =
            "AddressableSceneContentLoader/Bootstrap_Addressable (Clone)/Player/PlayerController/CameraRig/TrackingSpace/CenterEyeAnchor/Camera/HoodMesh";
        private const string RotationSnapSettingPath =
            "AddressableSceneContentLoader/Bootstrap_Addressable (Clone)/Managers/GameSettings/ContentBasedOnSave/Controls_RotationSnap";
        private const string HoodSizeSettingPath =
            "AddressableSceneContentLoader/Bootstrap_Addressable (Clone)/Managers/GameSettings/ContentBasedOnSave/Accessibility_HoodSize";

        // The ButtonSelector that drives the "Hood occlusions" UI entry in the game's Accessibility menu.
        private const string HoodSizeSelectorPath =
            "SceneContentLoader/Menu (Clone)/=====MENU=====/MenuUI/MenuCanvas/Settings_Accessibility/Content/ScrollView/Viewport/Content/HoodSize/HoodSizeSelector";
        private const string HoodSizeTextPath = HoodSizeSelectorPath + "/Text (TMP)";

        // The user's native choice — 0 (was "Reduced", we relabel to "None") means hide. 1 ("Standard") means show.
        // Anything else in save: treat as "show".
        private const int HiddenChoice = 0;

        private MelonPreferences_Category _cat;
        private MelonPreferences_Entry<bool> _prefSmoothTurn;
        private MelonPreferences_Entry<float> _prefSmoothTurnSpeed;
        private MelonPreferences_Entry<float> _prefSnapTurnAmount;
        private MelonPreferences_Entry<bool> _prefHideHoodDefault;

        private readonly GraphicsModule _graphics = new GraphicsModule();

        private bool _rotationNativeSavePushed;
        private bool _hoodDefaultApplied;
        private float _periodicTimer;

        // Reflection cache so we don't pay the lookup every tick.
        private FieldInfo _currentChoiceField;

        // What the game's save currently has for HoodSize (tracked via the menu selector when it's mounted).
        // null = unknown; poll mode falls back to MelonPref default.
        private int? _lastObservedHoodChoice;

        public override void OnInitializeMelon()
        {
            _cat = MelonPreferences.CreateCategory("LNVR_Tweaks");
            _cat.SetFilePath("UserData/LNVR_Tweaks.cfg");

            _prefSmoothTurn = _cat.CreateEntry("SmoothTurn", true,
                description: "Use smooth turning instead of snap turning. Pushed into the game's Controls.RotationSnap save on startup; changing it in the game's Options menu takes precedence until next launch.");
            _prefSmoothTurnSpeed = _cat.CreateEntry("SmoothTurnSpeed", 90f,
                description: "Smooth turn speed in degrees per second.");
            _prefSnapTurnAmount = _cat.CreateEntry("SnapTurnAmount", 45f,
                description: "Degrees per snap turn (when smooth turn is off).");
            _prefHideHoodDefault = _cat.CreateEntry("HideHoodByDefault", true,
                description: "If true and the user hasn't otherwise chosen in the Accessibility menu yet, hide the hood. Once the user picks 'None' or 'Standard' in-menu, that choice takes over.");

            _currentChoiceField = typeof(ButtonSelector).GetField("_currentChoice", BindingFlags.NonPublic | BindingFlags.Instance);

            _graphics.Initialize();

            MelonLogger.Msg("LNVR Tweaks loaded. The game's Accessibility menu's hood occlusion 'Reduced' choice is relabeled to 'None' and now fully hides the hood.");
        }

        public override void OnUpdate()
        {
            _periodicTimer -= Time.unscaledDeltaTime;
            if (_periodicTimer > 0f) return;
            _periodicTimer = 0.2f;

            PushRotationToNativeSave();
            RelabelAndSyncHoodSelector();
            EnforceLivePlayerState();
            _graphics.Tick();
        }

        // One-shot: write smooth-turn preference into the native Controls.RotationSnap save,
        // so the game's Controls menu displays the right toggle state.
        private void PushRotationToNativeSave()
        {
            if (_rotationNativeSavePushed) return;
            var go = GameObject.Find(RotationSnapSettingPath);
            if (go == null) return;
            try
            {
                var setter = go.GetComponent<SaveSetParam>();
                if (setter != null)
                {
                    setter.SetBool(!_prefSmoothTurn.Value);
                    setter.Save();
                    MelonLogger.Msg($"Controls.RotationSnap pushed to save: {!_prefSmoothTurn.Value} ({(_prefSmoothTurn.Value ? "Smooth" : "Snap")}).");
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"Rotation save push failed: {ex.Message}"); }
            _rotationNativeSavePushed = true;
        }

        // Every tick while the Accessibility menu is mounted:
        //   - Read the ButtonSelector's current choice (mirror it into _lastObservedHoodChoice).
        //   - Override the displayed TMP text so choice 0 reads "None" instead of "Reduced".
        // Also: if we've never seen the selector yet (user hasn't opened the menu), push our
        // default once so hood hiding works before the user ever touches the menu.
        private void RelabelAndSyncHoodSelector()
        {
            // Default push: one-shot, only if user hasn't interacted yet.
            if (!_hoodDefaultApplied)
            {
                var hoodSetter = GameObject.Find(HoodSizeSettingPath);
                if (hoodSetter != null)
                {
                    try
                    {
                        var setter = hoodSetter.GetComponent<SaveSetParam>();
                        if (setter != null)
                        {
                            var initialChoice = _prefHideHoodDefault.Value ? HiddenChoice : 1;
                            setter.SetInt(initialChoice);
                            setter.Save();
                            _lastObservedHoodChoice = initialChoice;
                            MelonLogger.Msg($"Accessibility.HoodSize initial default pushed to save: {initialChoice}.");
                        }
                    }
                    catch (Exception ex) { MelonLogger.Warning($"Hood default push failed: {ex.Message}"); }
                    _hoodDefaultApplied = true;
                }
            }

            // Menu sync + text relabel (only when the menu is actually mounted).
            var selectorGo = GameObject.Find(HoodSizeSelectorPath);
            if (selectorGo == null) return;

            try
            {
                var bs = selectorGo.GetComponent<ButtonSelector>();
                if (bs != null && _currentChoiceField != null)
                {
                    var choice = (int)_currentChoiceField.GetValue(bs);
                    _lastObservedHoodChoice = choice;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"Hood choice read failed: {ex.Message}"); }

            try
            {
                if (_lastObservedHoodChoice == HiddenChoice)
                {
                    var textGo = GameObject.Find(HoodSizeTextPath);
                    if (textGo != null)
                    {
                        var tmp = textGo.GetComponent<TMP_Text>();
                        if (tmp != null && tmp.text != "None")
                        {
                            tmp.text = "None";
                        }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"Hood text relabel failed: {ex.Message}"); }
        }

        // Enforce rotation mechanic + hood visibility directly on the player every tick.
        // The native save-change → player-state chain isn't always reliable (timing, chapter
        // reload, respawn), so belt-and-suspenders: also enforce here.
        private void EnforceLivePlayerState()
        {
            try
            {
                var pr = UnityEngine.Object.FindObjectOfType<PlayerRotation>();
                if (pr != null)
                {
                    var desiredType = _prefSmoothTurn.Value ? RotationMechanic.Smooth : RotationMechanic.Snap;
                    if (pr.RotationType != desiredType) pr.RotationType = desiredType;
                    if (Mathf.Abs(pr.SmoothTurnSpeed - _prefSmoothTurnSpeed.Value) > 0.01f)
                        pr.SmoothTurnSpeed = _prefSmoothTurnSpeed.Value;
                    if (Mathf.Abs(pr.SnapRotationAmount - _prefSnapTurnAmount.Value) > 0.01f)
                        pr.SnapRotationAmount = _prefSnapTurnAmount.Value;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"Rotation enforce failed: {ex.Message}"); }

            try
            {
                var hood = GameObject.Find(HoodMeshPath);
                if (hood != null)
                {
                    // Hide only if the user's current choice in the menu is "None" (index 0).
                    // If they've picked "Standard" (index 1), leave the hood alone.
                    // If we've never observed (user hasn't opened the menu), use the MelonPref default.
                    var shouldHide = _lastObservedHoodChoice.HasValue
                        ? _lastObservedHoodChoice.Value == HiddenChoice
                        : _prefHideHoodDefault.Value;
                    var shouldBeActive = !shouldHide;
                    if (hood.activeSelf != shouldBeActive)
                    {
                        hood.SetActive(shouldBeActive);
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"Hood enforce failed: {ex.Message}"); }
        }
    }
}
