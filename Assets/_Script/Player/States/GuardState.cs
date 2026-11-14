using UnityEngine;

/// Static guard: faces the target, keeps the orbit distance, no movement. Records when
/// it started so a hit can check for a timed block or parry.
public class GuardState : PlayerState
{
    private float pressTime = -999f;

    public GuardState(PlayerController p) : base(p) { }

    public override void Enter()
    {
        p.Velocity = Vector3.zero;
        pressTime = Time.time;
        Anim.PlayGuard();
    }

    public override void Tick()
    {
        Motor.FaceTarget();
        p.Velocity = Vector3.zero;
        Motor.Move(Vector3.zero);
    }

    /// A guard press within reactionWindow of the hit is a timed block (within
    /// parryThreshold, a parry). Quality is 1 at parry timing, 0 at the window's edge.
    public PlayerController.GuardReactionResult EvaluateTiming(float reactionWindow, float parryThreshold, out float quality)
    {
        quality = 0f;
        float timeSincePress = Time.time - pressTime;
        if (timeSincePress > reactionWindow) return PlayerController.GuardReactionResult.None;

        if (timeSincePress <= parryThreshold)
        {
            quality = 1f;
            return PlayerController.GuardReactionResult.Parry;
        }

        float range = reactionWindow - parryThreshold;
        quality = range > 0f ? 1f - Mathf.Clamp01((timeSincePress - parryThreshold) / range) : 0f;
        return PlayerController.GuardReactionResult.TimedBlock;
    }
}
