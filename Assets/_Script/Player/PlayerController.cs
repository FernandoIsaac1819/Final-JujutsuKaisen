using System;
using UnityEngine;

/// Locked-on player movement: locomotion around the target, jump, dash, guard, plus the
/// movement side of RCT, attacks and hitstun. Tuning lives in a PlayerSettings asset.
/// Requires CharacterController + Animator. No root motion.
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public class PlayerController : MonoBehaviour
{
    public enum ActionState { Locomotion, Jumping, Landing, DashWindup, Dashing, Guarding, UsingRCT, Attacking, Hitstun }
    public enum GuardReactionResult { None, TimedBlock, Parry }

    [Header("Refs")]
    [SerializeField] private InputReader input;
    [SerializeField] private PlayerSettings settings;
    [Tooltip("Optional. If set, the player orbits and faces it.")]
    [SerializeField] private Transform target;
    [Tooltip("Optional. If null, stats use base values.")]
    [SerializeField] private Reinforcement reinforcement;
    [Tooltip("Optional. Movement is locked to RCT rules while it's active.")]
    [SerializeField] private ReverseCursedTechnique rct;
    [Tooltip("Optional. Movement is locked while a combo is active.")]
    [SerializeField] private ComboController combo;

    [Header("Animator states")]
    [SerializeField] private string locomotionStateName = "Locomotion_Combat";
    [SerializeField] private float locomotionCrossfadeDuration = 0.3f;
    [SerializeField] private string dashStateName = "Directional_Dash";
    [SerializeField] private float dashCrossfadeDuration = 0.05f;

    [Header("Debug")]
    [SerializeField] private bool showLockOnDebugOverlay = true;
    [SerializeField] private float debugOrbitRadius;
    [SerializeField] private float debugActualDistance;

    // ---- Public API ----

    public event Action<Transform> TargetChanged;
    public Transform Target => target;
    public bool CanUseRCT => action == ActionState.Locomotion;
    public bool CanAttack => action == ActionState.Locomotion ||
        (action == ActionState.Dashing && DashProgress01 >= Dash.attackCancelWindow);
    public bool IsGuarding => action == ActionState.Guarding;
    public float GuardDamageReduction => Combat.guardDamageReduction;
    /// Hook for a future Sidestep system.
    public bool IsVulnerableToSidestep => action == ActionState.Locomotion;
    public bool IsInCombatRange() => target != null && Vector3.Distance(transform.position, target.position) <= Combat.combatRange;

    // ---- Settings shortcuts ----

    private LocomotionSettings Loco => settings.locomotion;
    private FacingSettings Facing => settings.facing;
    private JumpSettings Jump => settings.jump;
    private DashSettings Dash => settings.dash;
    private CombatSettings Combat => settings.combat;

    private float MoveSpeedMultiplier => reinforcement != null ? reinforcement.MoveSpeedMultiplier : 1f;
    private float JumpMultiplier => reinforcement != null ? reinforcement.JumpMultiplier : 1f;
    private float DashSpeedMultiplier => reinforcement != null ? reinforcement.DashSpeedMultiplier : 1f;
    private float AirSpeedMultiplier => reinforcement != null ? reinforcement.AirSpeedMultiplier : 1f;

    // ---- Animator parameters ----

    private static readonly int DirXHash = Animator.StringToHash("DirX");
    private static readonly int DirZHash = Animator.StringToHash("DirZ");
    private static readonly int MoveAmountHash = Animator.StringToHash("MoveAmount");
    private static readonly int LocomotionSpeedHash = Animator.StringToHash("LocomotionSpeed01");
    private static readonly int LocomotionSpeedMultiplierHash = Animator.StringToHash("LocomotionSpeedMultiplier");
    private static readonly int AttackSpeedMultiplierHash = Animator.StringToHash("AttackSpeedMultiplier");
    private static readonly int JumpHash = Animator.StringToHash("Jump");
    private static readonly int DashHash = Animator.StringToHash("Dash");
    private static readonly int GuardHash = Animator.StringToHash("Guard");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");
    private static readonly int LandingHash = Animator.StringToHash("Landing");
    private static readonly int VerticalVelocityHash = Animator.StringToHash("VerticalVelocity");

    // ---- Runtime state ----

    private CharacterController controller;
    private Animator animator;
    private ActionState action = ActionState.Locomotion;
    private Vector3 velocity; // horizontal; shared by every state

    // ================================================================ Lifecycle

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();

        if (settings == null)
        {
            Debug.LogError($"[PlayerController] '{name}' has no PlayerSettings asset assigned.", this);
            enabled = false;
            return;
        }

        RecalculateJumpPhysics();
        animator.SetFloat(AttackSpeedMultiplierHash, Combat.attackAnimSpeed);
        animator.SetFloat(LocomotionSpeedMultiplierHash, 1f);
    }

    private void OnValidate()
    {
        if (settings != null) RecalculateJumpPhysics();
    }

    private void OnEnable()
    {
        if (settings == null) return;

        input.JumpEvent += OnJumpPressed;
        input.DashEvent += OnDashPressed;
        input.GuardEvent += OnGuardPressed;
        input.GuardCanceledEvent += OnGuardReleased;
        if (rct != null) rct.ActiveChanged += OnRCTActiveChanged;
        if (combo != null) combo.ActiveChanged += OnComboActiveChanged;
        if (reinforcement != null) reinforcement.TierChanged += OnReinforcementTierChanged;
        animator.SetFloat(AttackSpeedMultiplierHash, Combat.attackAnimSpeed);

        EngageLockOn(); // always locked on
    }

    private void OnDisable()
    {
        input.JumpEvent -= OnJumpPressed;
        input.DashEvent -= OnDashPressed;
        input.GuardEvent -= OnGuardPressed;
        input.GuardCanceledEvent -= OnGuardReleased;
        if (rct != null) rct.ActiveChanged -= OnRCTActiveChanged;
        if (combo != null) combo.ActiveChanged -= OnComboActiveChanged;
        if (reinforcement != null) reinforcement.TierChanged -= OnReinforcementTierChanged;
    }

    /// Attack anim speed follows Reinforcement's tier. TierChanged already fires when the
    /// stats apply, so this stays in sync with movement speed.
    private void OnReinforcementTierChanged(Reinforcement.Tier tier)
    {
        float value = tier == Reinforcement.Tier.Off ? Combat.attackAnimSpeed : reinforcement.ReinforcedAnimSpeed;
        animator.SetFloat(AttackSpeedMultiplierHash, value);
    }

    private void Update()
    {
        TickTimers();
        if (target != null) TickTargetSwitch();

        // A dash pressed during cooldown fires the moment cooldown clears.
        if (dashBufferTimer > 0f && dashCooldownTimer <= 0f && action == ActionState.Locomotion && dashLockoutTimer <= 0f)
        {
            dashBufferTimer = 0f;
            EnterDashWindup();
        }

        switch (action)
        {
            case ActionState.Locomotion: TickGroundMovement(); break;
            case ActionState.UsingRCT: TickGroundMovement(); break;
            case ActionState.Jumping: TickJump(); break;
            case ActionState.Landing: TickLanding(); break;
            case ActionState.Guarding: TickGuard(); break;
            case ActionState.Attacking: TickAttacking(); break;
            case ActionState.Hitstun: TickHitstun(); break;
            case ActionState.DashWindup: TickDashWindup(); break;
            case ActionState.Dashing: TickDash(); break;
        }

        ClampMinDistanceToTarget();

        // Radius rule: a frame that didn't move along the orbit takes the real distance
        // as the new intended one (see Orbit section).
        if (!movedOnOrbitThisFrame) SyncOrbitRadius();
        movedOnOrbitThisFrame = false;

        if (target != null)
        {
            debugOrbitRadius = orbitRadius;
            debugActualDistance = Vector3.Distance(transform.position, target.position);
        }

        animator.SetBool(GroundedHash, controller.isGrounded);
        animator.SetBool(LandingHash, action == ActionState.Landing);
    }

    private void TickTimers()
    {
        float dt = Time.deltaTime;
        if (jumpCooldownTimer > 0f) jumpCooldownTimer -= dt;
        if (jumpLockoutTimer > 0f) jumpLockoutTimer -= dt;
        if (jumpBufferTimer > 0f) jumpBufferTimer -= dt;
        if (dashCooldownTimer > 0f) dashCooldownTimer -= dt;
        if (dashLockoutTimer > 0f) dashLockoutTimer -= dt;
        if (dashBufferTimer > 0f) dashBufferTimer -= dt;
        airborneTimer = controller.isGrounded ? 0f : airborneTimer + dt;
    }

    // ================================================================ Targeting

    private TargetGroup activeGroup;
    private float prevLookX;
    private float targetSwitchCooldownTimer;

    /// Locks onto the nearest TargetGroup's Primary. Safe to call again.
    private void EngageLockOn()
    {
        if (activeGroup == null) activeGroup = FindNearestTargetGroup();
        if (activeGroup != null) SetTarget(activeGroup.Primary);
        Debug.Log($"[LockOn] Engaged, target='{target?.name}'");
    }

    private TargetGroup FindNearestTargetGroup()
    {
        TargetGroup nearest = null;
        float nearestDistSqr = float.MaxValue;
        foreach (TargetGroup group in FindObjectsByType<TargetGroup>(FindObjectsSortMode.None))
        {
            float distSqr = (group.Primary.position - transform.position).sqrMagnitude;
            if (distSqr < nearestDistSqr)
            {
                nearestDistSqr = distSqr;
                nearest = group;
            }
        }
        return nearest;
    }

    /// Takes the new target's distance as the intended orbit radius, then notifies listeners
    /// (camera rigs keep their own reference to the enemy).
    private void SetTarget(Transform newTarget)
    {
        target = newTarget;
        SyncOrbitRadius();
        TargetChanged?.Invoke(target);
    }

    /// Right-stick flick switches target. Fires on the edge (crossing the threshold), not
    /// while held. Threshold and cooldown come from the TargetGroup.
    private void TickTargetSwitch()
    {
        if (activeGroup == null)
        {
            Debug.Log($"[LockOn] No TargetGroup found; flick switching disabled. Frame {Time.frameCount}");
            return;
        }
        if (targetSwitchCooldownTimer > 0f) targetSwitchCooldownTimer -= Time.deltaTime;

        float lookX = input.LookInput.x;
        bool wasBelow = Mathf.Abs(prevLookX) < activeGroup.FlickThreshold;
        bool isAbove = Mathf.Abs(lookX) >= activeGroup.FlickThreshold;

        if (Mathf.Abs(lookX) > 0.05f)
            Debug.Log($"[LockOn] lookX={lookX:F3}, threshold={activeGroup.FlickThreshold:F2}, wasBelow={wasBelow}, isAbove={isAbove}, cooldown={targetSwitchCooldownTimer:F2}");

        if (wasBelow && isAbove && targetSwitchCooldownTimer <= 0f)
        {
            int direction = lookX > 0f ? 1 : -1;
            Transform next = activeGroup.GetTargetInDirection(transform.position, transform.right, target, direction);
            if (next != null)
            {
                SetTarget(next);
                Debug.Log($"[LockOn] Switched target to '{target.name}' (flick {(direction > 0 ? "right" : "left")})");
            }
            else
            {
                Debug.Log($"[LockOn] Flick {(direction > 0 ? "right" : "left")} found no target; check the TargetGroup's Secondaries.");
            }
            targetSwitchCooldownTimer = activeGroup.FlickCooldown;
        }

        prevLookX = lookX;
    }

    private void FaceTarget()
    {
        if (target == null) return;
        Vector3 dir = target.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized);
    }

    // ================================================================ Locomotion (also RCT)

    private enum LocomotionAnimCategory { Idle, Forward, Backward }
    private LocomotionAnimCategory currentAnimCategory = LocomotionAnimCategory.Idle;
    private float idleGraceTimer;
    private float lastSpeedBlend01;          // walk/run value held through the grace window
    private float sprintHoldTimer;
    private Vector2 localPlanarVelocity;     // x = right, y = toward target
    private Vector2 localPlanarVelocityRef;
    private float facingOffset;              // smoothed yaw offset from facing the target (degrees)
    private float facingOffsetVelocity;
    private float facingTargetOffset;
    private float moveAmountBlend, moveAmountBlendVelocity;
    private float dirZBlend, dirZBlendVelocity;

    /// Order matters: category decides facing; the blend reads actual facing vs. actual
    /// travel, so it runs after velocity and facing.
    private void TickGroundMovement()
    {
        Vector2 mv = input.MoveInput;
        bool hasInput = GetLocomotionMovement(mv, out Vector3 continuousDir, out float rawAngleDeg, out float speedBlend01, out float targetSpeed);

        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, mv.magnitude);
        bool moving = currentAnimCategory != LocomotionAnimCategory.Idle; // true through the grace window

        UpdateLocomotionVelocity(hasInput, rawAngleDeg, targetSpeed);
        UpdateLocomotionFacing(hasInput, continuousDir, rawAngleDeg);
        UpdateLocomotionBlend(hasInput, continuousDir);

        Move(velocity);

        // Hold walk/run through the grace window instead of easing toward walk mid-flick.
        if (hasInput) lastSpeedBlend01 = speedBlend01;
        else if (moving) speedBlend01 = lastSpeedBlend01;
        animator.SetFloat(LocomotionSpeedHash, speedBlend01, Loco.walkRunBlendDamp, Time.deltaTime);
    }

    /// Returns false inside the dead zone. Otherwise gives the stick angle (0 = toward
    /// target, 90 = right), its world direction, walk/run blend, and target speed
    /// including sprint buildup. Also drives the run animation speed.
    private bool GetLocomotionMovement(Vector2 mv, out Vector3 continuousDir, out float rawAngleDeg, out float speedBlend01, out float targetSpeed)
    {
        float magnitude = mv.magnitude;
        GetLocomotionBasis(out Vector3 fwd, out _);

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
            animator.SetFloat(LocomotionSpeedMultiplierHash, 1f); // shared by the whole state, incl. Idle
            return false;
        }

        bool isRunning = magnitude >= Loco.runThreshold;
        speedBlend01 = isRunning ? 1f : 0f;

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
        animator.SetFloat(LocomotionSpeedMultiplierHash, animSpeed);

        targetSpeed = isRunning
            ? Loco.runSpeed * MoveSpeedMultiplier + Loco.sprintMaxBonus * sprintRampT
            : Loco.walkSpeed;
        return true;
    }

    /// Forward / Backward / Idle from the stick angle, with hysteresis at the boundary
    /// (wider near the walk threshold, where the angle is noisy). A short dead-zone dip
    /// keeps the current category so a fast flick reads as a reversal.
    private void UpdateLocomotionAnimCategory(bool hasInput, float rawAngleDeg, float magnitude)
    {
        if (hasInput) idleGraceTimer = 0f;
        else idleGraceTimer += Time.deltaTime;

        if (!hasInput)
        {
            bool inGrace = currentAnimCategory != LocomotionAnimCategory.Idle && idleGraceTimer < Loco.idleInputGraceTime;
            currentAnimCategory = inGrace ? currentAnimCategory : LocomotionAnimCategory.Idle;
            return;
        }

        float noiseRange = Mathf.Max(0.0001f, Loco.runThreshold - Loco.walkThreshold);
        float noiseFactor = 1f - Mathf.Clamp01((magnitude - Loco.walkThreshold) / noiseRange);
        float hysteresis = Facing.categoryHysteresis * (1f + noiseFactor * 2f); // up to 3x at the walk threshold

        float threshold = Facing.forwardBackThreshold;
        if (currentAnimCategory == LocomotionAnimCategory.Forward) threshold += hysteresis;
        else if (currentAnimCategory == LocomotionAnimCategory.Backward) threshold -= hysteresis;

        currentAnimCategory = Mathf.Abs(rawAngleDeg) <= threshold ? LocomotionAnimCategory.Forward : LocomotionAnimCategory.Backward;
    }

    /// Velocity is smoothed as two numbers (sideways, toward/away) in the current orbit
    /// frame and recomposed each frame, so orbiting stays a true circle while reversals
    /// slow through zero.
    private void UpdateLocomotionVelocity(bool hasInput, float rawAngleDeg, float targetSpeed)
    {
        GetLocomotionBasis(out Vector3 fwd, out Vector3 right);

        if (currentAnimCategory == LocomotionAnimCategory.Idle)
        {
            // Instant stop once really idle, so there's no slide under the Idle pose.
            localPlanarVelocity = Vector2.zero;
            localPlanarVelocityRef = Vector2.zero;
        }
        else
        {
            Vector2 targetLocal = Vector2.zero; // grace window: ease toward a stop
            if (hasInput)
            {
                float r = rawAngleDeg * Mathf.Deg2Rad;
                targetLocal = new Vector2(Mathf.Sin(r), Mathf.Cos(r)) * targetSpeed;
            }
            localPlanarVelocity = Vector2.SmoothDamp(localPlanarVelocity, targetLocal, ref localPlanarVelocityRef, Loco.velocitySmoothTime);
        }

        velocity = right * localPlanarVelocity.x + fwd * localPlanarVelocity.y;
    }

    /// Facing is a yaw offset from facing the target, smoothed as a number. Forward faces
    /// travel; Backward faces away from it (backpedal); Idle faces the target. The 180°
    /// change at the Forward/Backward boundary always sweeps through 0, so the body turns
    /// through the target.
    private void UpdateLocomotionFacing(bool hasInput, Vector3 continuousDir, float rawAngleDeg)
    {
        if (hasInput)
        {
            facingTargetOffset = currentAnimCategory switch
            {
                LocomotionAnimCategory.Forward => rawAngleDeg,
                LocomotionAnimCategory.Backward => rawAngleDeg - 180f * Mathf.Sign(rawAngleDeg),
                _ => 0f
            };
        }
        else if (currentAnimCategory == LocomotionAnimCategory.Idle)
        {
            facingTargetOffset = 0f;
        }
        // else: grace window, keep the last target (the dead-zone angle is noise).

        if (TryGetFacingBase(out Vector3 baseFwd))
        {
            facingOffset = Mathf.SmoothDamp(facingOffset, facingTargetOffset, ref facingOffsetVelocity, Facing.smoothTime, Facing.turnSpeed);
            transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(facingOffset, Vector3.up) * baseFwd, Vector3.up);
        }
        else if (hasInput)
        {
            Vector3 faceDir = currentAnimCategory == LocomotionAnimCategory.Backward ? -continuousDir : continuousDir;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(faceDir, Vector3.up), Facing.turnSpeed * Time.deltaTime);
        }
    }

    /// Drives Locomotion_Combat: MoveAmount (0 Idle, 1 moving) and DirZ (+1 run, -1
    /// backpedal). DirZ comes from the body's actual facing vs. actual travel, so the legs
    /// crossfade in step with turns and switch as a reversal crosses zero. The ×2 keeps
    /// small turn lag from mixing in the other clip.
    private void UpdateLocomotionBlend(bool hasInput, Vector3 inputDir)
    {
        bool moving = currentAnimCategory != LocomotionAnimCategory.Idle;
        float targetDirZ = dirZBlend; // idle: hold, so starting again doesn't pop

        if (moving)
        {
            // Below this speed the travel direction is meaningless; use the stick instead.
            const float minSpeedForDirection = 0.5f;
            Vector3 planar = Vector3.ProjectOnPlane(velocity, Vector3.up);
            Vector3 travel = planar.sqrMagnitude >= minSpeedForDirection * minSpeedForDirection ? planar.normalized
                           : hasInput ? inputDir
                           : Vector3.zero;
            if (travel != Vector3.zero)
                targetDirZ = Mathf.Clamp(Vector3.Dot(transform.forward, travel) * 2f, -1f, 1f);
        }

        moveAmountBlend = Mathf.SmoothDamp(moveAmountBlend, moving ? 1f : 0f, ref moveAmountBlendVelocity, Loco.blendSmoothTime);
        dirZBlend = Mathf.SmoothDamp(dirZBlend, targetDirZ, ref dirZBlendVelocity, Loco.blendSmoothTime);
        animator.SetFloat(MoveAmountHash, moveAmountBlend);
        animator.SetFloat(DirZHash, dirZBlend);
    }

    /// Re-anchors every piece of Locomotion's smoothing to the current reality, since the
    /// previous state moved, rotated and animated the character by other means.
    private void EnterLocomotion(bool crossfade = true)
    {
        action = ActionState.Locomotion;
        if (crossfade && !string.IsNullOrEmpty(locomotionStateName))
            animator.CrossFadeInFixedTime(locomotionStateName, locomotionCrossfadeDuration);

        // Start blends from the Animator's actual values (Dash/Jump write DirZ directly).
        dirZBlend = animator.GetFloat(DirZHash);
        dirZBlendVelocity = 0f;
        moveAmountBlendVelocity = 0f;

        SyncFacingOffset();

        // Carry the arriving velocity so the handoff eases instead of snapping.
        GetLocomotionBasis(out Vector3 fwd, out Vector3 right);
        localPlanarVelocity = new Vector2(Vector3.Dot(velocity, right), Vector3.Dot(velocity, fwd));
        localPlanarVelocityRef = Vector2.zero;

        // Grace only applies within continuous locomotion; the category is stale here.
        idleGraceTimer = Loco.idleInputGraceTime;

        bool hasInput = GetLocomotionMovement(input.MoveInput, out _, out float rawAngleDeg, out float speedBlend01, out _);
        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, input.MoveInput.magnitude);
        if (hasInput) lastSpeedBlend01 = speedBlend01;
        else localPlanarVelocity = Vector2.zero;

        // MoveAmount is only read by the Locomotion state, which isn't visible yet, so snap
        // it. DirZ is not snapped: the outgoing Dash/Jump trees still read it.
        moveAmountBlend = currentAnimCategory == LocomotionAnimCategory.Idle ? 0f : 1f;
        animator.SetFloat(MoveAmountHash, moveAmountBlend);
        animator.SetFloat(LocomotionSpeedHash, speedBlend01, Loco.walkRunBlendDamp, Time.deltaTime);
    }

    private bool TryGetFacingBase(out Vector3 baseFwd)
    {
        bool ok = TryGetOrbitDirection(out Vector3 toPlayer);
        baseFwd = -toPlayer;
        return ok;
    }

    private void SyncFacingOffset()
    {
        facingOffsetVelocity = 0f;
        facingOffset = TryGetFacingBase(out Vector3 baseFwd)
            ? Vector3.SignedAngle(baseFwd, Vector3.ProjectOnPlane(transform.forward, Vector3.up), Vector3.up)
            : 0f;
        facingTargetOffset = facingOffset;
    }

    // ================================================================ Jump

    private float gravityUp, gravityDown, jumpVelocity;
    private float verticalVelocity;
    private Vector2 airLocalVelocity; // x = right, y = toward target
    private float jumpCooldownTimer, jumpLockoutTimer, jumpBufferTimer;
    private float airborneTimer;
    private float landingTimer;

    private void RecalculateJumpPhysics()
    {
        float effectiveHeight = Jump.height * JumpMultiplier;
        gravityUp = -(2f * effectiveHeight) / (Jump.timeToApex * Jump.timeToApex);
        gravityDown = -(2f * effectiveHeight) / (Jump.timeToFall * Jump.timeToFall);
        jumpVelocity = -gravityUp * Jump.timeToApex;
    }

    private void OnJumpPressed()
    {
        bool fromLocomotion = action == ActionState.Locomotion && jumpLockoutTimer <= 0f;
        bool fromDash = action == ActionState.Dashing || action == ActionState.DashWindup;
        bool fromRCT = action == ActionState.UsingRCT;
        bool grounded = airborneTimer <= Jump.coyoteTime;

        if ((fromLocomotion || fromDash || fromRCT) && grounded && jumpCooldownTimer <= 0f && !IsInCombatRange())
        {
            if (fromRCT) rct?.ForceDeactivate();
            DoJump();
            return;
        }

        if (action == ActionState.Jumping)
            jumpBufferTimer = Jump.bufferWindow; // fires on landing
    }

    private void DoJump()
    {
        RecalculateJumpPhysics(); // pick up runtime tweaks and Reinforcement
        verticalVelocity = jumpVelocity;

        // 4-way snapped to match the directional jump clips; inside the dead zone it's a
        // neutral jump.
        int snappedX = 0, snappedZ = 0;
        Vector2 mv = input.MoveInput;
        if (mv.magnitude >= Loco.walkThreshold)
        {
            float snappedAngle = Mathf.Round(Mathf.Atan2(mv.x, mv.y) * Mathf.Rad2Deg / 90f) * 90f * Mathf.Deg2Rad;
            snappedX = Mathf.RoundToInt(Mathf.Sin(snappedAngle));
            snappedZ = Mathf.RoundToInt(Mathf.Cos(snappedAngle));
        }
        airLocalVelocity = new Vector2(snappedX, snappedZ) * Jump.airSpeed * AirSpeedMultiplier;
        velocity = AirLocalToWorld(airLocalVelocity);
        animator.SetFloat(DirXHash, snappedX);
        animator.SetFloat(DirZHash, snappedZ);

        animator.SetBool(DashHash, false);
        dashIsOrbital = false;
        dashIsFixedDistance = false;
        action = ActionState.Jumping;
        animator.SetTrigger(JumpHash);
    }

    /// Air velocity lives in the orbit frame, so a side jump arcs around the target.
    /// Optional air control steers it past the dead zone; releasing keeps momentum.
    private void TickJump()
    {
        FaceTarget();

        if (Jump.airControl > 0f)
        {
            Vector2 mv = input.MoveInput;
            float magnitude = mv.magnitude;
            if (magnitude >= Loco.walkThreshold)
            {
                Vector2 targetLocal = (mv / magnitude) * Mathf.Clamp01(magnitude) * Jump.airSpeed * AirSpeedMultiplier;
                airLocalVelocity = Vector2.MoveTowards(airLocalVelocity, targetLocal, Jump.airControl * Time.deltaTime);
            }
        }
        velocity = AirLocalToWorld(airLocalVelocity);

        verticalVelocity += (verticalVelocity > 0f ? gravityUp : gravityDown) * Time.deltaTime;
        Vector3 horizontal = OrbitStep(velocity * Time.deltaTime);
        controller.Move(horizontal + Vector3.up * verticalVelocity * Time.deltaTime);

        animator.SetFloat(VerticalVelocityHash, verticalVelocity);

        if (controller.isGrounded && verticalVelocity <= 0f)
        {
            verticalVelocity = 0f;
            jumpCooldownTimer = Jump.cooldown;

            if (jumpBufferTimer > 0f)
            {
                jumpBufferTimer = 0f;
                DoJump(); // buffered press: skip Landing
                return;
            }

            action = ActionState.Landing;
            landingTimer = Jump.landingRecovery;
        }
    }

    private void TickLanding()
    {
        // Stick input cancels recovery. Crossfade from code: this path never sets
        // Landing=true, and Jump rise's own exit (DirZ > 0.4) misses backpedals.
        if (input.MoveInput.magnitude >= Loco.walkThreshold)
        {
            EnterLocomotion(crossfade: true);
            dashLockoutTimer = Dash.lockoutAfterLanding;
            return;
        }

        landingTimer -= Time.deltaTime;
        if (landingTimer <= 0f)
        {
            EnterLocomotion(crossfade: false); // Land -> Locomotion via the Animator
            dashLockoutTimer = Dash.lockoutAfterLanding;
        }
    }

    private Vector3 AirLocalToWorld(Vector2 local)
    {
        GetLocomotionBasis(out Vector3 fwd, out Vector3 right);
        return right * local.x + fwd * local.y;
    }

    // ================================================================ Dash

    private float dashBufferTimer, dashAimTimer, dashCooldownTimer, dashLockoutTimer;
    private float dashLaunchMultiplier = 1f;
    private bool dashIsOrbital;       // in range, sideways: sweeps around the target
    private bool dashIsFixedDistance; // in range, forward/back: fixed distance and speed
    private float dashFixedRemaining;
    private float dashLaunchSpeed;
    private float dashLaunchAngularSpeed;
    private float dashOrbitAngularSpeed;
    private float dashOrbitAngleRemaining;
    private float dashOrbitAngularDecel;

    /// 0 at launch, 1 when finished. Based on remaining speed/distance, not clip time,
    /// since Reinforcement changes how long a dash lasts.
    private float DashProgress01
    {
        get
        {
            if (dashIsOrbital)
                return dashLaunchAngularSpeed <= 0.0001f ? 1f : 1f - Mathf.Clamp01(Mathf.Abs(dashOrbitAngularSpeed) / dashLaunchAngularSpeed);
            if (dashIsFixedDistance)
                return Dash.inRangeDistance <= 0.0001f ? 1f : 1f - Mathf.Clamp01(dashFixedRemaining / Dash.inRangeDistance);
            return dashLaunchSpeed <= 0.0001f ? 1f : 1f - Mathf.Clamp01(velocity.magnitude / dashLaunchSpeed);
        }
    }

    private void OnDashPressed()
    {
        bool fromLocomotion = action == ActionState.Locomotion && dashLockoutTimer <= 0f;
        bool fromLanding = action == ActionState.Landing;
        bool fromRCT = action == ActionState.UsingRCT;
        bool fromAttack = action == ActionState.Attacking && combo != null && combo.InPunishWindow;
        bool stateOk = fromLocomotion || fromLanding || fromRCT || fromAttack;

        if (stateOk && dashCooldownTimer <= 0f)
        {
            if (fromRCT) rct?.ForceDeactivate();
            if (fromAttack) combo?.ForceEndCombo();
            EnterDashWindup();
            return;
        }

        if (stateOk)
            dashBufferTimer = Dash.bufferWindow; // only cooldown blocked it
    }

    private void EnterDashWindup()
    {
        action = ActionState.DashWindup;
        dashAimTimer = Dash.aimWindow;
        velocity = Vector3.zero; // frozen while aiming
    }

    private void TickDashWindup()
    {
        dashAimTimer -= Time.deltaTime;

        if (input.MoveInput.sqrMagnitude > 0.0001f)
            LaunchDash(input.MoveInput);
        else if (dashAimTimer <= 0f)
            EnterLocomotion(); // no direction given: no dash
    }

    /// In combat range: sideways sweeps around the target, forward/back is a short fixed
    /// dash (still can't pass attackRange). Outside: a decelerating straight dash.
    private void LaunchDash(Vector2 aimInput)
    {
        bool nearTarget = IsInCombatRange() && target != null;
        Vector3 dashDir = GetOrbitAwareFourDirection(aimInput, out int snappedX, out int snappedZ);

        animator.SetFloat(DirXHash, snappedX);
        animator.SetFloat(DirZHash, snappedZ);

        action = ActionState.Dashing;
        dashLaunchMultiplier = DashSpeedMultiplier;
        dashIsOrbital = nearTarget && snappedX != 0;
        dashIsFixedDistance = nearTarget && snappedX == 0;

        if (dashIsOrbital)
        {
            TryGetOrbitDirection(out Vector3 toPlayer);
            float sign = Mathf.Sign(Vector3.Dot(dashDir, Vector3.Cross(Vector3.up, toPlayer)));
            float radius = Mathf.Max(orbitRadius, 0.01f);

            dashOrbitAngleRemaining = Dash.sideDodgeAngle * sign;

            if (Dash.solveAngleFromDuration)
            {
                // Exactly sideDodgeAngle, in exactly the straight dash's duration.
                float baseDuration = Dash.deceleration > 0f ? Dash.speed / Dash.deceleration : 0.01f;
                float duration = Mathf.Max(0.01f, baseDuration / Mathf.Max(dashLaunchMultiplier, 0.01f));
                dashOrbitAngularSpeed = (2f * Dash.sideDodgeAngle / duration) * sign;
                dashOrbitAngularDecel = Mathf.Abs(dashOrbitAngularSpeed) / duration;
            }
            else
            {
                // Straight dash's feel; stops early if the angle is reached first.
                dashOrbitAngularSpeed = (Dash.speed * dashLaunchMultiplier / radius) * Mathf.Rad2Deg * sign;
                dashOrbitAngularDecel = 0f; // recomputed from radius each frame
            }
            dashLaunchAngularSpeed = Mathf.Abs(dashOrbitAngularSpeed);
        }
        else if (dashIsFixedDistance)
        {
            dashFixedRemaining = Dash.inRangeDistance;
            velocity = dashDir * Dash.inRangeSpeed * dashLaunchMultiplier;
        }
        else
        {
            velocity = dashDir * Dash.speed * dashLaunchMultiplier;
            dashLaunchSpeed = velocity.magnitude;
        }

        animator.SetBool(DashHash, true);
        if (!string.IsNullOrEmpty(dashStateName))
            animator.CrossFadeInFixedTime(dashStateName, dashCrossfadeDuration);
    }

    private void TickDash()
    {
        FaceTarget();
        Vector3 stick = Vector3.up * Loco.groundStickForce * Time.deltaTime;

        if (dashIsOrbital)
        {
            float angularDecel = Dash.solveAngleFromDuration
                ? dashOrbitAngularDecel
                : (Dash.deceleration * dashLaunchMultiplier / Mathf.Max(orbitRadius, 0.01f)) * Mathf.Rad2Deg;

            dashOrbitAngularSpeed = Mathf.MoveTowards(dashOrbitAngularSpeed, 0f, angularDecel * Time.deltaTime);

            float step = dashOrbitAngularSpeed * Time.deltaTime;
            if (Mathf.Abs(step) > Mathf.Abs(dashOrbitAngleRemaining))
                step = dashOrbitAngleRemaining; // never overshoot the angle
            dashOrbitAngleRemaining -= step;

            TryGetOrbitDirection(out Vector3 toPlayer);
            controller.Move(OrbitCorrection(Quaternion.AngleAxis(step, Vector3.up) * toPlayer) + stick);

            if (Mathf.Abs(dashOrbitAngleRemaining) <= 0.05f || Mathf.Abs(dashOrbitAngularSpeed) <= 0.5f)
                EndDash();
        }
        else if (dashIsFixedDistance)
        {
            float step = Mathf.Min(Dash.inRangeSpeed * dashLaunchMultiplier * Time.deltaTime, dashFixedRemaining);
            controller.Move(ClampApproachToMinDistance(velocity.normalized * step) + stick);
            dashFixedRemaining -= step;
            if (dashFixedRemaining <= 0f) EndDash();
        }
        else
        {
            velocity = Vector3.MoveTowards(velocity, Vector3.zero, Dash.deceleration * dashLaunchMultiplier * Time.deltaTime);
            controller.Move(ClampApproachToMinDistance(velocity * Time.deltaTime) + stick);
            if (velocity.sqrMagnitude <= 0.0001f) EndDash();
        }
    }

    private void EndDash()
    {
        ClearDash();
        EnterLocomotion();
    }

    /// Shared by EndDash and an attack cancelling the dash.
    private void ClearDash()
    {
        velocity = Vector3.zero;
        dashIsOrbital = false;
        dashIsFixedDistance = false;
        animator.SetBool(DashHash, false);
        dashCooldownTimer = Dash.cooldown;
        jumpLockoutTimer = Jump.lockoutAfterDash;
    }

    private Vector3 GetOrbitAwareFourDirection(Vector2 mv, out int snappedX, out int snappedZ)
    {
        GetLocomotionBasis(out Vector3 fwd, out Vector3 right);
        snappedX = 0;
        snappedZ = 0;
        if (mv.sqrMagnitude < 0.0001f) return Vector3.zero;

        float snappedAngle = Mathf.Round(Mathf.Atan2(mv.x, mv.y) * Mathf.Rad2Deg / 90f) * 90f;
        Vector3 dir = Quaternion.AngleAxis(snappedAngle, Vector3.up) * fwd;
        snappedZ = Mathf.RoundToInt(Vector3.Dot(dir, fwd));
        snappedX = Mathf.RoundToInt(Vector3.Dot(dir, right));
        return dir;
    }

    // ================================================================ Guard

    private float lastGuardPressTime = -999f;

    /// Called when a hit lands. A guard press within reactionWindowDuration counts as a
    /// timed block (within parryThreshold, a parry). Quality is 1 at parry timing, 0 at
    /// the window's edge.
    public GuardReactionResult EvaluateGuardTiming(float reactionWindowDuration, float parryThreshold, out float quality)
    {
        quality = 0f;
        if (!IsGuarding) return GuardReactionResult.None;

        float timeSincePress = Time.time - lastGuardPressTime;
        if (timeSincePress > reactionWindowDuration) return GuardReactionResult.None;

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
        bool fromRCT = action == ActionState.UsingRCT;
        if (action != ActionState.Locomotion && !fromRCT) return;
        if (fromRCT) rct?.ForceDeactivate();

        action = ActionState.Guarding;
        velocity = Vector3.zero;
        lastGuardPressTime = Time.time;
        animator.SetBool(GuardHash, true);
    }

    private void OnGuardReleased()
    {
        if (action != ActionState.Guarding) return;
        EnterLocomotion();
        animator.SetBool(GuardHash, false);
    }

    /// Static hold: faces the target, keeps the orbit distance, no movement.
    private void TickGuard()
    {
        FaceTarget();
        velocity = Vector3.zero;
        Move(velocity);
    }

    // ================================================================ RCT

    /// RCT moves under Locomotion rules (TickGroundMovement); this just enters/exits it.
    private void OnRCTActiveChanged(bool active)
    {
        if (active)
        {
            if (action == ActionState.Locomotion) action = ActionState.UsingRCT;
        }
        else if (action == ActionState.UsingRCT)
        {
            EnterLocomotion();
        }
    }

    // ================================================================ Attacks & hitstun

    private Vector3 attackStepDirection;
    private float attackStepRemaining;
    private float attackStepSpeed;
    private float hitstunTimer;

    private void OnComboActiveChanged(bool active)
    {
        if (active)
        {
            bool fromDash = action == ActionState.Dashing;
            if (action != ActionState.Locomotion && !fromDash) return; // CanAttack gates this
            if (fromDash) ClearDash();
            action = ActionState.Attacking;
        }
        else if (action == ActionState.Attacking)
        {
            EnterLocomotion();
        }
    }

    /// Called by ComboController when a move starts. Direction is captured once so the
    /// step can't drift.
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
            controller.Move(ClampApproachToMinDistance(attackStepDirection * step) + Vector3.up * Loco.groundStickForce * Time.deltaTime);
            attackStepRemaining -= step;
        }

        // Keep the locomotion category current so the attack can end straight into it.
        bool hasInput = GetLocomotionMovement(input.MoveInput, out _, out float rawAngle, out _, out _);
        UpdateLocomotionAnimCategory(hasInput, rawAngle, input.MoveInput.magnitude);
    }

    /// Called by an attacker's ComboController when a hit lands on this character.
    public void ApplyHitReaction(float hitstunDuration, Vector3 knockbackVelocity)
    {
        rct?.ForceDeactivate();
        action = ActionState.Hitstun;
        hitstunTimer = hitstunDuration;
        velocity = knockbackVelocity;
    }

    private void TickHitstun()
    {
        velocity = Vector3.MoveTowards(velocity, Vector3.zero, Combat.knockbackDecay * Time.deltaTime);
        controller.Move((velocity + Vector3.up * Loco.groundStickForce) * Time.deltaTime);

        hitstunTimer -= Time.deltaTime;
        if (hitstunTimer <= 0f) EnterLocomotion();
    }

    // ================================================================ Orbit movement

    // Only the INTENDED distance to the target is stored; direction is read fresh from the
    // real position. Drift is a radius problem, so an authoritative radius is enough.
    // Radius rule (end of Update): a frame that didn't move along the orbit re-reads the
    // radius from the real distance. Orbital frames keep it and change it only through
    // deliberate toward/away movement.
    private float orbitRadius;
    private bool movedOnOrbitThisFrame;

    /// Stick input frame: fwd = toward the target (or the body's forward with no target).
    private void GetLocomotionBasis(out Vector3 fwd, out Vector3 right)
    {
        if (TryGetOrbitDirection(out Vector3 toPlayer))
        {
            fwd = -toPlayer;
            right = Vector3.Cross(Vector3.up, fwd);
        }
        else
        {
            fwd = transform.forward;
            right = transform.right;
        }
    }

    /// Flat direction from the target to the player.
    private bool TryGetOrbitDirection(out Vector3 dir)
    {
        dir = Vector3.forward;
        if (target == null) return false;
        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 0.0001f) return false;
        dir = toPlayer.normalized;
        return true;
    }

    private void SyncOrbitRadius()
    {
        if (target == null) return;
        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        orbitRadius = toPlayer.magnitude;
    }

    /// Grounded move along the orbit, plus the ground-stick force.
    private void Move(Vector3 horizontalVelocity)
    {
        Vector3 displacement = OrbitStep(horizontalVelocity * Time.deltaTime);
        controller.Move(displacement + Vector3.up * Loco.groundStickForce * Time.deltaTime);
    }

    /// Toward/away changes the intended radius; sideways becomes an arc (length / radius
    /// = angle). Returns the displacement that puts the character there. No target: the
    /// displacement passes through unchanged.
    private Vector3 OrbitStep(Vector3 desiredDisplacement)
    {
        if (!TryGetOrbitDirection(out Vector3 radialDir)) return desiredDisplacement;
        if (orbitRadius <= 0f) SyncOrbitRadius();

        Vector3 tangentDir = Vector3.Cross(Vector3.up, radialDir);
        orbitRadius = Mathf.Max(0.01f, orbitRadius + Vector3.Dot(desiredDisplacement, radialDir));
        float angleDeg = Vector3.Dot(desiredDisplacement, tangentDir) / orbitRadius * Mathf.Rad2Deg;

        return OrbitCorrection(Quaternion.AngleAxis(angleDeg, Vector3.up) * radialDir);
    }

    /// Displacement to newDir at the intended radius. Shared by OrbitStep and the side dash.
    private Vector3 OrbitCorrection(Vector3 newDir)
    {
        movedOnOrbitThisFrame = true;
        Vector3 correction = target.position + newDir * orbitRadius - transform.position;
        correction.y = 0f;
        return correction;
    }

    /// Pushes the player back out to attackRange if this frame got closer.
    private void ClampMinDistanceToTarget()
    {
        if (target == null) return;

        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        float distance = toPlayer.magnitude;

        if (distance < Combat.attackRange && distance > 0.0001f)
        {
            Vector3 correction = target.position + toPlayer.normalized * Combat.attackRange - transform.position;
            correction.y = 0f;
            controller.Move(correction + Vector3.up * Loco.groundStickForce * Time.deltaTime);
            orbitRadius = Combat.attackRange;
        }
    }

    /// Trims the toward-target part of a straight move (attack step, straight dash) so it
    /// stops at attackRange instead of overshooting and being pushed back a frame later.
    private Vector3 ClampApproachToMinDistance(Vector3 desiredDisplacement)
    {
        if (target == null) return desiredDisplacement;

        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        float currentDistance = toPlayer.magnitude;
        if (currentDistance <= 0.0001f) return desiredDisplacement;

        Vector3 towardTarget = -toPlayer.normalized;
        float approach = Vector3.Dot(desiredDisplacement, towardTarget);
        float maxApproach = Mathf.Max(0f, currentDistance - Combat.attackRange);
        if (approach > maxApproach)
            desiredDisplacement -= towardTarget * (approach - maxApproach);

        return desiredDisplacement;
    }

    // ================================================================ Debug drawing

    private void OnGUI()
    {
        if (!showLockOnDebugOverlay) return;
        var style = new GUIStyle { fontSize = 20, normal = { textColor = target != null ? Color.yellow : Color.white } };
        GUI.Label(new Rect(10, 10, 400, 60), $"Target: {(target != null ? target.name : "none")}", style);
    }

    private void OnDrawGizmos()
    {
        if (target == null || settings == null) return;

        Gizmos.color = Color.cyan;
        DrawGizmoCircle(target.position, Combat.combatRange, 48);
        Gizmos.color = Color.red;
        DrawGizmoCircle(target.position, Combat.attackRange, 32);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(target.position + Vector3.up * 1.8f, 0.3f);
    }

    private static void DrawGizmoCircle(Vector3 center, float radius, int segments)
    {
        if (radius <= 0f) return;
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i / (float)segments * 360f * Mathf.Deg2Rad;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
