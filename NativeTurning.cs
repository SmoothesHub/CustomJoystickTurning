using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;

// GorillaSnapTurn in the target game still uses this XRI compatibility component.
#pragma warning disable CS0618

namespace CustomJoystickTurning
{
    internal static class NativeTurning
    {
        private static readonly AccessTools.FieldRef<GorillaSnapTurn, float> Pending =
            AccessTools.FieldRefAccess<GorillaSnapTurn, float>("m_CurrentTurnAmount");
        private static readonly AccessTools.FieldRef<GorillaSnapTurn, bool> AxisReset =
            AccessTools.FieldRefAccess<GorillaSnapTurn, bool>("m_AxisReset");
        private static readonly AccessTools.FieldRef<GorillaSnapTurn, float> TurnAmount =
            AccessTools.FieldRefAccess<GorillaSnapTurn, float>("m_TurnAmount");
        private static readonly Action<GorillaSnapTurn, float> StartTurn =
            (Action<GorillaSnapTurn, float>)Delegate.CreateDelegate(typeof(Action<GorillaSnapTurn, float>),
                AccessTools.Method(typeof(GorillaSnapTurn), "StartTurn"));
        private static GorillaSnapTurn provider;
        private static XRController leftController;
        private static XRController rightController;
        private static ControllerInputPoller previousPoller;
        private static InputDevice previousDevice;

        internal static void Validate()
        {
            // Preflight the real installed IL before installing any UI patches.
            TickPatch.Rewrite(PatchProcessor.GetOriginalInstructions(
                AccessTools.Method(typeof(GorillaSnapTurn), "Tick"))).ToList();
            if (AccessTools.Method(typeof(GorillaNetworking.GorillaComputer), "ProcessTurnState",
                    new[] { typeof(GorillaNetworking.GorillaKeyboardBindings) }) == null ||
                AccessTools.Method(typeof(GorillaNetworking.GorillaComputer), "TurnScreen") == null)
                throw new NotSupportedException("The game's native TURN methods have changed.");
        }

        internal static void ResetPending()
        {
            if (provider == null) return;
            Pending(provider) = 0f;
            AxisReset(provider) = true;
        }

        private static void BindControllers(GorillaSnapTurn turn)
        {
            provider = turn;
            leftController = null;
            rightController = null;
            foreach (var controller in turn.controllers)
            {
                if (controller == null) continue;
                if (controller.controllerNode == XRNode.LeftHand) leftController = controller;
                else if (controller.controllerNode == XRNode.RightHand) rightController = controller;
            }
            // One hierarchy lookup when the local turn provider changes, never per tick.
            // The native turning list can contain only the original right controller.
            foreach (var controller in turn.transform.root.GetComponentsInChildren<XRController>(true))
            {
                if (leftController == null && controller.controllerNode == XRNode.LeftHand) leftController = controller;
                else if (rightController == null && controller.controllerNode == XRNode.RightHand) rightController = controller;
            }
            Plugin.Instance.Selection.Reset();
            ResetPending();
        }

        internal static void ProcessSelectedInput(GorillaSnapTurn turn)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Ready) { Pending(turn) = 0f; return; }
            if (provider != turn) BindControllers(turn);
            var input = ControllerInputPoller.instance;
            if (input != previousPoller)
            {
                previousPoller = input;
                plugin.Selection.Reset();
                ResetPending();
            }

            bool left = plugin.Selection.Hand == TurningHand.Left;
            var controller = left ? leftController : rightController;
            InputDevice device = input == null ? default(InputDevice) :
                (left ? input.leftControllerDevice : input.rightControllerDevice);
            if (previousDevice != device)
            {
                previousDevice = device;
                plugin.Selection.Reset();
                ResetPending();
            }
            // Some player prefabs only have an XRI turning component for the original
            // hand. In that case use the native provider's input-enable gate, while
            // still reading exclusively from the selected hand's actual device.
            bool inputEnabled = controller != null ? controller.enableInputActions : HasEnabledNativeController(turn);
            bool available = input != null && !input.handTrackingActive &&
                device.isValid && inputEnabled;
            float axis = plugin.Selection.Filter(
                input == null ? 0f : input.leftControllerPrimary2DAxis.x,
                input == null ? 0f : input.rightControllerPrimary2DAxis.x,
                available, turn.deadZone);

            if (!available || plugin.Selection.AwaitingCenter) Pending(turn) = 0f;
            // Keep the game's own StartTurn: debounce, turning overrides, locomotion
            // locks, turn amount, snap/smooth/NONE and actual GTPlayer.Turn all remain native.
            if (axis > turn.deadZone) StartTurn(turn, TurnAmount(turn));
            else if (axis < -turn.deadZone) StartTurn(turn, -TurnAmount(turn));
            else AxisReset(turn) = true;
        }

        private static bool HasEnabledNativeController(GorillaSnapTurn turn)
        {
            var controllers = turn.controllers;
            if (controllers == null) return false;
            for (int i = 0; i < controllers.Count; i++)
                if (controllers[i] != null && controllers[i].enableInputActions) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(GorillaSnapTurn), "Tick")]
    internal static class TickPatch
    {
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var type = typeof(GorillaSnapTurn);
            var controllers = AccessTools.Field(type, "m_Controllers");
            var pending = AccessTools.Field(type, "m_CurrentTurnAmount");
            int begin = code.FindIndex(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, controllers)) - 1;
            int end = code.FindIndex(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, pending)) - 1;

            // This adapter targets the controller loop used by Steam build 25787313.
            // Reject an unfamiliar layout instead of showing controls that do nothing.
            if (begin != 2 || end <= begin || code[begin].opcode != OpCodes.Ldarg_0 ||
                code[end].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[1].operand, AccessTools.Method(type, "ValidateTurningOverriders")) ||
                code.Count(c => Equals(c.operand, controllers)) != 3 ||
                code.Count(c => Equals(c.operand, AccessTools.Method(type, "StartTurn"))) != 2 ||
                code.Count(c => Equals(c.operand, AccessTools.Field(typeof(ControllerInputPoller), "leftControllerPrimary2DAxis"))) != 1 ||
                code.Count(c => Equals(c.operand, AccessTools.Field(typeof(ControllerInputPoller), "rightControllerPrimary2DAxis"))) != 1 ||
                code.Any(c => c.blocks.Count != 0))
                throw new NotSupportedException("Unrecognized GorillaSnapTurn.Tick input layout.");

            // No branch from the retained code may jump into the removed loop.
            var removedLabels = new HashSet<Label>(code.Skip(begin).Take(end - begin).SelectMany(c => c.labels));
            foreach (var instruction in code.Take(begin).Concat(code.Skip(end)))
            {
                if ((instruction.operand is Label label && removedLabels.Contains(label)) ||
                    (instruction.operand is Label[] labels && labels.Any(removedLabels.Contains)))
                    throw new NotSupportedException("A branch enters the native input loop.");
            }

            var result = code.Take(begin).ToList();
            result.Add(new CodeInstruction(OpCodes.Ldarg_0));
            result.Add(new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(NativeTurning), nameof(NativeTurning.ProcessSelectedInput))));
            result.AddRange(code.Skip(end));
            return result;
        }
    }
}
