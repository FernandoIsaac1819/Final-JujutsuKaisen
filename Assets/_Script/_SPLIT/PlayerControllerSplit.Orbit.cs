using UnityEngine;

/// Authoritative orbit state (orbitDirection/orbitRadius) and the Move()/OrbitalDisplacement
/// pipeline everything else routes horizontal movement through. This is what actually
/// prevents circular-strafe drift — see SyncOrbitState's own comment for why.
public partial class PlayerControllerSplit
{
    // ---------------- Helpers ----------------

    // Authoritative orbit state — evolved purely through rotation, never re-derived from
    // transform.position each frame. This is what actually fixes the drift: rotating a
    // vector is mathematically exact (never changes its length), while re-reading position
    // after CharacterController.Move() inherits whatever small collision-sweep imprecision
    // happened that frame, and that error was compounding frame over frame.
    private Vector3 orbitDirection = Vector3.forward; // world-space, points from target toward player
    private float orbitRadius;
    private bool orbitInitialized;
    private enum LocomotionAnimCategory { Idle, Forward, Backward, StrafeLeft, StrafeRight }
    private LocomotionAnimCategory currentAnimCategory = LocomotionAnimCategory.Idle;
    private Vector2 locomotionBlendInput; // smoothly damped toward whichever category's fixed point is current — never sits at an arbitrary diagonal
    private Vector2 locomotionBlendVelocity;
    private Vector3 moveDirTransitionStart; // direction captured at the instant a category change happens
    private float moveDirTransitionTimer; // counts down from locomotionBlendSmoothTime — once it hits 0, movement uses the exact fresh direction with zero blending, same as before
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

    private void Move(Vector3 horizontal)
    {
        Vector3 displacement = horizontal * Time.deltaTime;

        if (target != null)
        {
            if (!orbitInitialized) SyncOrbitState();
            displacement = OrbitalDisplacement(displacement);
        }

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
