using UnityEngine;

/// Movement while ComboController runs a combo: face the target and play each move's
/// forward step. ComboController owns the attack itself.
public class AttackState : PlayerState
{
    private Vector3 stepDirection;
    private float stepRemaining;
    private float stepSpeed;

    public AttackState(PlayerController p) : base(p) { }

    /// Called when a move starts. Direction is captured once so the step can't drift.
    public void BeginStep(float distance, float duration)
    {
        stepRemaining = 0f;
        Transform target = p.Target;
        if (target == null || duration <= 0f || distance <= 0f) return;

        Vector3 dir = target.position - p.transform.position;
        dir.y = 0f;
        stepDirection = dir.sqrMagnitude > 0.0001f ? dir.normalized : p.transform.forward;
        stepRemaining = distance;
        stepSpeed = distance / duration;
    }

    public override void Tick()
    {
        Motor.FaceTarget();

        if (stepRemaining > 0f)
        {
            float step = Mathf.Min(stepSpeed * Time.deltaTime, stepRemaining);
            Motor.MoveStraightClamped(stepDirection * step);
            stepRemaining -= step;
        }

        p.Locomotion.TrackInputWhileBusy();
    }
}
