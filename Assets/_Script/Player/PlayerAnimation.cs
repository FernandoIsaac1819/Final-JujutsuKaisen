using UnityEngine;

/// The only thing in the player that writes to the Animator. States say what to play;
/// this turns it into one crossfade (which state) plus parameters (blending within it).
/// Attacks are the exception: ComboController crossfades into its own move states.
public class PlayerAnimation
{
    private static readonly int DirX = Animator.StringToHash("DirX");
    private static readonly int DirZ = Animator.StringToHash("DirZ");
    private static readonly int MoveAmount = Animator.StringToHash("MoveAmount");
    private static readonly int WalkRun = Animator.StringToHash("LocomotionSpeed01");
    private static readonly int RunAnimSpeed = Animator.StringToHash("LocomotionSpeedMultiplier");
    private static readonly int AttackSpeed = Animator.StringToHash("AttackSpeedMultiplier");

    private readonly Animator animator;
    private readonly AnimationSettings s;

    public PlayerAnimation(Animator animator, AnimationSettings settings)
    {
        this.animator = animator;
        s = settings;
    }

    // ---- Which state plays ----

    public void PlayLocomotion(float fade) => CrossFade(s.locomotionState, fade);
    public void PlayLocomotion() => PlayLocomotion(s.locomotionFade);
    public void PlayFall() => CrossFade(s.fallState, s.fallFade);
    public void PlayLand() => CrossFade(s.landState, s.landFade);
    public void PlayGuard() => CrossFade(s.guardState, s.guardFade);

    public float LocomotionFromLandFade => s.locomotionFromLandFade;
    public float LocomotionFromGuardFade => s.locomotionFromGuardFade;

    /// Directional states blend their clips by DirX/DirZ (-1/0/1).
    public void PlayJump(int dirX, int dirZ)
    {
        SetDirection(dirX, dirZ);
        CrossFade(s.jumpState, s.jumpFade);
    }

    public void PlayDash(int dirX, int dirZ)
    {
        SetDirection(dirX, dirZ);
        CrossFade(s.dashState, s.dashFade);
    }

    // ---- Blending within Locomotion ----

    /// MoveAmount: 0 Idle, 1 moving. DirZ: +1 run, -1 backpedal.
    public void SetLocomotionBlend(float moveAmount, float dirZ)
    {
        animator.SetFloat(MoveAmount, moveAmount);
        animator.SetFloat(DirZ, dirZ);
    }

    public void SetMoveAmount(float moveAmount) => animator.SetFloat(MoveAmount, moveAmount);
    public float CurrentDirZ => animator.GetFloat(DirZ);

    /// 0 walk, 1 run, damped.
    public void SetWalkRun(float value, float damp) => animator.SetFloat(WalkRun, value, damp, Time.deltaTime);

    /// Playback speed of the whole Locomotion state (run sprint / Reinforcement).
    public void SetRunAnimSpeed(float speed) => animator.SetFloat(RunAnimSpeed, speed);

    /// Playback speed of attack states (Reinforcement tier).
    public void SetAttackSpeed(float speed) => animator.SetFloat(AttackSpeed, speed);

    // ---- Internals ----

    private void SetDirection(int dirX, int dirZ)
    {
        animator.SetFloat(DirX, dirX);
        animator.SetFloat(DirZ, dirZ);
    }

    private void CrossFade(string state, float fade)
    {
        if (!string.IsNullOrEmpty(state))
            animator.CrossFadeInFixedTime(state, fade);
    }
}
