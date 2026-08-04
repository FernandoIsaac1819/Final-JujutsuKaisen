using UnityEngine;

/// Dash: windup/aim-select input, launch (straight-line and orbital), deceleration,
/// and the interrupt points other systems (Jump, RCT, Attack) hook into via ForceDeactivate-
/// style cleanup.
public partial class PlayerControllerSplit
{
    // ---------------- Dash ----------------

    private void OnDashPressed()
    {
        bool fromLocomotion = action == ActionState.Locomotion && dashLockoutTimer <= 0f;
        bool fromLandingInterrupt = action == ActionState.Landing;
        bool fromRCTInterrupt = action == ActionState.UsingRCT;
        bool fromAttackInterrupt = action == ActionState.Attacking && combo != null && combo.InPunishWindow;
        bool stateOk = fromLocomotion || fromLandingInterrupt || fromRCTInterrupt || fromAttackInterrupt;

        if (stateOk && dashCooldownTimer <= 0f)
        {
            if (fromRCTInterrupt) rct?.ForceDeactivate();
            if (fromAttackInterrupt)
            {
                combo?.ForceEndCombo();
            }
            EnterDashWindup();
            return;
        }

        // Blocked only by cooldown (state itself was valid) — remember it, fires once cooldown clears.
        if (stateOk)
            dashBufferTimer = dashBufferWindow;
    }

    private void EnterDashWindup()
    {
        action = ActionState.DashWindup;
        dashAimTimer = dashAimWindow;
        velocity = Vector3.zero; // frozen during the aim window
    }

    private void TickDashWindup()
    {
        dashAimTimer -= Time.deltaTime;

        if (input.MoveInput.sqrMagnitude > 0.0001f)
        {
            LaunchDash(input.MoveInput);
        }
        else if (dashAimTimer <= 0f)
        {
            // No direction given by the time the window expired — dash simply doesn't happen.
            EnterLocomotion();
        }
    }

    private void LaunchDash(Vector2 aimInput)
    {
        if (aimInput.sqrMagnitude <= 0.0001f) return; // shouldn't happen given TickDashWindup, but guard anyway

        int snappedX, snappedZ;
        Vector3 dashDir;
        bool nearTarget = IsInCombatRange();

        // Resync before anything reads orbitDirection — state may be stale from before a
        // jump/landing that interrupted straight into this dash.
        if (nearTarget && target != null) SyncOrbitState();

        dashDir = GetOrbitAwareFourDirection(aimInput, out snappedX, out snappedZ);

        // Within combat range, only lateral (Left/Right) dashes are allowed at all —
        // forward/back simply don't trigger a dash, rather than falling back to a
        // straight-line one.
        if (nearTarget && snappedX == 0)
        {
            EnterLocomotion();
            return;
        }

        animator.SetFloat(DirXHash, snappedX);
        animator.SetFloat(DirZHash, snappedZ);

        action = ActionState.Dashing;
        dashLaunchMultiplier = DashSpeedMultiplier;

        // Within combat range, a pure lateral dash becomes a true orbit sweep around the
        // target — angle-tracked and decelerating angular speed, going through the same
        // verified-correct orbitDirection rotation Locomotion uses. Forward/Back (and
        // anything outside combat range) stay a straight-line dash, unchanged.
        dashIsOrbital = nearTarget && snappedX != 0 && target != null;

        if (dashIsOrbital)
        {
            // Sign determined by checking which way this direction actually rotates
            // orbitDirection — reuses the same convention OrbitalDisplacement already uses
            // and Locomotion already verified correct, instead of guessing independently.
            Vector3 tangentDir = Vector3.Cross(Vector3.up, orbitDirection);
            float sign = Mathf.Sign(Vector3.Dot(dashDir, tangentDir));
            float radius = Mathf.Max(orbitRadius, 0.01f);

            dashOrbitAngleRemaining = sideDodgeOrbitAngle * sign;

            if (dashAngleSolveFromDuration)
            {
                // Solved so the sweep finishes in exactly the straight dash's own duration,
                // at exactly sideDodgeOrbitAngle degrees — guaranteed. Sweep speed is fixed
                // by this math rather than dashSpeed's natural feel.
                float baseDuration = dashDeceleration > 0f ? dashSpeed / dashDeceleration : 0.01f;
                float duration = Mathf.Max(0.01f, baseDuration / Mathf.Max(dashLaunchMultiplier, 0.01f));
                dashOrbitAngularSpeed = (2f * sideDodgeOrbitAngle / duration) * sign;
                dashOrbitAngularDecel = Mathf.Abs(dashOrbitAngularSpeed) / duration;
                dashLaunchAngularSpeed = Mathf.Abs(dashOrbitAngularSpeed);
            }
            else
            {
                // Hard cap: same feel as the straight dash (speed/decel derived from
                // dashSpeed/dashDeceleration/radius), but stops early if the angle is
                // reached before natural deceleration would finish.
                dashOrbitAngularSpeed = (dashSpeed * dashLaunchMultiplier / radius) * Mathf.Rad2Deg * sign;
                dashOrbitAngularDecel = 0f; // recomputed live from radius each frame in this mode
                dashLaunchAngularSpeed = Mathf.Abs(dashOrbitAngularSpeed);
            }
        }
        else
        {
            velocity = dashDir * dashSpeed * dashLaunchMultiplier;
            dashLaunchSpeed = velocity.magnitude;
        }

        animator.SetBool(DashHash, true);
        if (!string.IsNullOrEmpty(dashStateName))
            animator.CrossFadeInFixedTime(dashStateName, dashCrossfadeDuration);
    }

    private void TickDash()
    {
        FaceTarget(); // capped turn speed already prevents mismatch — no need to freeze this anymore

        if (dashIsOrbital)
        {
            float angularDecel;
            if (dashAngleSolveFromDuration)
            {
                angularDecel = dashOrbitAngularDecel; // fixed at launch
            }
            else
            {
                float radius = Mathf.Max(orbitRadius, 0.01f);
                angularDecel = (dashDeceleration * dashLaunchMultiplier / radius) * Mathf.Rad2Deg;
            }

            dashOrbitAngularSpeed = Mathf.MoveTowards(dashOrbitAngularSpeed, 0f, angularDecel * Time.deltaTime);

            float step = dashOrbitAngularSpeed * Time.deltaTime;
            if (Mathf.Abs(step) > Mathf.Abs(dashOrbitAngleRemaining))
                step = dashOrbitAngleRemaining; // don't overshoot past the configured angle, in either mode

            orbitDirection = (Quaternion.AngleAxis(step, Vector3.up) * orbitDirection).normalized;
            dashOrbitAngleRemaining -= step;

            Vector3 desiredPosition = target.position + orbitDirection * orbitRadius;
            Vector3 correction = desiredPosition - transform.position;
            correction.y = 0f;
            controller.Move(correction + Vector3.up * groundedStickForce * Time.deltaTime);
            bool doneAngle = Mathf.Abs(dashOrbitAngleRemaining) <= 0.05f;
            bool doneSpeed = Mathf.Abs(dashOrbitAngularSpeed) <= 0.5f;
            if (doneAngle || doneSpeed)
            {
                EndDash();
            }
        }
        else
        {
            float effectiveDeceleration = dashDeceleration * dashLaunchMultiplier;
            velocity = Vector3.MoveTowards(velocity, Vector3.zero, effectiveDeceleration * Time.deltaTime);
            Vector3 horizontal = ClampApproachToMinDistance(velocity * Time.deltaTime);
            controller.Move(horizontal + Vector3.up * groundedStickForce * Time.deltaTime);
            if (velocity.sqrMagnitude <= 0.0001f)
            {
                EndDash();
            }
        }
    }

    private void EndDash()
    {
        velocity = Vector3.zero;
        dashIsOrbital = false;
        EnterLocomotion();
        animator.SetBool(DashHash, false);
        dashCooldownTimer = dashCooldown;
        jumpLockoutTimer = jumpLockoutAfterDash;
    }

}
