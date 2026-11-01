using System;

namespace CustomJoystickTurning
{
    public enum TurningHand { Right, Left }

    // No Unity dependencies: the switch/reconnect latch is testable independently.
    internal sealed class Selection
    {
        internal TurningHand Hand { get; private set; }
        internal bool AwaitingCenter { get; private set; } = true;

        internal Selection(TurningHand hand) { Hand = hand; }

        internal bool Select(TurningHand hand)
        {
            if (Hand == hand) return false;
            Hand = hand;
            AwaitingCenter = true;
            return true;
        }

        internal void Reset() { AwaitingCenter = true; }

        internal float Filter(float left, float right, bool available, float deadZone)
        {
            float axis = Hand == TurningHand.Left ? left : right;
            if (!available || float.IsNaN(axis) || float.IsInfinity(axis))
            {
                Reset();
                return 0f;
            }

            if (AwaitingCenter)
            {
                // A small horizontal neutral band, capped by the game's dead zone.
                // Vertical stick use does not cause yaw or prevent recentering.
                if (Math.Abs(axis) <= Math.Min(0.2f, Math.Max(0f, deadZone)))
                    AwaitingCenter = false;
                return 0f;
            }
            return Math.Max(-1f, Math.Min(1f, axis));
        }
    }
}
