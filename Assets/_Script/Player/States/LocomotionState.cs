using UnityEngine;

/// Walking/running around the target (also used while RCT is active). Forward runs face
/// travel; backward input backpedals while facing the target. Everything is smoothed in
/// the orbit frame so orbiting stays a true circle.
public class LocomotionState : PlayerState
{
    private enum Category { Idle, Forward, Backward }

    private Category category = Category.Idle;
    private float idleGraceTimer;
    private float lastSpeedBlend01;      // walk/run value held through the grace window
    private float sprintHoldTimer;
    private Vector2 localVelocity;        // x = right, y = toward target
    private Vector2 localVelocityRef;
    private float facingOffset;          // smoothed yaw offset from facing the target (degrees)
    private float facingOffsetVelocity;
    private float facingTargetOffset;
    private float moveAmountBlend, moveAmountBlendVelocity;
    private float dirZBlend, dirZBlendVelocity;

    public LocomotionState(PlayerController p) : base(p) { }

    /// Set by PlayerController.EnterLocomotion before entering; null = the default fade.
    public float? EnterFade { get; set; }

    /// Re-anchors all smoothing to the current reality: the previous state moved, rotated
    /// and animated the character by other means.
    public override void Enter()
    {
        if (EnterFade.HasValue) Anim.PlayLocomotion(EnterFade.Value);
        else Anim.PlayLocomotion();
        EnterFade = null;

        // Start blends from the Animator's actual values (Dash/Jump write DirZ directly).
        dirZBlend = Anim.CurrentDirZ;
        dirZBlendVelocity = 0f;
        moveAmountBlendVelocity = 0f;

        facingOffsetVelocity = 0f;
        facingOffset = Motor.TryGetOrbitDirection(out Vector3 toPlayer)
            ? Vector3.SignedAngle(-toPlayer, Vector3.ProjectOnPlane(p.transform.forward, Vector3.up), Vector3.up)
            : 0f;
        facingTargetOffset = facingOffset;

        // Carry the arriving velocity so the handoff eases instead of snapping.
        localVelocity = Motor.WorldToLocal(p.Velocity);
        localVelocityRef = Vector2.zero;

        // Grace only applies within continuous locomotion; the category is stale here.
        idleGraceTimer = Loco.idleInputGraceTime;

        bool hasInput = ReadStick(Input.MoveInput, out _, out float rawAngleDeg, out float speedBlend01, out _);
        UpdateCategory(hasInput, rawAngleDeg, Input.MoveInput.magnitude);
        if (hasInput) lastSpeedBlend01 = speedBlend01;
        else localVelocity = Vector2.zero;

        // MoveAmount is only read by the Locomotion state, which isn't visible yet, so snap
        // it. DirZ is not snapped: the outgoing Dash/Jump trees still read it.
        moveAmountBlend = category == Category.Idle ? 0f : 1f;
        Anim.SetMoveAmount(moveAmountBlend);
        Anim.SetWalkRun(speedBlend01, Loco.walkRunBlendDamp);
    }

    /// Order matters: category decides facing; the blend reads actual facing vs. actual
    /// travel, so it runs after velocity and facing.
    public override void Tick()
    {
        Vector2 mv = Input.MoveInput;
        bool hasInput = ReadStick(mv, out Vector3 continuousDir, out float rawAngleDeg, out float speedBlend01, out float targetSpeed);

        UpdateCategory(hasInput, rawAngleDeg, mv.magnitude);
        bool moving = category != Category.Idle; // true through the grace window

        UpdateVelocity(hasInput, rawAngleDeg, targetSpeed);
        UpdateFacing(hasInput, continuousDir, rawAngleDeg);
        UpdateBlend(hasInput, continuousDir);

        Motor.Move(p.Velocity);

        // Hold walk/run through the grace window instead of easing toward walk mid-flick.
        if (hasInput) lastSpeedBlend01 = speedBlend01;
        else if (moving) speedBlend01 = lastSpeedBlend01;
        Anim.SetWalkRun(speedBlend01, Loco.walkRunBlendDamp);
    }

    /// While another state (an attack) is active, keep the category current so it can end
    /// straight into Locomotion.
    public void TrackInputWhileBusy()
    {
        bool hasInput = ReadStick(Input.MoveInput, out _, out float rawAngle, out _, out _);
        UpdateCategory(hasInput, rawAngle, Input.MoveInput.magnitude);
    }

    /// Returns false inside the dead zone. Otherwise gives the stick angle (0 = toward
    /// target, 90 = right), its world direction, walk/run blend, and target speed
    /// including sprint buildup. Also drives the run animation speed.
    private bool ReadStick(Vector2 mv, out Vector3 continuousDir, out float rawAngleDeg, out float speedBlend01, out float targetSpeed)
    {
        float magnitude = mv.magnitude;
        Motor.GetBasis(out Vector3 fwd, out _);

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

        if (magnitude < Loco.walkThreshold)
        {
            speedBlend01 = 0f;
            targetSpeed = 0f;
            sprintHoldTimer = 0f;
            Anim.SetRunAnimSpeed(1f); // shared by the whole state, incl. Idle
            return false;
        }

        bool isRunning = magnitude >= Loco.runThreshold;
        speedBlend01 = isRunning ? 1f : 0f;

        Reinforcement reinforcement = p.Reinforcement;
        bool reinforcementBoostingSpeed = reinforcement != null && reinforcement.MoveSpeedMultiplier > 1.0001f;
        bool sprintAllowed = !reinforcementBoostingSpeed || Loco.sprintWhileReinforced;
        sprintHoldTimer = isRunning && sprintAllowed ? sprintHoldTimer + Time.deltaTime : 0f;

        float sprintRampT = 0f;
        if (sprintHoldTimer >= Loco.sprintBuildupDelay)
        {
            sprintRampT = Loco.sprintRampDuration > 0f
                ? Mathf.Clamp01((sprintHoldTimer - Loco.sprintBuildupDelay) / Loco.sprintRampDuration)
                : 1f;
        }

        // Run animation speed: Reinforcement's when it's boosting, else the sprint ramp.
        // Walk stays at 1, like walk speed ignores Reinforcement and sprint.
        float animSpeed = 1f;
        if (isRunning)
        {
            animSpeed = reinforcementBoostingSpeed
                ? reinforcement.ReinforcedAnimSpeed
                : Mathf.Lerp(1f, Loco.sprintMaxAnimSpeed, sprintRampT);
        }
        Anim.SetRunAnimSpeed(animSpeed);

        targetSpeed = isRunning
            ? Loco.runSpeed * p.MoveSpeedMultiplier + Loco.sprintMaxBonus * sprintRampT
            : Loco.walkSpeed;
        return true;
    }

    /// Forward / Backward / Idle from the stick angle, with hysteresis at the boundary
    /// (wider near the walk threshold, where the angle is noisy). A short dead-zone dip
    /// keeps the current category so a fast flick reads as a reversal.
    private void UpdateCategory(bool hasInput, float rawAngleDeg, float magnitude)
    {
        if (hasInput) idleGraceTimer = 0f;
        else idleGraceTimer += Time.deltaTime;

        if (!hasInput)
        {
            bool inGrace = category != Category.Idle && idleGraceTimer < Loco.idleInputGraceTime;
            if (!inGrace) category = Category.Idle;
            return;
        }

        float noiseRange = Mathf.Max(0.0001f, Loco.runThreshold - Loco.walkThreshold);
        float noiseFactor = 1f - Mathf.Clamp01((magnitude - Loco.walkThreshold) / noiseRange);
        float hysteresis = Facing.categoryHysteresis * (1f + noiseFactor * 2f); // up to 3x at the walk threshold

        float threshold = Facing.forwardBackThreshold;
        if (category == Category.Forward) threshold += hysteresis;
        else if (category == Category.Backward) threshold -= hysteresis;

        category = Mathf.Abs(rawAngleDeg) <= threshold ? Category.Forward : Category.Backward;
    }

    /// Velocity is smoothed as two numbers (sideways, toward/away) in the current orbit
    /// frame and recomposed each frame, so orbiting stays a true circle while reversals
    /// slow through zero.
    private void UpdateVelocity(bool hasInput, float rawAngleDeg, float targetSpeed)
    {
        if (category == Category.Idle)
        {
            // Instant stop once really idle, so there's no slide under the Idle pose.
            localVelocity = Vector2.zero;
            localVelocityRef = Vector2.zero;
        }
        else
        {
            Vector2 targetLocal = Vector2.zero; // grace window: ease toward a stop
            if (hasInput)
            {
                float r = rawAngleDeg * Mathf.Deg2Rad;
                targetLocal = new Vector2(Mathf.Sin(r), Mathf.Cos(r)) * targetSpeed;
            }
            localVelocity = Vector2.SmoothDamp(localVelocity, targetLocal, ref localVelocityRef, Loco.velocitySmoothTime);
        }

        p.Velocity = Motor.LocalToWorld(localVelocity);
    }

    /// Facing is a yaw offset from facing the target, smoothed as a number. Forward faces
    /// travel; Backward faces away from it (backpedal); Idle faces the target. The 180°
    /// change at the Forward/Backward boundary always sweeps through 0, so the body turns
    /// through the target.
    private void UpdateFacing(bool hasInput, Vector3 continuousDir, float rawAngleDeg)
    {
        if (hasInput)
        {
            facingTargetOffset = category switch
            {
                Category.Forward => rawAngleDeg,
                Category.Backward => rawAngleDeg - 180f * Mathf.Sign(rawAngleDeg),
                _ => 0f
            };
        }
        else if (category == Category.Idle)
        {
            facingTargetOffset = 0f;
        }
        // else: grace window, keep the last target (the dead-zone angle is noise).

        Transform t = p.transform;
        if (Motor.TryGetOrbitDirection(out Vector3 toPlayer))
        {
            facingOffset = Mathf.SmoothDamp(facingOffset, facingTargetOffset, ref facingOffsetVelocity, Facing.smoothTime, Facing.turnSpeed);
            t.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(facingOffset, Vector3.up) * -toPlayer, Vector3.up);
        }
        else if (hasInput)
        {
            Vector3 faceDir = category == Category.Backward ? -continuousDir : continuousDir;
            t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(faceDir, Vector3.up), Facing.turnSpeed * Time.deltaTime);
        }
    }

    /// Drives Locomotion_Combat: MoveAmount (0 Idle, 1 moving) and DirZ (+1 run, -1
    /// backpedal). DirZ comes from the body's actual facing vs. actual travel, so the legs
    /// crossfade in step with turns and switch as a reversal crosses zero. The ×2 keeps
    /// small turn lag from mixing in the other clip.
    private void UpdateBlend(bool hasInput, Vector3 inputDir)
    {
        bool moving = category != Category.Idle;
        float targetDirZ = dirZBlend; // idle: hold, so starting again doesn't pop

        if (moving)
        {
            // Below this speed the travel direction is meaningless; use the stick instead.
            const float minSpeedForDirection = 0.5f;
            Vector3 planar = Vector3.ProjectOnPlane(p.Velocity, Vector3.up);
            Vector3 travel = planar.sqrMagnitude >= minSpeedForDirection * minSpeedForDirection ? planar.normalized
                           : hasInput ? inputDir
                           : Vector3.zero;
            if (travel != Vector3.zero)
                targetDirZ = Mathf.Clamp(Vector3.Dot(p.transform.forward, travel) * 2f, -1f, 1f);
        }

        moveAmountBlend = Mathf.SmoothDamp(moveAmountBlend, moving ? 1f : 0f, ref moveAmountBlendVelocity, Loco.blendSmoothTime);
        dirZBlend = Mathf.SmoothDamp(dirZBlend, targetDirZ, ref dirZBlendVelocity, Loco.blendSmoothTime);
        Anim.SetLocomotionBlend(moveAmountBlend, dirZBlend);
    }
}
