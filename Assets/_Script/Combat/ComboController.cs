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
    private readonly List<bool> hitEventFired = new List<bool>(); // parallel to currentMove.HitEvents, reused across moves
    private bool inPunishWindow; // true once the LAST hit event's tolerance window has passed — the recovery frames ARE the window
    private bool advancedGateArmed; // true if the move that just finished both gates Advanced and connected (any hit event)
    private float activeMoveTimer; // real elapsed seconds since the current move started
    private readonly List<bool> stepFired = new List<bool>(); // parallel to currentMove.ForwardSteps, reused across moves
    private bool isChargingLight;   // true between a Light press and its release
    private float lightHoldTimer;
    private bool isChargingHeavy;   // true between a Heavy press and its release

    public bool IsAttacking => currentMove != null;
    public bool InPunishWindow => inPunishWindow;

    /// The latest point (normalized time) any hit event's tolerance window ends at — 0 if
    /// the move has no hit events at all, in which case the punish window opens immediately.
    private float LastHitEventWindowEnd(AttackMoveData move)
    {
        float latest = 0f;
        for (int i = 0; i < move.HitEvents.Count; i++)
        {
            AttackMoveData.HitEvent hit = move.HitEvents[i];
            float end = hit.triggerTime + hit.activeWindow / 2f;
            if (end > latest) latest = end;
        }
        return latest;
    }

    /// 1 = window just opened, 0 = about to close. Window duration is implicit — whatever's
    /// left of the current clip after its hitbox-active portion ends, not a separately
    /// authored value, so this always matches what's actually still playing on screen.
    public float PunishWindowFraction
    {
        get
        {
            if (!inPunishWindow || currentMove == null || animator == null) return 0f;

            float clipLength = animator.GetCurrentAnimatorStateInfo(0).length;
            float recoveryStart = clipLength * LastHitEventWindowEnd(currentMove);
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
        input.HeavyAttackCanceledEvent += OnHeavyReleased;
    }

    private void OnDisable()
    {
        input.LightAttackEvent -= OnLightPressed;
        input.LightAttackCanceledEvent -= OnLightReleased;
        input.HeavyAttackEvent -= OnHeavyPressed;
        input.HeavyAttackCanceledEvent -= OnHeavyReleased;
    }

    /// Both fresh-entry and mid-combo chain presses wait for release before actually
    /// firing, rather than triggering instantly — this is what lays the groundwork for a
    /// future charge attack (hold longer = stronger), even though that scaling isn't built
    /// yet; right now, holding past rushHoldThreshold before releasing is the only thing
    /// hold duration affects (Rush vs normal entry). Gate checks happen at press time (so
    /// an invalid press doesn't even start a charge) AND are re-checked fresh at release
    /// time (so a state change during the hold — e.g. the punish window closing — is
    /// handled correctly instead of firing into a now-invalid state).
    private void OnLightPressed()
    {
        if (currentMove == null)
        {
            if (playerController != null && !playerController.CanAttack)
            {
                Debug.Log($"[OnLightPressed] Frame {Time.frameCount}: REJECTED (fresh entry, CanAttack=false)");
                return;
            }
        }
        else
        {
            if (currentMove.IsAdvanced)
            {
                Debug.Log($"[OnLightPressed] Frame {Time.frameCount}: REJECTED ('{currentMove.name}' IsAdvanced)");
                return;
            }
            if (!inPunishWindow)
            {
                Debug.Log($"[OnLightPressed] Frame {Time.frameCount}: REJECTED ('{currentMove.name}' inPunishWindow=false, activeMoveTimer={activeMoveTimer:F4})");
                return;
            }
        }

        Debug.Log($"[OnLightPressed] Frame {Time.frameCount}: ACCEPTED, isChargingLight=true (currentMove='{currentMove?.name}')");
        isChargingLight = true;
        lightHoldTimer = 0f;
    }

    private void OnLightReleased()
    {
        if (!isChargingLight)
        {
            Debug.Log($"[OnLightReleased] Frame {Time.frameCount}: ignored, isChargingLight was already false");
            return;
        }
        isChargingLight = false;

        if (currentMove == null)
        {
            // Fresh entry — held past the rush threshold while outside combat range = Rush,
            // otherwise the normal entry move.
            bool rushEligible = rushAttackMove != null && playerController != null &&
                lightHoldTimer >= rushHoldThreshold && !playerController.IsInCombatRange();
            Debug.Log($"[OnLightReleased] Frame {Time.frameCount}: fresh entry, starting {(rushEligible ? "RUSH" : "normal")} move");
            StartMove(rushEligible ? rushAttackMove : lightEntryMove);
        }
        else
        {
            if (currentMove.IsAdvanced || !inPunishWindow)
            {
                Debug.Log($"[OnLightReleased] Frame {Time.frameCount}: REJECTED at release ('{currentMove.name}' IsAdvanced={currentMove.IsAdvanced} inPunishWindow={inPunishWindow}, t-relevant activeMoveTimer={activeMoveTimer:F4})");
                return;
            }

            AttackMoveData next = currentMove.NextOnLight;
            Debug.Log($"[OnLightReleased] Frame {Time.frameCount}: chaining from '{currentMove.name}' to '{next?.name}'");
            if (next != null) StartMove(next);
            else EndCombo();
        }
    }

    private void OnHeavyPressed()
    {
        if (currentMove == null)
        {
            if (playerController != null && !playerController.CanAttack)
            {
                Debug.Log($"[OnHeavyPressed] Frame {Time.frameCount}: REJECTED (fresh entry, CanAttack=false)");
                return;
            }
            if (heavyEntryMove == null) return;
        }
        else
        {
            if (currentMove.IsAdvanced)
            {
                Debug.Log($"[OnHeavyPressed] Frame {Time.frameCount}: REJECTED ('{currentMove.name}' IsAdvanced)");
                return;
            }
            if (!inPunishWindow)
            {
                Debug.Log($"[OnHeavyPressed] Frame {Time.frameCount}: REJECTED ('{currentMove.name}' inPunishWindow=false, activeMoveTimer={activeMoveTimer:F4})");
                return;
            }
        }

        Debug.Log($"[OnHeavyPressed] Frame {Time.frameCount}: ACCEPTED, isChargingHeavy=true (currentMove='{currentMove?.name}')");
        isChargingHeavy = true;
    }

    private void OnHeavyReleased()
    {
        if (!isChargingHeavy)
        {
            Debug.Log($"[OnHeavyReleased] Frame {Time.frameCount}: ignored, isChargingHeavy was already false");
            return;
        }
        isChargingHeavy = false;

        if (currentMove == null)
        {
            Debug.Log($"[OnHeavyReleased] Frame {Time.frameCount}: fresh entry, starting heavyEntryMove");
            if (heavyEntryMove != null) StartMove(heavyEntryMove);
        }
        else
        {
            if (currentMove.IsAdvanced || !inPunishWindow)
            {
                Debug.Log($"[OnHeavyReleased] Frame {Time.frameCount}: REJECTED at release ('{currentMove.name}' IsAdvanced={currentMove.IsAdvanced} inPunishWindow={inPunishWindow}, activeMoveTimer={activeMoveTimer:F4})");
                return;
            }

            AttackMoveData next = advancedGateArmed ? currentMove.AdvancedUnlock : currentMove.NextOnHeavy;
            Debug.Log($"[OnHeavyReleased] Frame {Time.frameCount}: chaining from '{currentMove.name}' to '{next?.name}'");
            if (next != null) StartMove(next);
            else EndCombo();
        }
    }

    private void StartMove(AttackMoveData move)
    {
        if (move == null) return;

        bool wasIdle = currentMove == null;
        if (!wasIdle) Debug.Log($"[StartMove] Frame {Time.frameCount}: '{currentMove.name}' -> '{move.name}' (was inPunishWindow={inPunishWindow}, activeMoveTimer was {activeMoveTimer:F4})");
        else Debug.Log($"[StartMove] Frame {Time.frameCount}: starting '{move.name}' from idle");
        currentMove = move;
        if (move.HitEvents.Count == 0)
            Debug.LogWarning($"'{move.name}' has NO Hit Events configured — its punish window will open almost immediately, since there's nothing to wait for. Add at least one Hit Event in the Inspector.", move);
        inPunishWindow = false;
        activeMoveTimer = 0f;

        hitEventFired.Clear();
        for (int i = 0; i < move.HitEvents.Count; i++) hitEventFired.Add(false);

        stepFired.Clear();
        for (int i = 0; i < move.ForwardSteps.Count; i++) stepFired.Add(false);

        if (animator != null && !string.IsNullOrEmpty(move.AnimatorStateName))
            animator.CrossFadeInFixedTime(move.AnimatorStateName, move.BlendInDuration);

        if (wasIdle) ActiveChanged?.Invoke(true);
    }

    private void Update()
    {
        // Just accumulates now — the release handler decides what to do with the duration,
        // rather than firing the instant a threshold is crossed while still held.
        if (isChargingLight) lightHoldTimer += Time.deltaTime;

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

    /// One continuous tick covering the whole lifetime of a move: hit detection during the
    /// active frames, opening the punish window the instant those active frames end (the
    /// window IS the remaining recovery animation, not a separate added wait), and finally
    /// handing off once the clip has actually finished — either back to Locomotion (Basic,
    /// nothing chained it in time) or auto-continuing (Advanced).
    private void TickActiveMove()
    {
        activeMoveTimer += Time.deltaTime;
        if (animator == null) return;

        // While still mid-crossfade, GetCurrentAnimatorStateInfo reports the SOURCE state
        // (the one being transitioned FROM), not the destination — GetNextAnimatorStateInfo
        // is what reports the incoming state during a transition. For a fresh entry the
        // source is Locomotion, harmless either way. For chaining, the source is the
        // PREVIOUS attack clip — and since a chain only happens during that previous move's
        // own punish window, its normalizedTime is already late (e.g. 0.7-0.9). Reading
        // that stale value for even a frame after StartMove() means t starts already past
        // LastHitEventWindowEnd, opening the punish window instantly — this is what was
        // letting chained moves (never fresh-entry ones) be interrupted immediately.
        // Derived from activeMoveTimer (code-controlled, reset to exactly 0 in StartMove,
        // incremented by Time.deltaTime every frame) instead of the Animator's own
        // normalizedTime — that was proving unreliable immediately after a chain, reporting
        // values like 0.65 when activeMoveTimer said only 0.01s had actually elapsed. Only
        // clip length is still read from the Animator, which is a fixed property, not
        // something that changes during a transition.
        AnimatorStateInfo info = animator.IsInTransition(0)
            ? animator.GetNextAnimatorStateInfo(0)
            : animator.GetCurrentAnimatorStateInfo(0);
        float t = info.length > 0f ? Mathf.Clamp01(activeMoveTimer / info.length) : 0f;

        for (int i = 0; i < currentMove.ForwardSteps.Count; i++)
        {
            if (stepFired[i]) continue;

            AttackMoveData.ForwardStepEntry step = currentMove.ForwardSteps[i];
            if (t < step.triggerTime) continue;

            stepFired[i] = true;
            // No combat-range gate here anymore — Attack Range naturally reduces or skips
            // this step via ClampApproachToMinDistance, already wired into TickAttacking.
            playerController?.BeginAttackStep(step.distance, step.duration);
        }

        for (int i = 0; i < currentMove.HitEvents.Count; i++)
        {
            if (hitEventFired[i]) continue;

            AttackMoveData.HitEvent hit = currentMove.HitEvents[i];
            float halfWindow = hit.activeWindow / 2f;
            if (t < hit.triggerTime - halfWindow || t > hit.triggerTime + halfWindow) continue;

            hitEventFired[i] = true;
            TryLandHit(hit);
        }

        if (!inPunishWindow && t > LastHitEventWindowEnd(currentMove))
        {
            // Hitbox frames are done, but the clip is still playing its recovery — this IS
            // the window now. A press here crossfades straight into the next move, cutting
            // off whatever recovery frames were left, which is what actually reads as
            // "flowing into the next hit" instead of a dead pause.
            inPunishWindow = true;
            Debug.Log($"[PunishWindow] Frame {Time.frameCount}: OPENED for '{currentMove.name}' at t={t:F4} (LastHitEventWindowEnd={LastHitEventWindowEnd(currentMove):F4}), activeMoveTimer={activeMoveTimer:F4}");
            bool anyHitConnected = hitEventFired.Contains(true);
            advancedGateArmed = currentMove.GatesAdvanced && anyHitConnected;
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

    private void TryLandHit(AttackMoveData.HitEvent hit)
    {
        if (targetDamageReceiver == null) return;

        Vector3 hitboxCenter = transform.position + transform.forward * hit.hitboxForwardOffset + Vector3.up * hit.hitboxHeightOffset;
        float distSqr = (targetDamageReceiver.transform.position - hitboxCenter).sqrMagnitude;
        float radius = hit.hitboxRadius;

        if (distSqr <= radius * radius)
        {
            ApplyHit(hit);
        }
    }

    private void ApplyHit(AttackMoveData.HitEvent hit)
    {
        float damageMultiplier = reinforcement != null ? reinforcement.DamageMultiplier : 1f;
        float finalDamage = hit.damage * damageMultiplier;

        bool wasParried = false;

        if (targetPlayerController != null)
        {
            PlayerController.GuardReactionResult result = targetPlayerController.EvaluateGuardTiming(
                hit.reactionWindowDuration, hit.parryThreshold, out float quality);

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
            Vector3 knockbackDir = hit.knockback switch
            {
                AttackMoveData.KnockbackType.Backward =>
                    (targetPlayerController.transform.position - transform.position).normalized,
                AttackMoveData.KnockbackType.LaunchUp => Vector3.up,
                _ => Vector3.zero
            };

            targetPlayerController.ApplyHitReaction(hit.hitstunDuration, knockbackDir * hit.knockbackForce);
        }
    }

    /// Called externally (PlayerController interrupting an attack with a dash) to force the
    /// combo to end immediately without firing ActiveChanged — the caller is responsible for
    /// whatever comes next, since it isn't necessarily returning to Locomotion.
    public void ForceEndCombo()
    {
        currentMove = null;
        inPunishWindow = false;
        advancedGateArmed = false;
    }

    private void EndCombo()
    {
        currentMove = null;
        inPunishWindow = false;
        advancedGateArmed = false;

        if (animator != null && !string.IsNullOrEmpty(locomotionStateName))
            animator.CrossFadeInFixedTime(locomotionStateName, locomotionBlendDuration);

        ActiveChanged?.Invoke(false);
    }

    private void OnDrawGizmos()
    {
        if (currentMove == null || animator == null) return;

        float t = animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;

        for (int i = 0; i < currentMove.HitEvents.Count; i++)
        {
            AttackMoveData.HitEvent hit = currentMove.HitEvents[i];
            float halfWindow = hit.activeWindow / 2f;
            bool hitboxActive = t >= hit.triggerTime - halfWindow && t <= hit.triggerTime + halfWindow;

            Gizmos.color = hitboxActive ? Color.red : Color.white;
            Vector3 hitboxCenter = transform.position + transform.forward * hit.hitboxForwardOffset + Vector3.up * hit.hitboxHeightOffset;
            Gizmos.DrawWireSphere(hitboxCenter, hit.hitboxRadius);
        }
    }
}