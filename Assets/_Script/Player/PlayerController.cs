using System;
using UnityEngine;

/// <summary>
/// Drives grounded 8-directional locomotion (always on, no Free/Combat mode toggle),
/// jump, dash, and guard lock. Guard is a fully static hold — no movement, no rotation
/// beyond tracking the target, no interruption except releasing it. "Combat" as a broader
/// concept is intentionally not represented here anymore — that's reserved for whatever
/// actual fight-state logic gets built later. Requires CharacterController + Animator on
/// the same GameObject. Root motion is not used anywhere in this script.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public class PlayerController : MonoBehaviour
{
    public enum ActionState { Locomotion, Jumping, Landing, DashWindup, Dashing, Guarding, UsingRCT, Attacking, Hitstun }

    [Header("Refs")]
    [SerializeField] private InputReader input;
    [SerializeField] private Transform target; // optional — if set, character faces it; if null, keeps current facing
    [SerializeField] private Reinforcement reinforcement; // optional — if null, stats just use base values
    [Tooltip("Attack animation playback speed while Reinforcement is Off. Only affects " +
             "states in the Animator that reference the AttackSpeedMultiplier parameter — " +
             "see setup instructions. Does not affect Locomotion, Guard, Dash, etc.")]
    [SerializeField] private float normalAnimSpeed = 1f;
    [Tooltip("Optional. Locks movement while active, based on its own ActiveChanged event.")]
    [SerializeField] private ReverseCursedTechnique rct;
    [Tooltip("Optional. Locks movement while attacking, based on its own ActiveChanged event.")]
    [SerializeField] private ComboController combo;

    public bool CanUseRCT => action == ActionState.Locomotion;
    public bool CanAttack => action == ActionState.Locomotion ||
        (action == ActionState.Dashing && DashProgress01 >= dashAttackCancelWindow);

    // ---------------- Targeting ----------------
    // The game is always locked on — there's no toggle or free-look mode anymore. This
    // section just finds the fight's TargetGroup, locks onto its Primary, and lets the
    // player flick-switch between Primary/Secondaries.
    public Transform Target => target;

    /// Fired whenever target is reassigned (initial engage, or a flick switch) — anything
    /// with its own independent reference to the enemy (the camera, notably) needs to
    /// subscribe to this rather than assuming target never changes.
    public event Action<Transform> TargetChanged;

    private TargetGroup activeGroup; // the group currently providing targets — found once on startup
    private float prevLookX;
    private float targetSwitchCooldownTimer;

    /// Finds the fight's TargetGroup and locks onto its Primary. Called once on startup —
    /// there's no toggle to re-trigger this later, but it's harmless to call again (e.g.
    /// if the target is ever lost) since it always re-resolves from scratch.
    private void EngageLockOn()
    {
        if (activeGroup == null) activeGroup = FindNearestTargetGroup();
        if (activeGroup != null)
        {
            SetTarget(activeGroup.Primary); // ALWAYS the primary on engage, never nearest
        }

        Debug.Log($"[LockOn] Engaged, target='{target?.name}'");
    }

    /// The game is strict 1v1, so there should only ever be one TargetGroup active at a
    /// time in practice — this search is just future-proofing, not meant to handle several
    /// simultaneous fights.
    private TargetGroup FindNearestTargetGroup()
    {
        TargetGroup[] groups = FindObjectsByType<TargetGroup>(FindObjectsSortMode.None);
        TargetGroup nearest = null;
        float nearestDistSqr = float.MaxValue;

        foreach (TargetGroup group in groups)
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

    /// Reassigns target and does everything that MUST happen alongside that: resyncing
    /// orbit state (otherwise the next orbital correction snaps toward the new target using
    /// direction/radius data that belonged to the old one — this is what was causing the
    /// teleport bug) and notifying anything with its own independent reference to the enemy.
    private void SetTarget(Transform newTarget)
    {
        target = newTarget;
        SyncOrbitState();
        TargetChanged?.Invoke(target);
    }

    /// Detects a flick as an EDGE — the stick crossing the threshold this frame when it
    /// wasn't past it last frame — rather than firing continuously while held past it.
    /// Threshold/cooldown come from the active group, not a fixed value here, since
    /// different enemies may reasonably want different flick feel.
    private void TickTargetSwitch()
    {
        if (activeGroup == null)
        {
            Debug.Log($"[LockOn] TickTargetSwitch: activeGroup is NULL — flick switching cannot work until a TargetGroup is found. Frame {Time.frameCount}");
            return;
        }
        if (targetSwitchCooldownTimer > 0f) targetSwitchCooldownTimer -= Time.deltaTime;

        float lookX = input.LookInput.x;
        bool wasBelow = Mathf.Abs(prevLookX) < activeGroup.FlickThreshold;
        bool isAbove = Mathf.Abs(lookX) >= activeGroup.FlickThreshold;

        if (Mathf.Abs(lookX) > 0.05f) // only log when the stick is actually being moved, to avoid spam at rest
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
                Debug.Log($"[LockOn] Flick detected ({(direction > 0 ? "right" : "left")}) but GetTargetInDirection found nothing — check TargetGroup's Secondaries list is populated");
            }
            targetSwitchCooldownTimer = activeGroup.FlickCooldown;
        }

        prevLookX = lookX;
    }

    /// 0 at the instant a dash launches, 1 once it's fully decelerated — based on how far
    /// the current speed has decayed from its launch value, not on animation clip time,
    /// since the dash's actual duration varies with Reinforcement's speed multiplier.
    private float DashProgress01
    {
        get
        {
            if (dashIsOrbital)
            {
                if (dashLaunchAngularSpeed <= 0.0001f) return 1f;
                return 1f - Mathf.Clamp01(Mathf.Abs(dashOrbitAngularSpeed) / dashLaunchAngularSpeed);
            }
            if (dashIsFixedDistance)
            {
                if (inRangeDashDistance <= 0.0001f) return 1f;
                return 1f - Mathf.Clamp01(dashFixedRemaining / inRangeDashDistance);
            }
            if (dashLaunchSpeed <= 0.0001f) return 1f;
            return 1f - Mathf.Clamp01(velocity.magnitude / dashLaunchSpeed);
        }
    }

    [Header("Hitstun")]
    [Tooltip("How quickly knockback velocity decays back to zero during hitstun.")]
    [SerializeField] private float knockbackDecay = 15f;
    private float hitstunTimer;

    /// Stub hook for a future Sidestep system — not implemented yet, but this is the
    /// victim-side interrupt point it would check. A real Sidestep would query something
    /// like this to know whether the character is currently vulnerable/interruptible.
    public bool IsVulnerableToSidestep => action == ActionState.Locomotion;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4.5f; // run speed
    [SerializeField] private float dirBlendTime = 0.1f; // eases DirX/DirZ instead of snapping instantly

    [Header("Walk / Run")]
    [Tooltip("Stick magnitude (0-1) below which there's no movement at all.")]
    [Range(0f, 1f)]
    [SerializeField] private float walkThreshold = 0.15f;
    [Tooltip("Stick magnitude (0-1) at or above which movement becomes running instead of walking.")]
    [Range(0f, 1f)]
    [SerializeField] private float runThreshold = 0.35f;
    [Tooltip("Fixed walk speed — NOT affected by Reinforcement, unlike run (moveSpeed).")]
    [SerializeField] private float walkSpeed = 2f;

    [Header("Sprint Buildup")]
    [Tooltip("How long you must continuously hold run-level input before the speed bonus starts building. Resets instantly the moment you drop below the run threshold.")]
    [SerializeField] private float sprintBuildupDelay = 1f;
    [Tooltip("How long it takes to ramp from 0 to the max bonus, once buildup starts.")]
    [SerializeField] private float sprintRampDuration = 2f;
    [Tooltip("Extra speed ADDED on top of moveSpeed * Reinforcement's multiplier once fully built up — not a replacement value.")]
    [SerializeField] private float sprintMaxBonus = 5f;
    [Tooltip("Animation playback speed multiplier at max sprint bonus (1 = unaffected). Only affects Animator states with Speed Multiplier bound to LocomotionSpeedMultiplier — see setup instructions.")]
    [SerializeField] private float sprintMaxAnimSpeedMultiplier = 1.2f;
    [Tooltip("Off for now — sprint bonus is blocked entirely while Reinforcement is actively boosting move speed. Flip on to allow it later.")]
    [SerializeField] private bool sprintBonusAppliesWhileReinforced = false;
    private float sprintHoldTimer;

    [Header("Movement-Direction Facing (Locomotion/RCT)")]
    [Tooltip("Degrees from straight forward within which input counts as Forward (Backward " +
             "covers the rest). 90 splits the circle evenly in half — there's no separate " +
             "Strafe zone anymore, so no need to leave room for one.")]
    [Range(10f, 90f)]
    [SerializeField] private float forwardBackThreshold = 90f;
    [Tooltip("Extra angular buffer before switching category again once already in one — " +
             "prevents rapid flip-flopping between animations when input hovers near the boundary.")]
    [SerializeField] private float categoryHysteresis = 5f;
    [Tooltip("Degrees/sec the character rotates to face the movement direction while moving. " +
             "Separate from FaceTarget, which resumes once movement stops.")]
    [SerializeField] private float movementFacingTurnSpeed = 720f;
    [Tooltip("How quickly the rotation TARGET direction itself is smoothed — small values " +
             "just filter out stick noise at low magnitude without adding noticeable lag to " +
             "intentional turns. Separate from Movement Facing Turn Speed, which caps how " +
             "fast the character can actually rotate.")]
    [SerializeField] private float rotationDirSmoothTime = 0.08f;
    [Tooltip("How long the DirX/DirZ blend tree input takes to glide between category points " +
             "(Forward/Backward/Idle) when the category changes. This is the actual " +
             "smoothness dial — shorter feels snappier, longer feels floatier.")]
    [SerializeField] private float locomotionBlendSmoothTime = 0.12f;

    [Tooltip("How quickly currentSpeed eases between walk and run (or from idle into either) — separate from moveAccelTime, which still governs Guard's speed easing.")]
    [SerializeField] private float walkRunTransitionTime = 0.15f;
    [Tooltip("Seconds to accelerate/decelerate to the target move speed. This is what actually smooths " +
             "the handoff back into Locomotion after Jump/Dash/Landing — without it, movement snaps to " +
             "full speed instantly the moment there's input, no matter how smooth the state before it was.")]
    [SerializeField] private float moveAccelTime = 0.15f;
    private Vector3 velocity; // shared by Locomotion, Dash, and Jump's horizontal component — one value, no handoff needed
    private float currentSpeed;
    private float speedVelocityRef;

    [Header("Jump")]
    [SerializeField] private float jumpHeight = 2f;
    [SerializeField] private float timeToApex = 0.35f;
    [SerializeField] private float timeToFall = 0.28f;
    [SerializeField] private float airHorizontalSpeed = 4f;
    [Tooltip("Seconds after landing before Jump can be used again.")]
    [SerializeField] private float jumpCooldown = 0.5f;
    private float jumpCooldownTimer;
    [Tooltip("Seconds after a dash ends before Jump can be used, letting the Dash→Locomotion " +
             "animator transition finish before cutting to Jump. Only applies to jumping from " +
             "settled Locomotion — jumping to directly interrupt an in-progress dash is unaffected.")]
    [SerializeField] private float jumpLockoutAfterDash = 0.15f;
    private float jumpLockoutTimer;
    [Tooltip("Grace window after walking off a ledge where Jump still works, as if still grounded.")]
    [SerializeField] private float coyoteTime = 0.1f;
    private float airborneTimer;
    [Tooltip("Window before landing where a Jump press is remembered and fires automatically the " +
             "instant you land, instead of requiring frame-perfect timing.")]
    [SerializeField] private float jumpBufferWindow = 0.15f;
    private float jumpBufferTimer;
    [Tooltip("Units/sec² horizontal velocity can be steered by input while airborne. 0 = fully locked " +
             "momentum (current behavior) — raise this later for characters that need real air control.")]
    [SerializeField] private float airControl = 0f;

    [Header("Combat Range")]
    [Tooltip("Distance from the target within which Dash becomes an orbiting side-step " +
             "instead of a straight-line dash, and Jump is disabled entirely.")]
    [SerializeField] private float combatRange = 5f;
    [Tooltip("Degrees a lateral dash sweeps around the target.")]
    [SerializeField] private float sideDodgeOrbitAngle = 45f;
    [Tooltip("OFF (Hard Cap): sweeps with the same natural feel as a straight dash " +
             "(speed/decel derived from dashSpeed/dashDeceleration/radius), but stops early " +
             "if the angle above is reached first — duration can differ slightly from a " +
             "straight dash. ON (Solve From Duration): speed/decel are computed backward so " +
             "the sweep reaches exactly that angle AND finishes in the same duration as a " +
             "straight dash, guaranteed — but the sweep's speed is fixed by that math " +
             "instead of coming from dashSpeed's natural feel.")]
    [SerializeField] private bool dashAngleSolveFromDuration = false;
    [Tooltip("Fixed distance an in-range forward/back dash travels — deliberately separate " +
             "from dashSpeed/dashDeceleration (the outside-range physics dash), since a " +
             "short, predictable distance is what makes sense at close quarters. A forward " +
             "dash still can't overshoot Attack Range, same as everything else that " +
             "approaches the target.")]
    [SerializeField] private float inRangeDashDistance = 2f;
    [Tooltip("Constant speed for the in-range forward/back dash — not deceleration-based, so this is a real m/s value, not a launch speed.")]
    [SerializeField] private float inRangeDashSpeed = 8f;
    [Tooltip("The ideal distance from the target for performing attacks — also a hard floor " +
             "for ALL movement (walking, dashing, jumping, attack forward steps), enforced " +
             "every frame, so nothing can ever cross through it. Attack forward steps work " +
             "toward this distance instead of a fixed distance, naturally getting reduced " +
             "or skipped if already this close.")]
    [SerializeField] private float attackRange = 1f;

    public bool IsInCombatRange() => target != null && Vector3.Distance(transform.position, target.position) <= combatRange;

    [Header("Dash")]
    [SerializeField] private float dashSpeed = 10f;
    [Tooltip("Units/sec² the dash decelerates by — same idea as Jump's gravity. Higher = shorter, " +
             "snappier dash. Lower = longer, more gradual. The dash ends naturally when velocity hits 0.")]
    [SerializeField] private float dashDeceleration = 25f;
    [Tooltip("Window after pressing Dash to flick a direction. Movement is frozen during this window. " +
             "If it expires with no direction given, the dash fires forward.")]
    [SerializeField] private float dashAimWindow = 0.15f;
    [Tooltip("Seconds after a dash fully stops before Dash can be used again.")]
    [SerializeField] private float dashCooldown = 0.5f;
    [Tooltip("Fraction of the dash's deceleration (0-1) that must complete before an attack " +
             "can interrupt it. 0 = interruptible immediately at launch. 1 = only " +
             "interruptible once fully decelerated (functionally never, since it'd already " +
             "be ending on its own by then).")]
    [Range(0f, 1f)]
    [SerializeField] private float dashAttackCancelWindow = 0.7f;
    [Tooltip("Seconds after landing recovery finishes before Dash can be used, letting the " +
             "Land→Locomotion animator transition finish before cutting to Dash. Only applies to " +
             "dashing from settled Locomotion — dashing to directly interrupt Landing is unaffected.")]
    [SerializeField] private float dashLockoutAfterLanding = 0.15f;
    private float dashLockoutTimer;
    [Tooltip("Window where a Dash press during cooldown is remembered and fires automatically once " +
             "cooldown clears, instead of requiring the player to re-time a second press.")]
    [SerializeField] private float dashBufferWindow = 0.15f;
    private float dashBufferTimer;
    private float dashAimTimer;
    private float dashCooldownTimer;
    private float dashLaunchMultiplier = 1f;
    private bool dashIsOrbital;
    private bool dashIsFixedDistance; // in-range forward/back — fixed distance/speed, not deceleration-based
    private float dashFixedRemaining;
    private float dashLaunchSpeed; // captured at launch — straight dash's initial speed magnitude, for progress tracking
    private float dashLaunchAngularSpeed; // captured at launch — orbital dash's initial angular speed magnitude
    private float dashOrbitAngularSpeed;
    private float dashOrbitAngleRemaining;
    private float dashOrbitAngularDecel; // only meaningful in Solve From Duration mode — fixed at launch

    [Header("Landing")]
    [SerializeField] private float landingRecoveryDuration = 0.15f;
    private float landingTimer;

    [Header("Grounding")]
    [SerializeField] private float groundedStickForce = -2f;

    [Header("Debug (temporary — remove once orbit issue is solved)")]
    [SerializeField] private float debugOrbitRadius;
    [SerializeField] private float debugActualDistance;

    private CharacterController controller;
    private Animator animator;

    private ActionState action = ActionState.Locomotion;

    private float MoveSpeedMultiplier => reinforcement != null ? reinforcement.MoveSpeedMultiplier : 1f;
    private float JumpMultiplier => reinforcement != null ? reinforcement.JumpMultiplier : 1f;
    private float DashSpeedMultiplier => reinforcement != null ? reinforcement.DashSpeedMultiplier : 1f;
    private float AirSpeedMultiplier => reinforcement != null ? reinforcement.AirSpeedMultiplier : 1f;

    // jump physics (derived)
    private float gravityUp, gravityDown, jumpVelocity;
    private float verticalVelocity;

    private static readonly int DirXHash = Animator.StringToHash("DirX");
    private static readonly int DirZHash = Animator.StringToHash("DirZ");
    private static readonly int AttackSpeedMultiplierHash = Animator.StringToHash("AttackSpeedMultiplier");
    private static readonly int LocomotionSpeedMultiplierHash = Animator.StringToHash("LocomotionSpeedMultiplier");
    private static readonly int LocomotionSpeedHash = Animator.StringToHash("LocomotionSpeed01");
    private static readonly int JumpHash = Animator.StringToHash("Jump");
    private static readonly int DashHash = Animator.StringToHash("Dash");
    [Tooltip("Exact Animator state name for the dash animation — CrossFaded into directly " +
             "on launch, guaranteeing it interrupts immediately regardless of Exit Time " +
             "settings on any bool-driven transition into it.")]
    [SerializeField] private string dashStateName = "Directional_Dash";
    [SerializeField] private float dashCrossfadeDuration = 0.05f;
    [Tooltip("Exact Animator state name for Locomotion — EnterLocomotion crossfades to this " +
             "explicitly rather than relying on graph-level transitions, so returning to " +
             "Locomotion is reliable regardless of which state was interrupted to get here.")]
    [SerializeField] private string locomotionStateName = "Locomotion_Combat";
    [SerializeField] private float locomotionCrossfadeDuration = 0.1f;
    private static readonly int GuardHash = Animator.StringToHash("Guard");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");
    private static readonly int LandingHash = Animator.StringToHash("Landing");
    private static readonly int VerticalVelocityHash = Animator.StringToHash("VerticalVelocity");

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
        RecalculateJumpPhysics();
        animator.SetFloat(AttackSpeedMultiplierHash, normalAnimSpeed); // earliest possible point — never left at the Animator's own 0 default
        animator.SetFloat(LocomotionSpeedMultiplierHash, 1f);
    }

    private void OnValidate()
    {
        RecalculateJumpPhysics();
    }

    private void OnEnable()
    {
        input.JumpEvent += OnJumpPressed;
        input.DashEvent += OnDashPressed;
        input.GuardEvent += OnGuardPressed;
        input.GuardCanceledEvent += OnGuardReleased;
        if (rct != null) rct.ActiveChanged += OnRCTActiveChanged;
        if (combo != null) combo.ActiveChanged += OnComboActiveChanged;
        if (reinforcement != null) reinforcement.TierChanged += OnReinforcementTierChanged;
        animator.SetFloat(AttackSpeedMultiplierHash, normalAnimSpeed); // initial value, in case Reinforcement never fires an early event

        // The game is always locked on — engage immediately at startup rather than
        // waiting on a button that no longer exists.
        EngageLockOn();
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

    /// Fires in sync with when stats actually apply (TierChanged is already delayed by
    /// statActivationDelay to match MoveSpeedMultiplier etc.), not the raw button press —
    /// so attack animation speed changes exactly when movement speed does. Sets an Animator
    /// PARAMETER, not animator.speed — animator.speed is global and would affect every
    /// clip (Locomotion, Guard, Dash, everything), not just attacks. This parameter only
    /// affects whichever states explicitly reference it via their own "Speed Multiplier"
    /// setting in the Animator graph — see setup instructions. Normal and High use the same
    /// value; there's no further increase for High specifically.
    private void OnReinforcementTierChanged(Reinforcement.Tier tier)
    {
        float value = tier == Reinforcement.Tier.Off ? normalAnimSpeed : reinforcement.ReinforcedAnimSpeed;
        animator.SetFloat(AttackSpeedMultiplierHash, value);
    }

    private void Update()
    {
        if (jumpCooldownTimer > 0f) jumpCooldownTimer -= Time.deltaTime;
        if (dashCooldownTimer > 0f) dashCooldownTimer -= Time.deltaTime;
        if (jumpLockoutTimer > 0f) jumpLockoutTimer -= Time.deltaTime;
        if (dashLockoutTimer > 0f) dashLockoutTimer -= Time.deltaTime;
        if (jumpBufferTimer > 0f) jumpBufferTimer -= Time.deltaTime;
        if (dashBufferTimer > 0f) dashBufferTimer -= Time.deltaTime;

        airborneTimer = controller.isGrounded ? 0f : airborneTimer + Time.deltaTime;

        if (target != null) TickTargetSwitch();

        // A buffered dash fires the instant cooldown clears, same idea as jump buffering on landing.
        if (dashBufferTimer > 0f && dashCooldownTimer <= 0f && action == ActionState.Locomotion && dashLockoutTimer <= 0f)
        {
            dashBufferTimer = 0f;
            EnterDashWindup();
        }

        switch (action)
        {
            case ActionState.Locomotion: TickLocomotion(); break;
            case ActionState.Jumping: TickJump(); break;
            case ActionState.Landing: TickLanding(); break;
            case ActionState.Guarding: TickGuard(); break;
            case ActionState.UsingRCT: TickRCT(); break;
            case ActionState.Attacking: TickAttacking(); break;
            case ActionState.Hitstun: TickHitstun(); break;
            case ActionState.DashWindup: TickDashWindup(); break;
            case ActionState.Dashing: TickDash(); break;
        }

        ClampMinDistanceToTarget();

        if (target != null)
        {
            debugOrbitRadius = orbitRadius;
            debugActualDistance = Vector3.Distance(transform.position, target.position);
        }

        animator.SetBool(GroundedHash, controller.isGrounded);
        animator.SetBool(LandingHash, action == ActionState.Landing);
    }

    /// Pushes the character back out to attackRange if anything this frame moved it
    /// closer than that — applied once, universally, regardless of which state did the moving.
    private void ClampMinDistanceToTarget()
    {
        if (target == null) return;

        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        float distance = toPlayer.magnitude;

        if (distance < attackRange && distance > 0.0001f)
        {
            Vector3 correctedPos = target.position + toPlayer.normalized * attackRange;
            Vector3 correction = correctedPos - transform.position;
            correction.y = 0f;
            controller.Move(correction + Vector3.up * groundedStickForce * Time.deltaTime);

            if (orbitInitialized) orbitRadius = attackRange; // keep authoritative state in sync
        }
    }

    /// Reduces a proposed horizontal displacement's approach-toward-target component, if
    /// any, so it can't push closer than attackRange — used by anything that moves
    /// straight toward the target (attack forward step, straight dash) so the overshoot
    /// never happens in the first place, instead of ClampMinDistanceToTarget correcting it
    /// one frame later. That one-frame-later correction is what was causing a visible
    /// "slide back" once the original motion stopped and the correction became the only
    /// remaining movement. Only the approach component is touched — lateral/perpendicular
    /// motion in the same displacement passes through untouched.
    private Vector3 ClampApproachToMinDistance(Vector3 desiredDisplacement)
    {
        if (target == null) return desiredDisplacement;

        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        float currentDistance = toPlayer.magnitude;
        if (currentDistance <= 0.0001f) return desiredDisplacement;

        Vector3 towardTarget = -toPlayer.normalized;
        float approachAmount = Vector3.Dot(desiredDisplacement, towardTarget);
        if (approachAmount <= 0f) return desiredDisplacement; // not approaching at all — nothing to clamp

        float maxApproach = Mathf.Max(0f, currentDistance - attackRange);
        if (approachAmount > maxApproach)
        {
            float excess = approachAmount - maxApproach;
            desiredDisplacement -= towardTarget * excess;
        }

        return desiredDisplacement;
    }

    // ---------------- Gizmos ----------------

    [Header("Debug")]
    [Tooltip("Shows the current lock-on target as an on-screen overlay during Play Mode.")]
    [SerializeField] private bool showLockOnDebugOverlay = true;

    private void OnGUI()
    {
        if (!showLockOnDebugOverlay) return;

        GUIStyle style = new GUIStyle { fontSize = 20, normal = { textColor = target != null ? Color.yellow : Color.white } };
        string text = $"Target: {(target != null ? target.name : "none")}";
        GUI.Label(new Rect(10, 10, 400, 60), text, style);
    }

    private void OnDrawGizmos()
    {
        if (target == null) return;

        Gizmos.color = Color.cyan;
        DrawGizmoCircle(target.position, combatRange, 48);

        Gizmos.color = Color.red;
        DrawGizmoCircle(target.position, attackRange, 32);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(target.position + Vector3.up * 1.8f, 0.3f);
    }

    private void DrawGizmoCircle(Vector3 center, float radius, int segments)
    {
        if (radius <= 0f) return;

        Vector3 prevPoint = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * 360f * Mathf.Deg2Rad;
            Vector3 nextPoint = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prevPoint, nextPoint);
            prevPoint = nextPoint;
        }
    }
    // ---------------- Locomotion ----------------

    private void TickLocomotion()
    {
        Vector2 mv = input.MoveInput;
        bool hasInput = GetLocomotionMovement(mv, out Vector3 continuousDir, out float rawAngleDeg, out float speedBlend01, out float targetSpeed);

        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, mv.magnitude); // determine category before deciding rotate direction

        if (hasInput)
        {
            currentSpeed = Mathf.SmoothDamp(currentSpeed, targetSpeed, ref speedVelocityRef, walkRunTransitionTime);
            velocity = continuousDir * currentSpeed; // always the current instantaneous tangent — never smoothed/lagged, which is what keeps orbiting a true circle

            // Forward faces the travel direction directly — this now covers the full
            // left/right range too, since there's no separate Strafe pose anymore; a pure
            // sideways input just rotates a full 90° to face it, same mechanism a
            // forward-diagonal already used. Backward faces the OPPOSITE of travel — pure
            // straight-back is 180° from normal forward, so its opposite is 0°, meaning no
            // rotation at all (stays facing the target, a true backpedal); a
            // backward-diagonal's opposite is offset by exactly the diagonal amount from
            // that baseline, which is the "twist by the diagonal angle" effect.
            if (currentAnimCategory == LocomotionAnimCategory.Forward) RotateTowardsMovementDirection(continuousDir);
            else RotateTowardsMovementDirection(-continuousDir); // Backward
        }
        else
        {
            // Instant stop on release — SmoothDamp toward zero would otherwise keep the
            // character visibly sliding for ~moveAccelTime after Idle pose already shows.
            currentSpeed = 0f;
            speedVelocityRef = 0f;
            velocity = Vector3.zero;
            FaceTargetSmoothed(); // idle — resume facing the target, smoothed to avoid a snap
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
    /// snapping instantly — used for Locomotion/RCT's Idle case, so releasing input doesn't
    /// produce an instant rotation snap. FaceTarget itself stays instant everywhere else
    /// (Guard, Dash, Attacking) — this doesn't touch those.
    private void FaceTargetSmoothed()
    {
        if (target == null) return;
        Vector3 dir = target.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f) RotateTowardsMovementDirection(dir.normalized);
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

    /// Same orbit-aware basis Locomotion/RCT use for their own movement resolution — keeps
    /// Dash's 4-directional snap consistent with the same verified-correct reference frame
    /// instead of a separately-guessed convention.
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
    private bool GetLocomotionMovement(Vector2 mv, out Vector3 continuousDir, out float rawAngleDeg, out float speedBlend01, out float targetSpeed)
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
            sprintHoldTimer = 0f; // idle must reset the ramp-up too, not just the anim speed multiplier below — otherwise sprint resumes wherever it left off instead of ramping fresh
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

            // Extra hysteresis near the walk threshold, where a stick barely pushed is most
            // susceptible to angle noise (small physical jitter is a much bigger fraction of
            // a small signal than a large one). Fades back to normal once magnitude is
            // confidently past the threshold, where the angle is reliable again.
            float noiseRange = Mathf.Max(0.0001f, runThreshold - walkThreshold);
            float noiseFactor = 1f - Mathf.Clamp01((magnitude - walkThreshold) / noiseRange);
            float effectiveHysteresis = categoryHysteresis * (1f + noiseFactor * 2f); // up to 3x wider right at the threshold

            float threshold = forwardBackThreshold;
            if (currentAnimCategory == LocomotionAnimCategory.Forward) threshold += effectiveHysteresis;
            else if (currentAnimCategory == LocomotionAnimCategory.Backward) threshold -= effectiveHysteresis;

            desired = absAngle <= threshold ? LocomotionAnimCategory.Forward : LocomotionAnimCategory.Backward;
        }

        currentAnimCategory = desired;

        // The blend tree only ever gets pulled toward one of these fixed points — never the
        // raw stick angle — so it can't linger blending at an arbitrary diagonal. SmoothDamp
        // glides it between points on a category change instead of jumping instantly.
        Vector2 targetPoint = desired switch
        {
            LocomotionAnimCategory.Forward => new Vector2(0f, 1f),
            LocomotionAnimCategory.Backward => new Vector2(0f, -1f),
            _ => Vector2.zero // Idle
        };

        locomotionBlendInput = Vector2.SmoothDamp(locomotionBlendInput, targetPoint, ref locomotionBlendVelocity, locomotionBlendSmoothTime);
        animator.SetFloat(DirXHash, locomotionBlendInput.x);
        animator.SetFloat(DirZHash, locomotionBlendInput.y);
    }
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
        // Dash's fixed-distance and straight-line branches (unlike the orbital one) move the
        // character with raw controller.Move(), never touching orbitDirection/orbitRadius —
        // and jumping can interrupt a dash directly (fromDashInterrupt in OnJumpPressed),
        // skipping EndDash/EnterLocomotion, which is normally what resyncs this. Without
        // this, GetOrbitAwareFourDirection below and every subsequent frame's
        // ApplyOrbitalCorrection in TickJump would use stale, pre-dash orbit data — the
        // same root cause as the earlier Landing teleport-back bug, just reached via a
        // different interrupt path this time.
        SyncOrbitState();

        RecalculateJumpPhysics(); // pick up any runtime inspector tweaks before this jump
        verticalVelocity = jumpVelocity;

        Vector3 dir = GetOrbitAwareFourDirection(input.MoveInput, out int snappedX, out int snappedZ);
        velocity = dir.normalized * airHorizontalSpeed * AirSpeedMultiplier;
        animator.SetFloat(DirXHash, snappedX);
        animator.SetFloat(DirZHash, snappedZ);

        animator.SetBool(DashHash, false);
        dashIsOrbital = false; // defensive — jump interrupting an orbital dash shouldn't be reachable, but cheap to guard against
        dashIsFixedDistance = false;
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
        Vector3 horizontalDisplacement = ApplyOrbitalCorrection(velocity * Time.deltaTime);
        Vector3 verticalDisplacement = Vector3.up * verticalVelocity * Time.deltaTime;
        controller.Move(horizontalDisplacement + verticalDisplacement);

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

    private void EnterLocomotion(bool crossfade = true)
    {
        action = ActionState.Locomotion;
        if (crossfade && !string.IsNullOrEmpty(locomotionStateName))
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

        bool hasInput = GetLocomotionMovement(input.MoveInput, out _, out float rawAngleDeg, out float speedBlend01, out _);
        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, input.MoveInput.magnitude);
        animator.SetFloat(LocomotionSpeedHash, speedBlend01, dirBlendTime, Time.deltaTime);
    }

    private void TickLanding()
    {
        // Movement input cuts the recovery short instantly — the ANIMATION still transitions
        // via the native Land -> Locomotion_Combat graph transition (its own set duration),
        // not a code-forced crossfade. Code-level state (action, movement) switches to
        // Locomotion right away either way.
        if (input.MoveInput.sqrMagnitude > 0.01f)
        {
            EnterLocomotion(crossfade: false);
            dashLockoutTimer = dashLockoutAfterLanding;
            return;
        }

        landingTimer -= Time.deltaTime;
        if (landingTimer <= 0f)
        {
            EnterLocomotion(crossfade: false);
            dashLockoutTimer = dashLockoutAfterLanding;
        }
    }

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

        animator.SetFloat(DirXHash, snappedX);
        animator.SetFloat(DirZHash, snappedZ);

        action = ActionState.Dashing;
        dashLaunchMultiplier = DashSpeedMultiplier;

        // Within combat range: lateral becomes a true orbit sweep (unchanged); forward/back
        // becomes a fixed distance/speed straight-line dash of its own — deliberately NOT
        // the same physics-deceleration dash used outside combat range, since a short,
        // controlled, predictable distance is what makes sense at close quarters (and
        // ClampApproachToMinDistance still prevents a forward one from overshooting
        // attackRange). Outside combat range, forward/back/lateral all stay the existing
        // physics-deceleration straight dash, unchanged.
        dashIsOrbital = nearTarget && snappedX != 0 && target != null;
        dashIsFixedDistance = nearTarget && snappedX == 0 && target != null;

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
        else if (dashIsFixedDistance)
        {
            dashFixedRemaining = inRangeDashDistance;
            velocity = dashDir * inRangeDashSpeed * dashLaunchMultiplier;
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
        else if (dashIsFixedDistance)
        {
            float step = Mathf.Min(inRangeDashSpeed * dashLaunchMultiplier * Time.deltaTime, dashFixedRemaining);
            Vector3 horizontal = ClampApproachToMinDistance(velocity.normalized * step);
            controller.Move(horizontal + Vector3.up * groundedStickForce * Time.deltaTime);
            dashFixedRemaining -= step;
            if (dashFixedRemaining <= 0f)
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
        dashIsFixedDistance = false;
        EnterLocomotion();
        animator.SetBool(DashHash, false);
        dashCooldownTimer = dashCooldown;
        jumpLockoutTimer = jumpLockoutAfterDash;
    }

    // ---------------- Guard ----------------

    [Tooltip("Damage reduction while actively guarding. Higher = blocks more damage. " +
             "0 = no extra reduction, 1 = fully blocks.")]
    [Range(0f, 1f)]
    [SerializeField] private float guardDamageReduction = 0.5f;

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

    private void TickGuard()
    {
        FaceTarget(); // still tracks the opponent, same as every other state

        // Fully static hold now — no movement at all, single Idle Guard clip, no blend
        // tree. Reinforcement only ever affected guard's damage reduction, never movement,
        // so nothing about that changes here.
        velocity = Vector3.zero;
        currentSpeed = 0f;
        speedVelocityRef = 0f;
        Move(velocity);
    }
    // ---------------- Reverse Cursed Technique ----------------

    /// Reacts to ReverseCursedTechnique's own activation — this is the only thing
    /// PlayerController does for RCT: lock movement while it's active, unlock when it isn't.
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
        bool hasInput = GetLocomotionMovement(mv, out Vector3 continuousDir, out float rawAngleDeg, out float speedBlend01, out float targetSpeed);

        UpdateLocomotionAnimCategory(hasInput, rawAngleDeg, mv.magnitude);

        if (hasInput)
        {
            currentSpeed = Mathf.SmoothDamp(currentSpeed, targetSpeed, ref speedVelocityRef, walkRunTransitionTime);
            velocity = continuousDir * currentSpeed;

            if (currentAnimCategory == LocomotionAnimCategory.Forward) RotateTowardsMovementDirection(continuousDir);
            else RotateTowardsMovementDirection(-continuousDir); // Backward
        }
        else
        {
            currentSpeed = 0f;
            speedVelocityRef = 0f;
            velocity = Vector3.zero;
            FaceTargetSmoothed(); // idle
        }

        Move(velocity);

        animator.SetFloat(LocomotionSpeedHash, speedBlend01, dirBlendTime, Time.deltaTime);
    }

    // ---------------- Combat ----------------

    /// Reacts to ComboController's own activation — this is the only thing PlayerController
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
                dashIsFixedDistance = false;
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
        bool attackHasInput = GetLocomotionMovement(input.MoveInput, out _, out float attackRawAngle, out _, out _);
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
    // ---------------- Helpers ----------------

    // Authoritative orbit state — evolved purely through rotation, never re-derived from
    // transform.position each frame. This is what actually fixes the drift: rotating a
    // vector is mathematically exact (never changes its length), while re-reading position
    // after CharacterController.Move() inherits whatever small collision-sweep imprecision
    // happened that frame, and that error was compounding frame over frame.
    private Vector3 orbitDirection = Vector3.forward; // world-space, points from target toward player
    private float orbitRadius;
    private bool orbitInitialized;
    private enum LocomotionAnimCategory { Idle, Forward, Backward }
    private LocomotionAnimCategory currentAnimCategory = LocomotionAnimCategory.Idle;
    private Vector2 locomotionBlendInput; // smoothly damped toward whichever category's fixed point is current — never sits at an arbitrary diagonal
    private Vector2 locomotionBlendVelocity;

    /// Re-anchors the orbit state from the actual current position. Call this at any point
    /// where the character may have moved in a way this system didn't track (entering
    /// Locomotion from Dash/Jump/Landing/Guard/RCT) — NOT every frame, since that would
    /// reintroduce the exact drift this system exists to prevent.
    private void SyncOrbitState()
    {
        if (target == null) return;
        Vector3 toPlayer = transform.position - target.position;
        toPlayer.y = 0f;
        orbitRadius = toPlayer.magnitude;
        orbitDirection = orbitRadius > 0.0001f ? toPlayer.normalized : transform.forward;
        orbitInitialized = true;
    }

    /// Applies orbital correction to a horizontal displacement if a target exists — shared
    /// by Move() (which adds groundedStickForce on top, appropriate while grounded) and
    /// TickJump (which handles its own vertical velocity from gravity/jump physics instead).
    private Vector3 ApplyOrbitalCorrection(Vector3 horizontalDisplacement)
    {
        if (target == null) return horizontalDisplacement;
        if (!orbitInitialized) SyncOrbitState();
        return OrbitalDisplacement(horizontalDisplacement);
    }

    private void Move(Vector3 horizontal)
    {
        Vector3 displacement = ApplyOrbitalCorrection(horizontal * Time.deltaTime);
        controller.Move(displacement + Vector3.up * groundedStickForce * Time.deltaTime);
    }

    /// Given a desired horizontal displacement for this frame, evolves the authoritative
    /// orbitDirection/orbitRadius (radial input changes radius directly; tangential input
    /// rotates orbitDirection by a real angular step — arc length divided by radius) and
    /// returns the displacement needed to move the actual character toward that state.
    private Vector3 OrbitalDisplacement(Vector3 desiredDisplacement)
    {
        Vector3 radialDir = orbitDirection;
        Vector3 tangentDir = Vector3.Cross(Vector3.up, radialDir);

        float radialAmount = Vector3.Dot(desiredDisplacement, radialDir);
        float tangentAmount = Vector3.Dot(desiredDisplacement, tangentDir);

        orbitRadius = Mathf.Max(0.01f, orbitRadius + radialAmount);

        if (Mathf.Abs(tangentAmount) > 0.0001f)
        {
            float angularStep = (tangentAmount / orbitRadius) * Mathf.Rad2Deg;
            orbitDirection = (Quaternion.AngleAxis(angularStep, Vector3.up) * orbitDirection).normalized;
        }

        Vector3 desiredPosition = target.position + orbitDirection * orbitRadius;
        Vector3 correction = desiredPosition - transform.position;
        correction.y = 0f;
        return correction;
    }
}