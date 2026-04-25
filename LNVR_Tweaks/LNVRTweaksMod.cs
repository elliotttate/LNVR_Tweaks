using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using Il2CppBNG;
using Il2CppLone.Menu;
using Il2CppTMPro;

namespace LNVR_Tweaks
{
    public class LNVRTweaksMod : MelonMod
    {
        private const string HoodMeshPath =
            "AddressableSceneContentLoader/Bootstrap_Addressable (Clone)/Player/PlayerController/CameraRig/TrackingSpace/CenterEyeAnchor/Camera/HoodMesh";

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
                description: "Use smooth turning instead of snap turning. Enforced at runtime without writing to the game's save.");
            _prefSmoothTurnSpeed = _cat.CreateEntry("SmoothTurnSpeed", 90f,
                description: "Smooth turn speed in degrees per second.");
            _prefSnapTurnAmount = _cat.CreateEntry("SnapTurnAmount", 45f,
                description: "Degrees per snap turn (when smooth turn is off).");
            _prefHideHoodDefault = _cat.CreateEntry("HideHoodByDefault", true,
                description: "If true and the user hasn't otherwise chosen in the Accessibility menu yet, hide the hood. Once the user picks 'None' or 'Standard' in-menu, that choice takes over.");

            _currentChoiceField = typeof(ButtonSelector).GetField("_currentChoice", BindingFlags.NonPublic | BindingFlags.Instance);

            _graphics.Initialize();

            MelonLogger.Msg("LNVR Tweaks loaded. Runtime tweaks are applied without writing to the game's save.");
        }

        public override void OnUpdate()
        {
            _periodicTimer -= Time.unscaledDeltaTime;
            if (_periodicTimer > 0f) return;
            _periodicTimer = 0.2f;

            RelabelAndSyncHoodSelector();
            EnforceLivePlayerState();
            _graphics.Tick();
        }

        // Every tick while the Accessibility menu is mounted:
        //   - Read the ButtonSelector's current choice (mirror it into _lastObservedHoodChoice).
        //   - Override the displayed TMP text so choice 0 reads "None" instead of "Reduced".
        // The mod does not call the game's SaveSetParam/SaveManager path; the configured default
        // is applied directly to the live HoodMesh until the menu exposes a user choice.
        private void RelabelAndSyncHoodSelector()
        {
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
        // This intentionally avoids the game's save pipeline; invoking SaveSetParam.Save during
        // startup can overwrite profile progress before the menu has finished loading profiles.
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
