using UnityEngine;

/// Guard: static hold with damage reduction, timed-block/parry reaction window, and its
/// own direction resolver (GetOrbitAwareDirection) — used only here, unlike
/// GetLocomotionMovement (Locomotion/RCT) which needs sprint/category logic Guard doesn't.
public partial class PlayerControllerSplit
{
    // ---------------- Guard ----------------

    [Tooltip("Damage reduction while actively guarding. Higher = blocks more damage. " +
             "0 = no extra reduction, 1 = fully blocks.")]
    [Range(0f, 1f)]
    [SerializeField] private float guardDamageReduction = 0.5f;
    [Tooltip("Fixed movement speed while guarding. Separate from moveSpeed and NOT affected " +
             "by Reinforcement — only guard's damage reduction changes with reinforcement tier.")]
    [SerializeField] private float guardWalkSpeed = 2f;

    public bool IsGuarding => action == ActionState.Guarding;
    public float GuardDamageReduction => guardDamageReduction;
    private float lastGuardPressTime = -999f;

    public enum GuardReactionResult { None, TimedBlock, Parry }

    /// Evaluates whether a FRESH Guard press (not one held from well before the window)
    /// falls within reactionWindowDuration of right now — meant to be called at the exact
    /// moment a hit lands. Must still be actively guarding when this is called; a press
    /// followed by an early release doesn't count. Returns the outcome plus a 0-1 quality
    /// (1 = at parryThreshold or better, 0 = right at the edge of the window) for a caller
    /// that wants to blend the block strength based on timing precision.
    public GuardReactionResult EvaluateGuardTiming(float reactionWindowDuration, float parryThreshold, out float quality)
    {
        quality = 0f;
        if (!IsGuarding) return GuardReactionResult.None;

        float timeSincePress = Time.time - lastGuardPressTime;
        if (timeSincePress > reactionWindowDuration) return GuardReactionResult.None; // press was too long ago — not a timed read

        if (timeSincePress <= parryThreshold)
        {
            quality = 1f;
            return GuardReactionResult.Parry;
        }

        float range = reactionWindowDuration - parryThreshold;
        quality = range > 0f ? 1f - Mathf.Clamp01((timeSincePress - parryThreshold) / range) : 0f;
        return GuardReactionResult.TimedBlock;
    }

    private void OnGuardPressed()
    {
        bool fromRCTInterrupt = action == ActionState.UsingRCT;
        if (action != ActionState.Locomotion && !fromRCTInterrupt) return;

        if (fromRCTInterrupt) rct?.ForceDeactivate();

        action = ActionState.Guarding;
        velocity = Vector3.zero;
        currentSpeed = 0f;
        speedVelocityRef = 0f;
        lastGuardPressTime = Time.time;
        animator.SetBool(GuardHash, true);
    }

    private void OnGuardReleased()
    {
        if (action != ActionState.Guarding) return;
        EnterLocomotion();
        animator.SetBool(GuardHash, false);
    }

    /// Computes movement direction using orbitDirection's own basis when available, falling
    /// back to actual transform rotation otherwise. Shared by any state that needs its
    /// movement to stay consistent with the orbital math (Locomotion, Guard, ...).
    private Vector3 GetOrbitAwareDirection(Vector2 mv, out int snappedX, out int snappedZ)
    {
        if (target != null && orbitInitialized)
        {
            Vector3 orbitForward = -orbitDirection;
            Vector3 orbitRight = Vector3.Cross(Vector3.up, orbitForward);
            return SnapToEightDirections(mv, orbitForward, orbitRight, out snappedX, out snappedZ);
        }

        return SnapToEightDirections(mv, out snappedX, out snappedZ);
    }

    private void TickGuard()
    {
        FaceTarget(); // still tracks the opponent, same as every other state

        Vector2 mv = input.MoveInput;
        bool hasInput = mv.sqrMagnitude > 0.0001f;
        Vector3 dir = GetOrbitAwareDirection(mv, out int snappedX, out int snappedZ);

        // Fixed speed, deliberately NOT scaled by MoveSpeedMultiplier — reinforcement only
        // affects guard's damage reduction, never its movement speed.
        if (hasInput)
        {
            currentSpeed = Mathf.SmoothDamp(currentSpeed, guardWalkSpeed, ref speedVelocityRef, moveAccelTime);
            velocity = dir * currentSpeed;
        }
        else
        {
            currentSpeed = 0f;
            speedVelocityRef = 0f;
            velocity = Vector3.zero;
        }

        Move(velocity);

        // Same shared DirX/DirZ params Locomotion uses — Guard's blend tree reads them too.
        animator.SetFloat(DirXHash, snappedX, dirBlendTime, Time.deltaTime);
        animator.SetFloat(DirZHash, snappedZ, dirBlendTime, Time.deltaTime);
    }
}
