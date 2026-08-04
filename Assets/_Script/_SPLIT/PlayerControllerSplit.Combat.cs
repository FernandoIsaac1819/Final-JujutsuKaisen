using UnityEngine;

/// Reverse Cursed Technique reaction (movement lock only — RCT's own logic lives in
/// ReverseCursedTechnique.cs) and Attacking (combo lock, forward steps, hit/hitstun
/// reactions). Kept together since RCT's section here is small and both are combat-adjacent.
public partial class PlayerControllerSplit
{
    // ---------------- Reverse Cursed Technique ----------------

    /// Reacts to ReverseCursedTechnique's own activation — this is the only thing
    /// PlayerControllerSplit does for RCT: lock movement while it's active, unlock when it isn't.
    private void OnRCTActiveChanged(bool active)
    {
        if (active)
        {
            if (action != ActionState.Locomotion) return; // shouldn't happen — CanUseRCT already gated this
            action = ActionState.UsingRCT;
        }
        else if (action == ActionState.UsingRCT)
        {
            EnterLocomotion();
        }
    }

    private void TickRCT()
    {
        Vector2 mv = input.MoveInput;
        bool hasInput = GetLocomotionMovement(mv, out Vector3 continuousDir, out Vector3 rightAxis, out float rawAngleDeg, out float speedBlend01, out float targetSpeed);

        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, mv.magnitude);

        if (hasInput)
        {
            currentSpeed = Mathf.SmoothDamp(currentSpeed, targetSpeed, ref speedVelocityRef, walkRunTransitionTime);

            Vector3 moveDir = GetSmoothedMoveDir(continuousDir, rightAxis);
            velocity = moveDir * currentSpeed;

            if (currentAnimCategory == LocomotionAnimCategory.Forward) RotateTowardsMovementDirection(continuousDir);
            else if (currentAnimCategory == LocomotionAnimCategory.Backward) RotateTowardsMovementDirection(-continuousDir);
            else FaceTargetSmoothed();
        }
        else
        {
            currentSpeed = 0f;
            speedVelocityRef = 0f;
            velocity = Vector3.zero;
            FaceTargetSmoothed(); // idle — smoothed to match Strafe (avoids a snap when flipping between them)
        }

        Move(velocity);

        animator.SetFloat(LocomotionSpeedHash, speedBlend01, dirBlendTime, Time.deltaTime);
    }

    // ---------------- Combat ----------------

    /// Reacts to ComboController's own activation — this is the only thing PlayerControllerSplit
    /// does for attacking: lock movement while a combo is active, unlock when it isn't.
    private void OnComboActiveChanged(bool active)
    {
        if (active)
        {
            bool fromDashInterrupt = action == ActionState.Dashing;
            if (action != ActionState.Locomotion && !fromDashInterrupt) return; // shouldn't happen — CanAttack already gated this

            if (fromDashInterrupt)
            {
                // Same cleanup EndDash() does, minus re-entering Locomotion — we're going
                // straight into Attacking instead.
                velocity = Vector3.zero;
                dashIsOrbital = false;
                animator.SetBool(DashHash, false);
                dashCooldownTimer = dashCooldown;
                jumpLockoutTimer = jumpLockoutAfterDash;
            }

            action = ActionState.Attacking;
        }
        else if (action == ActionState.Attacking)
        {
            EnterLocomotion();
        }
    }

    private Vector3 attackStepDirection;
    private float attackStepRemaining;
    private float attackStepSpeed;

    /// Called by ComboController when a move starts. Direction captured once (same
    /// reasoning as Dash/Rush — recomputing it every frame lets it drift).
    public void BeginAttackStep(float distance, float duration)
    {
        attackStepRemaining = 0f;
        if (target == null || duration <= 0f || distance <= 0f) return;

        Vector3 dir = target.position - transform.position;
        dir.y = 0f;
        attackStepDirection = dir.sqrMagnitude > 0.0001f ? dir.normalized : transform.forward;

        attackStepRemaining = distance;
        attackStepSpeed = distance / duration;
    }

    private void TickAttacking()
    {
        FaceTarget();

        if (attackStepRemaining > 0f)
        {
            float step = Mathf.Min(attackStepSpeed * Time.deltaTime, attackStepRemaining);
            Vector3 horizontal = ClampApproachToMinDistance(attackStepDirection * step);
            controller.Move(horizontal + Vector3.up * groundedStickForce * Time.deltaTime);
            attackStepRemaining -= step;
        }

        // Keeps the locomotion blend continuously matched to whatever's currently held,
        // even though the Locomotion animator state isn't active right now — purely
        // invisible bookkeeping (DirX/DirZ aren't read by the attack's own animation state
        // at all), but it means EnterLocomotion's resync has nothing stale left to catch up
        // on the moment the attack ends, regardless of whether input changed mid-attack.
        bool attackHasInput = GetLocomotionMovement(input.MoveInput, out _, out _, out float attackRawAngle, out _, out _);
        UpdateLocomotionAnimCategory(attackHasInput, attackRawAngle, input.MoveInput.magnitude);
    }

    /// Called by an attacker's ComboController when a hit lands on this character. Not
    /// gated behind IsVulnerableToSidestep or anything else yet — a real defensive layer
    /// (Guard/Sidestep/Parry interaction with incoming hits) would filter calls into this
    /// rather than this method filtering itself.
    public void ApplyHitReaction(float hitstunDuration, Vector3 knockbackVelocity)
    {
        rct?.ForceDeactivate();
        action = ActionState.Hitstun;
        hitstunTimer = hitstunDuration;
        velocity = knockbackVelocity;
    }

    private void TickHitstun()
    {
        velocity = Vector3.MoveTowards(velocity, Vector3.zero, knockbackDecay * Time.deltaTime);
        controller.Move((velocity + Vector3.up * groundedStickForce) * Time.deltaTime);

        hitstunTimer -= Time.deltaTime;
        if (hitstunTimer <= 0f)
        {
            EnterLocomotion();
        }
    }
}
