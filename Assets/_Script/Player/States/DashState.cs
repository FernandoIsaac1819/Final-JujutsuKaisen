using UnityEngine;

/// The dash, aimed by the stick at launch (snapped to 4 directions). In combat range a
/// sideways dash sweeps around the target and forward/back is a short fixed dash (still
/// can't pass attackRange). Outside combat range it's a decelerating straight dash.
public class DashState : PlayerState
{
    private enum Kind { Straight, Orbital, FixedDistance }

    private Kind kind;
    private float launchMultiplier = 1f;
    private float launchSpeed;          // straight
    private float fixedRemaining;       // fixed distance
    private float angularSpeed;         // orbital
    private float launchAngularSpeed;
    private float angleRemaining;
    private float angularDecel;

    public DashState(PlayerController p) : base(p) { }

    /// 0 at launch, 1 when finished. Based on remaining speed/distance, not clip time,
    /// since Reinforcement changes how long a dash lasts.
    public float Progress01 => kind switch
    {
        Kind.Orbital => launchAngularSpeed <= 0.0001f ? 1f : 1f - Mathf.Clamp01(Mathf.Abs(angularSpeed) / launchAngularSpeed),
        Kind.FixedDistance => Dash.inRangeDistance <= 0.0001f ? 1f : 1f - Mathf.Clamp01(fixedRemaining / Dash.inRangeDistance),
        _ => launchSpeed <= 0.0001f ? 1f : 1f - Mathf.Clamp01(p.Velocity.magnitude / launchSpeed),
    };

    public override void Enter()
    {
        Vector3 dashDir = SnapToFourDirections(Input.MoveInput, out int snappedX, out int snappedZ);
        Animator.SetFloat(PlayerAnimParams.DirX, snappedX);
        Animator.SetFloat(PlayerAnimParams.DirZ, snappedZ);

        bool nearTarget = p.IsInCombatRange();
        kind = !nearTarget ? Kind.Straight : snappedX != 0 ? Kind.Orbital : Kind.FixedDistance;
        launchMultiplier = p.DashSpeedMultiplier;

        switch (kind)
        {
            case Kind.Orbital:
                Motor.TryGetOrbitDirection(out Vector3 toPlayer);
                float sign = Mathf.Sign(Vector3.Dot(dashDir, Vector3.Cross(Vector3.up, toPlayer)));
                angleRemaining = Dash.sideDodgeAngle * sign;

                if (Dash.solveAngleFromDuration)
                {
                    // Exactly sideDodgeAngle, in exactly the straight dash's duration.
                    float baseDuration = Dash.deceleration > 0f ? Dash.speed / Dash.deceleration : 0.01f;
                    float duration = Mathf.Max(0.01f, baseDuration / Mathf.Max(launchMultiplier, 0.01f));
                    angularSpeed = (2f * Dash.sideDodgeAngle / duration) * sign;
                    angularDecel = Mathf.Abs(angularSpeed) / duration;
                }
                else
                {
                    // Straight dash's feel; stops early if the angle is reached first.
                    angularSpeed = (Dash.speed * launchMultiplier / Mathf.Max(Motor.OrbitRadius, 0.01f)) * Mathf.Rad2Deg * sign;
                    angularDecel = 0f; // recomputed from radius each frame
                }
                launchAngularSpeed = Mathf.Abs(angularSpeed);
                break;

            case Kind.FixedDistance:
                fixedRemaining = Dash.inRangeDistance;
                p.Velocity = dashDir * Dash.inRangeSpeed * launchMultiplier;
                break;

            default:
                p.Velocity = dashDir * Dash.speed * launchMultiplier;
                launchSpeed = p.Velocity.magnitude;
                break;
        }

        Animator.SetBool(PlayerAnimParams.Dash, true);
        p.CrossfadeToDash();
    }

    public override void Tick()
    {
        Motor.FaceTarget();

        switch (kind)
        {
            case Kind.Orbital:
                float decel = Dash.solveAngleFromDuration
                    ? angularDecel
                    : (Dash.deceleration * launchMultiplier / Mathf.Max(Motor.OrbitRadius, 0.01f)) * Mathf.Rad2Deg;
                angularSpeed = Mathf.MoveTowards(angularSpeed, 0f, decel * Time.deltaTime);

                float step = angularSpeed * Time.deltaTime;
                if (Mathf.Abs(step) > Mathf.Abs(angleRemaining))
                    step = angleRemaining; // never overshoot the angle
                angleRemaining -= step;

                Motor.RotateAroundTarget(step);
                if (Mathf.Abs(angleRemaining) <= 0.05f || Mathf.Abs(angularSpeed) <= 0.5f) Finish();
                break;

            case Kind.FixedDistance:
                float dist = Mathf.Min(Dash.inRangeSpeed * launchMultiplier * Time.deltaTime, fixedRemaining);
                Motor.MoveStraightClamped(p.Velocity.normalized * dist);
                fixedRemaining -= dist;
                if (fixedRemaining <= 0f) Finish();
                break;

            default:
                p.Velocity = Vector3.MoveTowards(p.Velocity, Vector3.zero, Dash.deceleration * launchMultiplier * Time.deltaTime);
                Motor.MoveStraightClamped(p.Velocity * Time.deltaTime);
                if (p.Velocity.sqrMagnitude <= 0.0001f) Finish();
                break;
        }
    }

    /// Runs on every way out (finished, attack cancel, jump, hit).
    public override void Exit()
    {
        p.Velocity = Vector3.zero;
        kind = Kind.Straight;
        launchSpeed = 0f;
        Animator.SetBool(PlayerAnimParams.Dash, false);
    }

    /// Starts the cooldowns that follow a completed (or attack-cancelled) dash.
    public void StartCooldowns()
    {
        Cooldowns.dashCooldown = Dash.cooldown;
        Cooldowns.jumpLockout = Jump.lockoutAfterDash;
    }

    private void Finish()
    {
        StartCooldowns();
        p.EnterLocomotion();
    }

    /// Snaps the stick to forward/back/left/right in the orbit frame.
    private Vector3 SnapToFourDirections(Vector2 mv, out int snappedX, out int snappedZ)
    {
        Motor.GetBasis(out Vector3 fwd, out Vector3 right);
        snappedX = 0;
        snappedZ = 0;
        if (mv.sqrMagnitude < 0.0001f) return Vector3.zero;

        float snappedAngle = Mathf.Round(Mathf.Atan2(mv.x, mv.y) * Mathf.Rad2Deg / 90f) * 90f;
        Vector3 dir = Quaternion.AngleAxis(snappedAngle, Vector3.up) * fwd;
        snappedZ = Mathf.RoundToInt(Vector3.Dot(dir, fwd));
        snappedX = Mathf.RoundToInt(Vector3.Dot(dir, right));
        return dir;
    }
}
