using UnityEngine;

/// Locomotion: walking/running/sprint buildup, category-based rotation and blend-tree
/// selection (Forward/Backward/Strafe/Idle), and the shared movement-direction resolver
/// GetLocomotionMovement (also used directly by RCT, in PlayerControllerSplit.Combat.cs).
public partial class PlayerControllerSplit
{
    // ---------------- Locomotion ----------------

    private void TickLocomotion()
    {
        Vector2 mv = input.MoveInput;
        bool hasInput = GetLocomotionMovement(mv, out Vector3 continuousDir, out Vector3 rightAxis, out float rawAngleDeg, out float speedBlend01, out float targetSpeed);

        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, mv.magnitude); // determine category before deciding rotate direction

        if (hasInput)
        {
            currentSpeed = Mathf.SmoothDamp(currentSpeed, targetSpeed, ref speedVelocityRef, walkRunTransitionTime);

            // Strafe moves exactly sideways, matching its dedicated clip; Forward/Backward
            // keep the full continuous direction. Blended only briefly across a category
            // change (see GetSmoothedMoveDir) — smooths the transition jerk without
            // reintroducing the earlier drift.
            Vector3 moveDir = GetSmoothedMoveDir(continuousDir, rightAxis);
            velocity = moveDir * currentSpeed;

            // Forward faces the travel direction directly. Backward faces the OPPOSITE of
            // it — pure straight-back travel is 180° from normal forward, so its opposite
            // is 0°, meaning no rotation at all (stays facing the target, matching a true
            // backpedal). A backward-diagonal's opposite is offset by exactly the diagonal
            // amount from that same baseline, which is the "twist by the diagonal angle"
            // effect. Strafe keeps its own dedicated clip and just faces the target.
            if (currentAnimCategory == LocomotionAnimCategory.Forward) RotateTowardsMovementDirection(continuousDir);
            else if (currentAnimCategory == LocomotionAnimCategory.Backward) RotateTowardsMovementDirection(-continuousDir);
            else FaceTargetSmoothed();
        }
        else
        {
            // Instant stop on release — SmoothDamp toward zero would otherwise keep the
            // character visibly sliding for ~moveAccelTime after Idle pose already shows.
            currentSpeed = 0f;
            speedVelocityRef = 0f;
            velocity = Vector3.zero;
            FaceTargetSmoothed(); // idle — resume facing the target, smoothed to match Strafe (avoids a snap when flipping between them)
        }

        Move(velocity);

        animator.SetFloat(LocomotionSpeedHash, speedBlend01, dirBlendTime, Time.deltaTime);
    }

    private void FaceTarget()
    {
        if (target == null) return;
        Vector3 dir = target.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized);
    }

    /// Same direction as FaceTarget, but smoothed at movementFacingTurnSpeed instead of
    /// snapping instantly — used specifically for Strafe, where the angle-to-target changes
    /// quickly during orbiting (especially at close range) and instant rotation reading
    /// that every frame is what was making it feel snappy. FaceTarget itself stays instant
    /// everywhere else (Guard, Dash, Attacking, Idle) — this doesn't touch those.
    private void FaceTargetSmoothed()
    {
        if (target == null) return;
        Vector3 dir = target.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f) RotateTowardsMovementDirection(dir.normalized);
    }

    /// Buckets raw stick input into one of 8 directions relative to current facing.
    /// Returns world-space direction; snappedX/snappedZ are -1/0/1 for driving discrete animator params.
    private Vector3 SnapToEightDirections(Vector2 mv, out int snappedX, out int snappedZ)
        => SnapToEightDirections(mv, transform.forward, transform.right, out snappedX, out snappedZ);

    /// Same as above, but takes an explicit forward/right instead of always reading
    /// transform.forward/right. TickLocomotion uses this with orbitDirection's own basis
    /// so movement input and the orbital math share one reference frame — otherwise a
    /// "pure left" press interpreted via transform.rotation (which can drift slightly out
    /// of sync with orbitDirection) leaks a small radial component every frame, which is
    /// what was causing the steady one-directional drift.
    private Vector3 SnapToEightDirections(Vector2 mv, Vector3 fwd, Vector3 right, out int snappedX, out int snappedZ)
    {
        snappedX = 0; snappedZ = 0;
        if (mv.sqrMagnitude < 0.0001f) return Vector3.zero;

        float angle = Mathf.Atan2(mv.x, mv.y) * Mathf.Rad2Deg;
        float snappedAngle = Mathf.Round(angle / 45f) * 45f;

        Vector3 dir = Quaternion.AngleAxis(snappedAngle, Vector3.up) * fwd;

        snappedZ = Mathf.RoundToInt(Vector3.Dot(dir, fwd));   // -1 back, 0 pure strafe, 1 forward
        snappedX = Mathf.RoundToInt(Vector3.Dot(dir, right)); // -1 left, 0 none, 1 right
        return dir;
    }

    /// Buckets raw stick input into one of 4 cardinal directions (no diagonals) relative
    /// to current facing. Used by Dash while within combat range.
    private Vector3 SnapToFourDirections(Vector2 mv, out int snappedX, out int snappedZ)
        => SnapToFourDirections(mv, transform.forward, transform.right, out snappedX, out snappedZ);

    private Vector3 SnapToFourDirections(Vector2 mv, Vector3 fwd, Vector3 right, out int snappedX, out int snappedZ)
    {
        snappedX = 0; snappedZ = 0;
        if (mv.sqrMagnitude < 0.0001f) return Vector3.zero;

        float angle = Mathf.Atan2(mv.x, mv.y) * Mathf.Rad2Deg;
        float snappedAngle = Mathf.Round(angle / 90f) * 90f;

        Vector3 dir = Quaternion.AngleAxis(snappedAngle, Vector3.up) * fwd;

        snappedZ = Mathf.RoundToInt(Vector3.Dot(dir, fwd));   // -1 back, 0, 1 forward
        snappedX = Mathf.RoundToInt(Vector3.Dot(dir, right)); // -1 left, 0, 1 right
        return dir;
    }

    /// Same orbit-aware basis as GetOrbitAwareDirection, for Dash's 4-directional snap
    /// specifically — keeps Dash consistent with the same verified-correct reference frame
    /// Locomotion/Guard/RCT already use, instead of a separately-guessed convention.
    private Vector3 GetOrbitAwareFourDirection(Vector2 mv, out int snappedX, out int snappedZ)
    {
        if (target != null && orbitInitialized)
        {
            Vector3 orbitForward = -orbitDirection;
            Vector3 orbitRight = Vector3.Cross(Vector3.up, orbitForward);
            return SnapToFourDirections(mv, orbitForward, orbitRight, out snappedX, out snappedZ);
        }

        return SnapToFourDirections(mv, out snappedX, out snappedZ);
    }

    /// Resolves raw stick input into: dir (snapped, for actual movement translation),
    /// continuousDir/rawAngleDeg (unsnapped, for rotation and category selection),
    /// speedBlend01 (0 = walk, 1 = run, fed to whichever category state is active), and
    /// targetSpeed (walkSpeed, unaffected by Reinforcement, or moveSpeed × Reinforcement's
    /// multiplier). Shared by TickLocomotion, EnterLocomotion, and TickRCT.
    private bool GetLocomotionMovement(Vector2 mv, out Vector3 continuousDir, out Vector3 rightAxis, out float rawAngleDeg, out float speedBlend01, out float targetSpeed)
    {
        float magnitude = mv.magnitude;

        Vector3 fwd, right;
        if (target != null && orbitInitialized)
        {
            fwd = -orbitDirection;
            right = Vector3.Cross(Vector3.up, fwd);
        }
        else
        {
            fwd = transform.forward;
            right = transform.right;
        }
        rightAxis = right;

        // Continuous (unsnapped) direction and angle — free/analog movement, used directly
        // for translation, plus character rotation, plus picking which of the 4 animation
        // categories is active. -180..180, 0 = forward (relative to fwd), 90 = right, -90 = left.
        if (magnitude < 0.0001f)
        {
            continuousDir = fwd;
            rawAngleDeg = 0f;
        }
        else
        {
            rawAngleDeg = Mathf.Atan2(mv.x, mv.y) * Mathf.Rad2Deg;
            continuousDir = Quaternion.AngleAxis(rawAngleDeg, Vector3.up) * fwd;
        }

        if (magnitude < walkThreshold)
        {
            speedBlend01 = 0f;
            targetSpeed = 0f;
            animator.SetFloat(LocomotionSpeedMultiplierHash, 1f); // whole Locomotion_Combat state shares this — must not leak a non-1 value into idle
            return false;
        }

        bool isRunning = magnitude >= runThreshold;
        speedBlend01 = isRunning ? 1f : 0f;

        // Sprint buildup: only while running, resets instantly the moment you're not.
        bool reinforcementBoostingSpeed = reinforcement != null && reinforcement.MoveSpeedMultiplier > 1.0001f;
        bool sprintAllowed = !reinforcementBoostingSpeed || sprintBonusAppliesWhileReinforced;

        if (isRunning && sprintAllowed)
        {
            sprintHoldTimer += Time.deltaTime;
        }
        else
        {
            sprintHoldTimer = 0f;
        }

        float sprintRampT = 0f;
        if (sprintHoldTimer >= sprintBuildupDelay)
        {
            sprintRampT = sprintRampDuration > 0f
                ? Mathf.Clamp01((sprintHoldTimer - sprintBuildupDelay) / sprintRampDuration)
                : 1f;
        }
        float sprintBonus = sprintMaxBonus * sprintRampT;

        // Locomotion's own animation speed multiplier — separate parameter from
        // AttackSpeedMultiplier, bound to the whole Locomotion_Combat state in the Animator
        // (Unity has no way to bind it to just the run motion inside a blend tree). Only
        // meaningful while running — walk and idle both stay at 1, same reasoning as why
        // walk ignores Reinforcement/sprint for actual speed too. Reinforcement takes
        // priority when it's actively boosting speed (sprint is already blocked in that
        // case unless sprintBonusAppliesWhileReinforced is on, so no conflict between them).
        float locomotionAnimSpeedMultiplier = 1f;
        if (isRunning)
        {
            locomotionAnimSpeedMultiplier = reinforcementBoostingSpeed
                ? reinforcement.ReinforcedAnimSpeed
                : Mathf.Lerp(1f, sprintMaxAnimSpeedMultiplier, sprintRampT);
        }
        animator.SetFloat(LocomotionSpeedMultiplierHash, locomotionAnimSpeedMultiplier);

        targetSpeed = isRunning ? moveSpeed * MoveSpeedMultiplier + sprintBonus : walkSpeed; // walk ignores Reinforcement and sprint entirely
        return true;
    }

    /// Rotates toward worldDir at movementFacingTurnSpeed — used while actively moving,
    /// in place of FaceTarget (which resumes once movement stops).
    private Vector3 smoothedRotationDir;
    private Vector3 smoothedRotationDirVelocity;

    private void RotateTowardsMovementDirection(Vector3 worldDir)
    {
        if (worldDir.sqrMagnitude < 0.0001f) return;

        // Smooth the direction itself, not just the rotation speed — a jittery raw angle at
        // low stick magnitude otherwise produces a jittery rotation TARGET every frame, and
        // the character visibly wobbles chasing it rather than settling into a clean turn,
        // even with movementFacingTurnSpeed capping how fast it CAN turn. This only touches
        // transform.rotation, never position or orbitDirection, so it can't reintroduce the
        // earlier orbital drift bug (that came from smoothing something feeding translation).
        smoothedRotationDir = Vector3.SmoothDamp(smoothedRotationDir, worldDir, ref smoothedRotationDirVelocity, rotationDirSmoothTime);
        if (smoothedRotationDir.sqrMagnitude < 0.0001f) return;

        Quaternion targetRot = Quaternion.LookRotation(smoothedRotationDir, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, movementFacingTurnSpeed * Time.deltaTime);
    }

    /// Picks which of the 4 directional animation states (or Idle) should be active based
    /// on the current input angle, with hysteresis to avoid rapid switching near the
    /// forwardBackThreshold boundary, and CrossFades into it if it changed.
    private LocomotionAnimCategory previousMoveCategory = LocomotionAnimCategory.Idle;

    /// Computes the exact per-category movement direction each frame (Strafe = precise
    /// tangent, Forward/Backward = continuousDir) — but blends from the PREVIOUS exact
    /// direction only for a brief, fixed window right after a category change, not on an
    /// ongoing basis. This is different from the earlier drift bug: that came from
    /// permanently lagging behind a target that kept rotating for as long as you kept
    /// strafing. Here the blend target is always this frame's fresh, correct direction, and
    /// the window is capped — once it elapses, movement is exactly the fresh direction again
    /// with zero blending, so there's nothing left to accumulate error over time.
    private Vector3 GetSmoothedMoveDir(Vector3 continuousDir, Vector3 rightAxis)
    {
        Vector3 exactDir = currentAnimCategory switch
        {
            LocomotionAnimCategory.StrafeLeft => -rightAxis,
            LocomotionAnimCategory.StrafeRight => rightAxis,
            _ => continuousDir
        };

        if (currentAnimCategory != previousMoveCategory)
        {
            moveDirTransitionStart = previousMoveCategory switch
            {
                LocomotionAnimCategory.StrafeLeft => -rightAxis,
                LocomotionAnimCategory.StrafeRight => rightAxis,
                _ => continuousDir
            };
            moveDirTransitionTimer = locomotionBlendSmoothTime;
            previousMoveCategory = currentAnimCategory;
        }

        if (moveDirTransitionTimer > 0f)
        {
            moveDirTransitionTimer -= Time.deltaTime;
            float t = 1f - Mathf.Clamp01(moveDirTransitionTimer / Mathf.Max(0.0001f, locomotionBlendSmoothTime));
            Vector3 blended = Vector3.Slerp(moveDirTransitionStart.normalized, exactDir.normalized, t);
            return blended.sqrMagnitude > 0.0001f ? blended.normalized : exactDir;
        }

        return exactDir;
    }

    private void UpdateLocomotionAnimCategory(bool hasInput, float rawAngleDeg, float magnitude)
    {
        LocomotionAnimCategory desired;

        if (!hasInput)
        {
            desired = LocomotionAnimCategory.Idle;
        }
        else
        {
            float absAngle = Mathf.Abs(rawAngleDeg);
            bool currentlyForwardBack = currentAnimCategory == LocomotionAnimCategory.Forward || currentAnimCategory == LocomotionAnimCategory.Backward;

            // Extra hysteresis near the walk threshold, where a stick barely pushed is most
            // susceptible to angle noise (small physical jitter is a much bigger fraction of
            // a small signal than a large one). Fades back to normal once magnitude is
            // confidently past the threshold, where the angle is reliable again.
            float noiseRange = Mathf.Max(0.0001f, runThreshold - walkThreshold);
            float noiseFactor = 1f - Mathf.Clamp01((magnitude - walkThreshold) / noiseRange);
            float effectiveHysteresis = categoryHysteresis * (1f + noiseFactor * 2f); // up to 3x wider right at the threshold

            float zone = currentlyForwardBack ? forwardBackThreshold + effectiveHysteresis : forwardBackThreshold - effectiveHysteresis;

            if (absAngle <= zone) desired = LocomotionAnimCategory.Forward;
            else if (absAngle >= 180f - zone) desired = LocomotionAnimCategory.Backward;
            else desired = rawAngleDeg > 0f ? LocomotionAnimCategory.StrafeRight : LocomotionAnimCategory.StrafeLeft;
        }

        currentAnimCategory = desired;

        // The blend tree only ever gets pulled toward one of these fixed points — never the
        // raw stick angle — so it can't linger blending at an arbitrary diagonal. SmoothDamp
        // glides it between points on a category change instead of jumping instantly.
        Vector2 targetPoint = desired switch
        {
            LocomotionAnimCategory.Forward => new Vector2(0f, 1f),
            LocomotionAnimCategory.Backward => new Vector2(0f, -1f),
            LocomotionAnimCategory.StrafeLeft => new Vector2(-1f, 0f),
            LocomotionAnimCategory.StrafeRight => new Vector2(1f, 0f),
            _ => Vector2.zero // Idle
        };

        locomotionBlendInput = Vector2.SmoothDamp(locomotionBlendInput, targetPoint, ref locomotionBlendVelocity, locomotionBlendSmoothTime);
        animator.SetFloat(DirXHash, locomotionBlendInput.x);
        animator.SetFloat(DirZHash, locomotionBlendInput.y);
    }
}
