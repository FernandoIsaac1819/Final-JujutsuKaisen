using System;
using UnityEngine;

/// Moves the CharacterController around the lock-on target: orbit steps, the attackRange
/// floor, ground stick, and facing. Owned by PlayerController; holds no gameplay state.
///
/// Orbit model: only the INTENDED distance to the target is stored; direction is read
/// fresh from the real position. Drift is a radius problem, so an authoritative radius is
/// enough. EndFrame() applies the radius rule: a frame that didn't move along the orbit
/// re-reads the radius from the real distance, so straight moves (dash, attack step,
/// knockback) are picked up without any re-sync calls.
public class PlayerMotor
{
    private readonly CharacterController controller;
    private readonly Transform transform;
    private readonly PlayerSettings settings;
    private readonly Func<Transform> getTarget;

    private float orbitRadius;
    private bool movedOnOrbitThisFrame;

    public PlayerMotor(CharacterController controller, PlayerSettings settings, Func<Transform> getTarget)
    {
        this.controller = controller;
        transform = controller.transform;
        this.settings = settings;
        this.getTarget = getTarget;
    }

    public bool IsGrounded => controller.isGrounded;
    public float OrbitRadius => orbitRadius;
    private Transform Target => getTarget();
    private float StickForce => settings.locomotion.groundStickForce;
    private float AttackRange => settings.combat.attackRange;

    // ---- Frame ----

    /// Call once at the end of the frame, after the state has moved.
    public void EndFrame()
    {
        ClampMinDistanceToTarget();
        if (!movedOnOrbitThisFrame) SyncOrbitRadius();
        movedOnOrbitThisFrame = false;
    }

    public void SyncOrbitRadius()
    {
        if (Target == null) return;
        orbitRadius = FlatToPlayer().magnitude;
    }

    // ---- Directions ----

    /// Stick input frame: fwd = toward the target (or the body's forward with no target).
    public void GetBasis(out Vector3 fwd, out Vector3 right)
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
    public bool TryGetOrbitDirection(out Vector3 dir)
    {
        dir = Vector3.forward;
        if (Target == null) return false;
        Vector3 toPlayer = FlatToPlayer();
        if (toPlayer.sqrMagnitude < 0.0001f) return false;
        dir = toPlayer.normalized;
        return true;
    }

    /// Orbit-frame vector (x = right, y = toward target) to world space.
    public Vector3 LocalToWorld(Vector2 local)
    {
        GetBasis(out Vector3 fwd, out Vector3 right);
        return right * local.x + fwd * local.y;
    }

    public Vector2 WorldToLocal(Vector3 world)
    {
        GetBasis(out Vector3 fwd, out Vector3 right);
        return new Vector2(Vector3.Dot(world, right), Vector3.Dot(world, fwd));
    }

    public void FaceTarget()
    {
        if (Target == null) return;
        Vector3 dir = Target.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir.normalized);
    }

    // ---- Moving ----

    /// Grounded move along the orbit at a horizontal velocity.
    public void Move(Vector3 horizontalVelocity)
    {
        controller.Move(OrbitStep(horizontalVelocity * Time.deltaTime) + Stick());
    }

    /// Airborne move along the orbit; the caller owns vertical velocity.
    public void MoveAir(Vector3 horizontalDisplacement, float verticalDisplacement)
    {
        controller.Move(OrbitStep(horizontalDisplacement) + Vector3.up * verticalDisplacement);
    }

    /// Straight grounded move (not along the orbit), e.g. knockback.
    public void MoveStraight(Vector3 displacement)
    {
        controller.Move(displacement + Stick());
    }

    /// Straight grounded move that stops at attackRange instead of overshooting and
    /// being pushed back a frame later (attack steps, straight dashes).
    public void MoveStraightClamped(Vector3 displacement)
    {
        controller.Move(ClampApproachToMinDistance(displacement) + Stick());
    }

    /// Rotates around the target by an angle at the intended radius (side dash).
    public void RotateAroundTarget(float angleDeg)
    {
        if (!TryGetOrbitDirection(out Vector3 toPlayer)) return;
        controller.Move(OrbitCorrection(Quaternion.AngleAxis(angleDeg, Vector3.up) * toPlayer) + Stick());
    }

    // ---- Internals ----

    /// Toward/away changes the intended radius; sideways becomes an arc (length / radius
    /// = angle). No target: the displacement passes through unchanged.
    private Vector3 OrbitStep(Vector3 desiredDisplacement)
    {
        if (!TryGetOrbitDirection(out Vector3 radialDir)) return desiredDisplacement;
        if (orbitRadius <= 0f) SyncOrbitRadius();

        Vector3 tangentDir = Vector3.Cross(Vector3.up, radialDir);
        orbitRadius = Mathf.Max(0.01f, orbitRadius + Vector3.Dot(desiredDisplacement, radialDir));
        float angleDeg = Vector3.Dot(desiredDisplacement, tangentDir) / orbitRadius * Mathf.Rad2Deg;

        return OrbitCorrection(Quaternion.AngleAxis(angleDeg, Vector3.up) * radialDir);
    }

    private Vector3 OrbitCorrection(Vector3 newDir)
    {
        movedOnOrbitThisFrame = true;
        Vector3 correction = Target.position + newDir * orbitRadius - transform.position;
        correction.y = 0f;
        return correction;
    }

    /// Pushes the player back out to attackRange if this frame got closer.
    private void ClampMinDistanceToTarget()
    {
        if (Target == null) return;

        Vector3 toPlayer = FlatToPlayer();
        float distance = toPlayer.magnitude;
        if (distance < AttackRange && distance > 0.0001f)
        {
            Vector3 correction = Target.position + toPlayer.normalized * AttackRange - transform.position;
            correction.y = 0f;
            controller.Move(correction + Stick());
            orbitRadius = AttackRange;
        }
    }

    private Vector3 ClampApproachToMinDistance(Vector3 desiredDisplacement)
    {
        if (Target == null) return desiredDisplacement;

        Vector3 toPlayer = FlatToPlayer();
        float currentDistance = toPlayer.magnitude;
        if (currentDistance <= 0.0001f) return desiredDisplacement;

        Vector3 towardTarget = -toPlayer.normalized;
        float approach = Vector3.Dot(desiredDisplacement, towardTarget);
        float maxApproach = Mathf.Max(0f, currentDistance - AttackRange);
        if (approach > maxApproach)
            desiredDisplacement -= towardTarget * (approach - maxApproach);

        return desiredDisplacement;
    }

    private Vector3 FlatToPlayer()
    {
        Vector3 toPlayer = transform.position - Target.position;
        toPlayer.y = 0f;
        return toPlayer;
    }

    private Vector3 Stick() => Vector3.up * StickForce * Time.deltaTime;
}
