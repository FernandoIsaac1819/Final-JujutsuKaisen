using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives the combo tree: input handling, hitbox timing, hit detection, damage, and
/// handing off hitstun/knockback to the target. Fully self-contained, same shape as
/// ReverseCursedTechnique — PlayerController doesn't own any of this logic, it just reacts
/// to ActiveChanged to lock/unlock movement.
///
/// The punish window is implicit, not a separately authored duration: it's whatever's left
/// of the current clip after its hitbox-active portion ends. A well-timed press crossfades
/// straight into the next move, cutting off whatever recovery frames remained — this is
/// what makes chaining feel continuous instead of leaving a dead pause between hits.
///
/// Basic strings: mashable on pure timing, no hit required to continue. Landing a Heavy
/// that has an AdvancedUnlock assigned arms access to that Advanced string on the next
/// Heavy press. If the window expires with no input, the combo ends and returns to
/// Locomotion — Dash/normal movement become available simply because that's what
/// Locomotion already permits (no special-casing needed for that "cancel" behavior).
///
/// Advanced strings: once entered, fully automatic — no further input, no cancel option.
/// Each move auto-chains to the next via NextOnLight (NextOnHeavy is unused for Advanced
/// nodes) once its own clip finishes, which is what gives the opponent a real (if
/// attacker-uncontrolled) reaction gap between hits. Runs to whatever move has no
/// NextOnLight set, then ends.
/// </summary>
public class ComboController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private InputReader input;
    [SerializeField] private Animator animator;
    private static readonly int AttackSpeedMultiplierHash = Animator.StringToHash("AttackSpeedMultiplier");
    [SerializeField] private PlayerController playerController;
    [Tooltip("Optional. Multiplies outgoing damage the same way it multiplies everything else.")]
    [SerializeField] private Reinforcement reinforcement;
    [Tooltip("Attack clip playback speed while not reinforced. Requires each attack state's " +
             "Speed Multiplier to be set to the AttackSpeedMultiplier Animator parameter.")]
    [SerializeField] private float normalAttackAnimSpeed = 1f;
    [Tooltip("Attack clip playback speed while Reinforcement is active (Normal or High).")]
    [SerializeField] private float reinforcedAttackAnimSpeed = 1.3f;
    [Tooltip("The opponent's DamageReceiver — where hits actually get applied.")]
    [SerializeField] private DamageReceiver targetDamageReceiver;
    [Tooltip("The opponent's PlayerController — where hitstun/knockback get applied.")]
    [SerializeField] private PlayerController targetPlayerController;

    [Header("Return To Locomotion")]
    [Tooltip("Exact Animator state name to CrossFade back to once a combo ends. Must match " +
             "your Locomotion blend tree's state name.")]
    [SerializeField] private string locomotionStateName = "Locomotion_Combat";
    [SerializeField] private float locomotionBlendDuration = 0.1f;

    [Header("Combo Tree Entry Points")]
    [Tooltip("The move played on a quick Light tap when not already attacking.")]
    [SerializeField] private AttackMoveData lightEntryMove;
    [Tooltip("The move played on a fresh Heavy press when not already attacking. Leave " +
             "empty if strings can only be opened with Light.")]
    [SerializeField] private AttackMoveData heavyEntryMove;
    [Tooltip("Played instead of lightEntryMove if Light is HELD past rushHoldThreshold " +
             "while outside combat range. A quick tap never triggers this.")]
    [SerializeField] private AttackMoveData rushAttackMove;
    [Tooltip("Seconds Light must be held before Rush triggers. Only checked while outside " +
             "combat range — holding Light up close just does a slightly delayed normal tap.")]
    [SerializeField] private float rushHoldThreshold = 0.4f;

    private AttackMoveData currentMove;
    private bool currentMoveHasHit;
    private bool inPunishWindow; // true once the hitbox-active portion ends — the recovery frames ARE the window
    private bool advancedGateArmed; // true if the move that just finished both gates Advanced and connected
    private float activeMoveTimer; // real elapsed seconds since the current move started
    private readonly List<bool> stepFired = new List<bool>(); // parallel to currentMove.ForwardSteps, reused across moves
    private bool isChargingLight;   // true between a fresh Light press and the tap/hold decision
    private float lightHoldTimer;

    public bool IsAttacking => currentMove != null;
    public bool InPunishWindow => inPunishWindow;

    /// 1 = window just opened, 0 = about to close. Window duration is implicit — whatever's
    /// left of the current clip after its hitbox-active portion ends, not a separately
    /// authored value, so this always matches what's actually still playing on screen.
    public float PunishWindowFraction
    {
        get
        {
            if (!inPunishWindow || currentMove == null || animator == null) return 0f;

            float clipLength = animator.GetCurrentAnimatorStateInfo(0).length;
            float recoveryStart = clipLength * currentMove.HitboxActiveEnd;
            float recoveryDuration = clipLength - recoveryStart;
            if (recoveryDuration <= 0f) return 0f;

            float elapsedInRecovery = activeMoveTimer - recoveryStart;
            return Mathf.Clamp01(1f - (elapsedInRecovery / recoveryDuration));
        }
    }

    /// Fired only on the transition into/out of attacking entirely — not between chained
    /// moves within a string. PlayerController listens to this to enter/exit its own
    /// movement-locked state.
    public event System.Action<bool> ActiveChanged;

    private void OnEnable()
    {
        input.LightAttackEvent += OnLightPressed;
        input.LightAttackCanceledEvent += OnLightReleased;
        input.HeavyAttackEvent += OnHeavyPressed;
    }

    private void OnDisable()
    {
        input.LightAttackEvent -= OnLightPressed;
        input.LightAttackCanceledEvent -= OnLightReleased;
        input.HeavyAttackEvent -= OnHeavyPressed;
    }

    private void OnLightPressed()
    {
        if (currentMove == null)
        {
            if (playerController != null && !playerController.CanAttack) return;
            // Don't commit yet — wait to see if this becomes a tap (released early) or a
            // hold (crosses rushHoldThreshold while still down). Only the fresh-entry case
            // needs this; continuing an existing string is still fully instant, below.
            isChargingLight = true;
            lightHoldTimer = 0f;
            return;
        }

        if (currentMove.IsAdvanced) return; // Advanced ignores input entirely — fully automatic
        if (!inPunishWindow) return;         // mid-swing — input has no effect (no buffering, for now)

        AttackMoveData next = currentMove.NextOnLight;
        if (next != null) StartMove(next);
        else EndCombo();
    }

    private void OnLightReleased()
    {
        if (!isChargingLight) return; // released after Rush already triggered, or wasn't charging at all — nothing to do

        isChargingLight = false;
        StartMove(lightEntryMove); // released before the hold threshold — a genuine tap
    }

    private void OnHeavyPressed()
    {
        if (currentMove == null)
        {
            if (playerController != null && !playerController.CanAttack) return;
            if (heavyEntryMove == null) return;
            StartMove(heavyEntryMove);
            return;
        }

        if (currentMove.IsAdvanced) return;
        if (!inPunishWindow) return;

        AttackMoveData next = advancedGateArmed ? currentMove.AdvancedUnlock : currentMove.NextOnHeavy;
        if (next != null) StartMove(next);
        else EndCombo();
    }

    private void StartMove(AttackMoveData move)
    {
        if (move == null) return;

        bool wasIdle = currentMove == null;
        if (!wasIdle)
        {
            int pendingSteps = 0;
            for (int i = 0; i < stepFired.Count; i++) if (!stepFired[i]) pendingSteps++;
            Debug.Log($"[StartMove] Frame {Time.frameCount}: CHAINING from '{currentMove.name}' (had {pendingSteps} unfired steps left) to '{move.name}', activeMoveTimer={activeMoveTimer:F4}");
        }
        else
        {
            Debug.Log($"[StartMove] Frame {Time.frameCount}: starting '{move.name}' from idle");
        }
        currentMove = move;
        inPunishWindow = false;
        currentMoveHasHit = false;
        activeMoveTimer = 0f;

        stepFired.Clear();
        for (int i = 0; i < move.ForwardSteps.Count; i++) stepFired.Add(false);

        if (animator != null && !string.IsNullOrEmpty(move.AnimatorStateName))
            animator.CrossFadeInFixedTime(move.AnimatorStateName, move.BlendInDuration);

        if (wasIdle) ActiveChanged?.Invoke(true);
    }

    private void Update()
    {
        if (isChargingLight) TickLightCharge();

        if (animator != null)
        {
            float speed = (reinforcement != null && reinforcement.IsEffectivelyReinforced)
                ? reinforcedAttackAnimSpeed
                : normalAttackAnimSpeed;
            animator.SetFloat(AttackSpeedMultiplierHash, speed);
        }

        if (currentMove == null) return;

        TickActiveMove();
    }

    private void TickLightCharge()
    {
        lightHoldTimer += Time.deltaTime;
        if (lightHoldTimer < rushHoldThreshold) return;

        isChargingLight = false;

        bool rushEligible = rushAttackMove != null && playerController != null && !playerController.IsInCombatRange();
        StartMove(rushEligible ? rushAttackMove : lightEntryMove);
    }

    /// One continuous tick covering the whole lifetime of a move: hit detection during the
    /// active frames, opening the punish window the instant those active frames end (the
    /// window IS the remaining recovery animation, not a separate added wait), and finally
    /// handing off once the clip has actually finished — either back to Locomotion (Basic,
    /// nothing chained it in time) or auto-continuing (Advanced).
    private void TickActiveMove()
    {
        activeMoveTimer += Time.deltaTime;
        if (animator == null) return;

        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
        float t = info.normalizedTime % 1f; // safe to wrap — only used for the hit window check below

        for (int i = 0; i < currentMove.ForwardSteps.Count; i++)
        {
            if (stepFired[i]) continue;

            AttackMoveData.ForwardStepEntry step = currentMove.ForwardSteps[i];
            if (t < step.triggerTime) continue;

            stepFired[i] = true;
            Debug.Log($"[ForwardStep] Frame {Time.frameCount}: step {i} FIRED at t={t:F4} (triggerTime={step.triggerTime:F4}), move={currentMove.name}");
            // No combat-range gate here anymore — Attack Range naturally reduces or skips
            // this step via ClampApproachToMinDistance, already wired into TickAttacking.
            playerController?.BeginAttackStep(step.distance, step.duration);
        }

        if (!currentMoveHasHit && t >= currentMove.HitboxActiveStart && t <= currentMove.HitboxActiveEnd)
            TryLandHit();

        if (!inPunishWindow && t > currentMove.HitboxActiveEnd)
        {
            // Hitbox frames are done, but the clip is still playing its recovery — this IS
            // the window now. A press here crossfades straight into the next move, cutting
            // off whatever recovery frames were left, which is what actually reads as
            // "flowing into the next hit" instead of a dead pause.
            inPunishWindow = true;
            advancedGateArmed = currentMove.GatesAdvanced && currentMoveHasHit;
        }

        // Completion is elapsed real time vs. the clip's actual length (in seconds) — not
        // normalizedTime crossing 1, which only works if the clip's Loop Time is off.
        if (activeMoveTimer >= info.length)
        {
            if (currentMove.IsAdvanced)
            {
                // Fully automatic continuation — no input, no cancel option. NextOnLight is
                // reused as the canonical "auto next" pointer for Advanced nodes.
                AttackMoveData next = currentMove.NextOnLight;
                if (next != null) StartMove(next);
                else EndCombo();
            }
            else
            {
                EndCombo(); // nothing chained this in time — back to free movement
            }
        }
    }

    private void TryLandHit()
    {
        if (targetDamageReceiver == null) return;

        Vector3 hitboxCenter = transform.position + transform.forward * currentMove.HitboxForwardOffset + Vector3.up * currentMove.HitboxHeightOffset;
        float distSqr = (targetDamageReceiver.transform.position - hitboxCenter).sqrMagnitude;
        float radius = currentMove.HitboxRadius;

        if (distSqr <= radius * radius)
        {
            currentMoveHasHit = true;
            ApplyHit();
        }
    }

    private void ApplyHit()
    {
        float damageMultiplier = reinforcement != null ? reinforcement.DamageMultiplier : 1f;
        float finalDamage = currentMove.Damage * damageMultiplier;

        bool wasParried = false;

        if (targetPlayerController != null)
        {
            PlayerController.GuardReactionResult result = targetPlayerController.EvaluateGuardTiming(
                currentMove.ReactionWindowDuration, currentMove.ParryThreshold, out float quality);

            switch (result)
            {
                case PlayerController.GuardReactionResult.Parry:
                    wasParried = true; // zero damage, no hitstun/knockback — skipped below
                    break;

                case PlayerController.GuardReactionResult.TimedBlock:
                    // Blends from the move's own passive guardDamageReduction (quality 0,
                    // same as an untimed hold) up toward a near-full block (quality 1, just
                    // short of the parry threshold).
                    float bonusReduction = Mathf.Lerp(targetPlayerController.GuardDamageReduction, 1f, quality);
                    targetDamageReceiver.TakeDamage(finalDamage, 1f - bonusReduction);
                    break;

                default:
                    targetDamageReceiver.TakeDamage(finalDamage); // normal — automatic guard/no-guard handling inside DamageReceiver
                    break;
            }
        }
        else
        {
            targetDamageReceiver.TakeDamage(finalDamage);
        }

        if (!wasParried && targetPlayerController != null)
        {
            Vector3 knockbackDir = currentMove.Knockback switch
            {
                AttackMoveData.KnockbackType.Backward =>
                    (targetPlayerController.transform.position - transform.position).normalized,
                AttackMoveData.KnockbackType.LaunchUp => Vector3.up,
                _ => Vector3.zero
            };

            targetPlayerController.ApplyHitReaction(currentMove.HitstunDuration, knockbackDir * currentMove.KnockbackForce);
        }
    }

    /// Called externally (PlayerController interrupting an attack with a dash) to force the
    /// combo to end immediately without firing ActiveChanged — the caller is responsible for
    /// whatever comes next, since it isn't necessarily returning to Locomotion.
    public void ForceEndCombo()
    {
        currentMove = null;
        inPunishWindow = false;
        currentMoveHasHit = false;
        advancedGateArmed = false;
    }

    private void EndCombo()
    {
        currentMove = null;
        inPunishWindow = false;
        currentMoveHasHit = false;
        advancedGateArmed = false;

        if (animator != null && !string.IsNullOrEmpty(locomotionStateName))
            animator.CrossFadeInFixedTime(locomotionStateName, locomotionBlendDuration);

        ActiveChanged?.Invoke(false);
    }

    private void OnDrawGizmos()
    {
        if (currentMove == null || animator == null) return;

        float t = animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
        bool hitboxActive = t >= currentMove.HitboxActiveStart && t <= currentMove.HitboxActiveEnd;

        Gizmos.color = hitboxActive ? Color.red : new Color(1f, 0f, 1f, 0.25f);
        Vector3 hitboxCenter = transform.position + transform.forward * currentMove.HitboxForwardOffset + Vector3.up * currentMove.HitboxHeightOffset;
        Gizmos.DrawWireSphere(hitboxCenter, currentMove.HitboxRadius);
    }
}