using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using GorillaNetworking;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

namespace CustomJoystickTurning
{
    [BepInPlugin(Id, "Custom Joystick Turning", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.smoothes.gorillatag.customjoystickturning";
        public const string Version = "1.0.0";
        internal static Plugin Instance;
        internal Selection Selection;
        internal bool Ready;
        private ConfigEntry<TurningHand> hand;
        private Harmony harmony;

        private void Awake()
        {
            Instance = this;
            try
            {
                Config.SaveOnConfigSet = false;
                // Same section/key as the older plugin; Right/Left migrate directly.
                hand = Config.Bind("Turning", "Joystick", TurningHand.Right,
                    "Joystick used for native turning. Change with R/L on the TURN tab.");
                if (!Enum.IsDefined(typeof(TurningHand), hand.Value)) hand.Value = TurningHand.Right;
                Selection = new Selection(hand.Value);
                hand.SettingChanged += OnSettingChanged;
                Config.Save();
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not initialize the joystick preference: " + ex);
                enabled = false;
            }
        }

        private void OnEnable()
        {
            if (Selection == null) return;
            try
            {
                NativeTurning.Validate();
                harmony = new Harmony(Id);
                harmony.PatchAll(typeof(Plugin).Assembly);
                Selection.Reset();
                Ready = true;
                Logger.LogInfo("Native TURN tab R/L controls ready. Selected: " + Selection.Hand);
                RefreshScreen();
            }
            catch (Exception ex)
            {
                Ready = false;
                harmony?.UnpatchSelf();
                Logger.LogError("Joystick controls were not enabled; original game controls remain. " + ex);
            }
        }

        internal void Select(TurningHand selected)
        {
            if (!Ready || !Selection.Select(selected)) return;
            NativeTurning.ResetPending();
            try
            {
                hand.Value = selected;
                Config.Save();
            }
            catch (Exception ex)
            {
                Logger.LogError("Joystick changed for this session, but saving failed: " + ex.Message);
            }
        }

        private void OnSettingChanged(object sender, EventArgs e)
        {
            var selected = Enum.IsDefined(typeof(TurningHand), hand.Value) ? hand.Value : TurningHand.Right;
            if (Selection.Select(selected)) NativeTurning.ResetPending();
            // TURN's normal PressButton path redraws in the same call. Config reloads
            // are applied here without polling or touching any other computer tab.
        }

        private static void RefreshScreen()
        {
            var computer = GorillaComputer.instance;
            if (computer != null && computer.currentState == GorillaComputer.ComputerState.Turn)
                computer.UpdateScreen();
        }

        private void OnDisable()
        {
            Ready = false;
            NativeTurning.ResetPending();
            harmony?.UnpatchSelf();
            RefreshScreen();
        }

        private void OnDestroy()
        {
            if (hand != null) hand.SettingChanged -= OnSettingChanged;
            if (Instance == this) Instance = null;
        }
    }

    internal static class ComputerAccess
    {
        private static readonly AccessTools.FieldRef<GorillaText, bool> Failed =
            AccessTools.FieldRefAccess<GorillaText, bool>("failedState");

        internal static bool IsAcceptingTurnInput(GorillaComputer computer)
        {
            return Plugin.Instance != null && Plugin.Instance.Ready && computer.initialized &&
                computer.currentState == GorillaComputer.ComputerState.Turn &&
                computer.screenText != null && !Failed(computer.screenText) &&
                NetworkSystem.Instance != null && !NetworkSystem.Instance.WrongVersion;
        }
    }

    [HarmonyPatch(typeof(GorillaComputer), "ProcessTurnState")]
    internal static class TurnKeyboardPatch
    {
        // This runs only after the game has dispatched a keyboard event to TURN.
        // Numbers, OPTION keys, and every other tab keep their original handlers.
        [HarmonyPostfix]
        private static void Postfix(GorillaComputer __instance, GorillaKeyboardBindings __0)
        {
            if (!ComputerAccess.IsAcceptingTurnInput(__instance)) return;
            if (__0 == GorillaKeyboardBindings.R) Plugin.Instance.Select(TurningHand.Right);
            else if (__0 == GorillaKeyboardBindings.L) Plugin.Instance.Select(TurningHand.Left);
        }
    }

    [HarmonyPatch(typeof(GorillaComputer), "TurnScreen")]
    internal static class TurnScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GorillaComputer __instance)
        {
            if (!ComputerAccess.IsAcceptingTurnInput(__instance)) return;
            string selected = Plugin.Instance.Selection.Hand == TurningHand.Left ? "LEFT" : "RIGHT";
            // Append to the game's freshly constructed text buffer, never currentText:
            // currentText is only committed later by GorillaTextManager.
            __instance.screenText.Append("\n\nJOYSTICK TURNING\n" +
                "PRESS R - RIGHT JOYSTICK TURNING\n" +
                "PRESS L - LEFT JOYSTICK TURNING\n" +
                "CURRENT: " + selected + " JOYSTICK");
        }
    }
}
