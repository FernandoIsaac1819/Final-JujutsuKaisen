using UnityEngine;

/// Jump: physics (derived from height/timeToApex/timeToFall), launch, air control,
/// landing recovery. Also owns EnterLocomotion — used by every other system to return to
/// Locomotion, but organically ended up here; it's a genuinely shared method, not Jump-specific.
public partial class PlayerControllerSplit
{
    // ---------------- Jump ----------------

    private void RecalculateJumpPhysics()
    {
        float effectiveHeight = jumpHeight * JumpMultiplier;
        gravityUp = -(2f * effectiveHeight) / (timeToApex * timeToApex);
        gravityDown = -(2f * effectiveHeight) / (timeToFall * timeToFall);
        jumpVelocity = -gravityUp * timeToApex;
    }

    private void OnJumpPressed()
    {
        bool fromLocomotion = action == ActionState.Locomotion && jumpLockoutTimer <= 0f;
        bool fromDashInterrupt = action == ActionState.Dashing || action == ActionState.DashWindup;
        bool fromRCTInterrupt = action == ActionState.UsingRCT;
        bool grounded = airborneTimer <= coyoteTime; // coyote time — still counts as grounded briefly after leaving a ledge

        if ((fromLocomotion || fromDashInterrupt || fromRCTInterrupt) && grounded && jumpCooldownTimer <= 0f && !IsInCombatRange())
        {
            if (fromRCTInterrupt) rct?.ForceDeactivate();
            DoJump();
            return;
        }

        // Airborne and couldn't jump — remember the press, fires automatically the instant we land.
        if (action == ActionState.Jumping)
            jumpBufferTimer = jumpBufferWindow;
    }

    private void DoJump()
    {
        RecalculateJumpPhysics(); // pick up any runtime inspector tweaks before this jump
        verticalVelocity = jumpVelocity;

        Vector3 dir = GetOrbitAwareFourDirection(input.MoveInput, out int snappedX, out int snappedZ);
        velocity = dir.normalized * airHorizontalSpeed * AirSpeedMultiplier;
        animator.SetFloat(DirXHash, snappedX);
        animator.SetFloat(DirZHash, snappedZ);

        animator.SetBool(DashHash, false);
        dashIsOrbital = false; // defensive — jump interrupting an orbital dash shouldn't be reachable, but cheap to guard against
        action = ActionState.Jumping;
        animator.SetTrigger(JumpHash);
    }

    private void TickJump()
    {
        FaceTarget(); // same reasoning as Dash — capped turn speed prevents mismatch, so no need to freeze this

        if (airControl > 0f)
        {
            Vector3 dir = GetOrbitAwareFourDirection(input.MoveInput, out _, out _);
            Vector3 targetAirVelocity = dir.normalized * airHorizontalSpeed * AirSpeedMultiplier;
            velocity = Vector3.MoveTowards(velocity, targetAirVelocity, airControl * Time.deltaTime);
        }

        verticalVelocity += (verticalVelocity > 0f ? gravityUp : gravityDown) * Time.deltaTime;
        Vector3 move = velocity + Vector3.up * verticalVelocity;
        controller.Move(move * Time.deltaTime);

        animator.SetFloat(VerticalVelocityHash, verticalVelocity);

        if (controller.isGrounded && verticalVelocity <= 0f)
        {
            verticalVelocity = 0f;
            jumpCooldownTimer = jumpCooldown;

            if (jumpBufferTimer > 0f)
            {
                jumpBufferTimer = 0f;
                DoJump(); // buffered press — skip Landing entirely, jump immediately
                return;
            }

            action = ActionState.Landing;
            landingTimer = landingRecoveryDuration;
        }
    }

    private void EnterLocomotion()
    {
        action = ActionState.Locomotion;
        if (!string.IsNullOrEmpty(locomotionStateName))
            animator.CrossFadeInFixedTime(locomotionStateName, locomotionCrossfadeDuration);
        // Synchronous, not deferred — a prior attempt to defer this to the end of Update()
        // caused a worse bug: action flips to Locomotion immediately (synchronous event),
        // so THIS frame's switch statement already routes to TickLocomotion before a
        // deferred sync ever got a chance to run, leaving it to use stale orbit data for
        // one frame and produce a visible snap. Syncing here immediately avoids that. The
        // original concern (reading position mid-forward-step) isn't a real risk in
        // practice — the step always finishes many frames before the clip does.
        SyncOrbitState();

        // Resync to whatever DirX/DirZ actually are right now — Dash (and Jump) set them
        // directly and instantly, completely bypassing this tracking variable, which
        // otherwise stays frozen at whatever it was before the interrupting state began.
        // Starting the smoothing from that stale value instead of the true current pose is
        // what caused the pop: small/invisible if the stale value happened to be close to
        // the dash direction, large and visible if they'd diverged (e.g. was idle, then
        // dashed a totally different direction).
        locomotionBlendInput = new Vector2(animator.GetFloat(DirXHash), animator.GetFloat(DirZHash));

        // Same fix, same reason, for rotation: smoothedRotationDir is frozen during Dash
        // (nothing calls RotateTowardsMovementDirection/FaceTargetSmoothed while dashing),
        // so it's still holding whatever direction it was tracking before the dash even
        // started. Resync it to the character's actual current facing so the post-dash
        // blend starts from reality instead of a stale pre-dash value.
        smoothedRotationDir = transform.forward;
        smoothedRotationDirVelocity = Vector3.zero;

        bool hasInput = GetLocomotionMovement(input.MoveInput, out _, out _, out float rawAngleDeg, out float speedBlend01, out _);
        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, input.MoveInput.magnitude);
        animator.SetFloat(LocomotionSpeedHash, speedBlend01, dirBlendTime, Time.deltaTime);
    }

    private void TickLanding()
    {
        // Movement input cuts the recovery short instantly — no animation event needed.
        if (input.MoveInput.sqrMagnitude > 0.01f)
        {
            EnterLocomotion();
            dashLockoutTimer = dashLockoutAfterLanding;
            return;
        }

        landingTimer -= Time.deltaTime;
        if (landingTimer <= 0f)
        {
            EnterLocomotion();
            dashLockoutTimer = dashLockoutAfterLanding;
        }
    }

}
