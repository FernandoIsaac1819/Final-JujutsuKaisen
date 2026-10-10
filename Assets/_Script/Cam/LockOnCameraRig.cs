using UnityEngine;

/// <summary>
/// Third-person camera, always locked onto PlayerController's current target — there is
/// no free-look mode. Yaw is driven entirely by the target's position: the camera
/// continuously reorients to sit behind the player on the opposite side from the target,
/// keeping the player between camera and enemy. The right stick is left free for
/// target-switch flicking (handled in PlayerController) rather than controlling the
/// camera.
///
/// A flick-driven target switch triggers a reorient transition (instant snap or smoothed,
/// independently tunable from ongoing follow damping) via PlayerController.TargetChanged.
///
/// Runs at a very early execution order so it's guaranteed to finish updating the
/// transform before CinemachineBrain processes the frame. Attach to a CinemachineCamera
/// with both Position Control and Rotation Control set to "Do Nothing".
/// </summary>
[DefaultExecutionOrder(-1000)]
public class LockOnCameraRig : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Transform player;
    [Tooltip("Used to read Target and subscribe to TargetChanged.")]
    [SerializeField] private PlayerController playerController;
    [Tooltip("The actual rendered camera — used to read FOV/aspect for the framing math. Defaults to Camera.main if left empty.")]
    [SerializeField] private Camera cam;

    [Header("Orbit")]
    [SerializeField] private float distance = 5f;
    [SerializeField] private float height = 1.8f;
    [Tooltip("Height offset for the look-at point, so the camera aims at roughly chest/head height, not the ground.")]
    [SerializeField] private float lookHeightOffset = 1.5f;

    [Header("Locked-On Framing")]
    [SerializeField] private float lockedOnPitch = 15f;
    [Tooltip("Look-at blend between player (0) and target (1).")]
    [Range(0f, 1f)]
    [SerializeField] private float lookAtTargetBias = 0.4f;

    [Header("Ongoing Damping")]
    [SerializeField] private float positionDamping = 8f;
    [SerializeField] private float rotationDamping = 10f;

    [Header("Reorient Transition (target switch)")]
    [Tooltip("If true, the camera instantly snaps to the new framing. If false, it transitions using the damping below instead.")]
    [SerializeField] private bool instantSnapOnReorient = false;
    [SerializeField] private float transitionPositionDamping = 15f;
    [SerializeField] private float transitionRotationDamping = 12f;
    [SerializeField] private float transitionDuration = 0.3f;
    private float transitionTimer;
    private bool snapPending;

    [Header("Collision")]
    [SerializeField] private LayerMask collisionMask;
    [SerializeField] private float collisionRadius = 0.3f;
    [SerializeField] private float collisionPadding = 0.2f;

    private Vector3 currentVelocity;

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
    }

    private void OnEnable()
    {
        if (playerController != null) playerController.TargetChanged += OnReorientTriggered;
    }

    private void OnDisable()
    {
        if (playerController != null) playerController.TargetChanged -= OnReorientTriggered;
    }

    /// Fires whenever the target is (re)assigned — initial engage at game start, or a
    /// flick-driven target switch — both are "suddenly need to reorient toward a (possibly
    /// new) target" moments, so both use the same transition mechanism.
    private void OnReorientTriggered(Transform newTarget)
    {
        if (instantSnapOnReorient) snapPending = true;
        else transitionTimer = transitionDuration;
    }

    private void LateUpdate()
    {
        if (player == null || cam == null) return;

        Transform target = playerController != null ? playerController.Target : null;

        // No target yet (e.g. before PlayerController's startup lock-on has resolved one) —
        // fall back to framing directly behind the player's own facing until one exists.
        Vector3 behindDir = -player.forward;
        Vector3 lookPoint = player.position + Vector3.up * lookHeightOffset;

        if (target != null)
        {
            Vector3 toTarget = target.position - player.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.001f) behindDir = -toTarget.normalized;

            lookPoint = Vector3.Lerp(player.position, target.position, lookAtTargetBias) + Vector3.up * lookHeightOffset;
        }

        Quaternion orbitRot = Quaternion.LookRotation(behindDir, Vector3.up) * Quaternion.Euler(lockedOnPitch, 0f, 0f);
        Vector3 desiredPos = player.position + orbitRot * Vector3.forward * distance + Vector3.up * height;
        Quaternion desiredRot = Quaternion.LookRotation((lookPoint - desiredPos).normalized, Vector3.up);

        desiredPos = ApplyCollision(player.position + Vector3.up * height, desiredPos);

        if (snapPending)
        {
            transform.position = desiredPos;
            transform.rotation = desiredRot;
            snapPending = false;
        }
        else
        {
            bool inTransition = transitionTimer > 0f;
            float posDamping = inTransition ? transitionPositionDamping : positionDamping;
            float rotDamping = inTransition ? transitionRotationDamping : rotationDamping;
            if (inTransition) transitionTimer -= Time.deltaTime;

            transform.position = Vector3.SmoothDamp(transform.position, desiredPos, ref currentVelocity, 1f / posDamping);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotDamping * Time.deltaTime);
        }
    }

    /// Spring-arm-style push-in — casts from roughly the player's chest toward the desired
    /// camera position, pulling the camera closer if geometry is in the way.
    private Vector3 ApplyCollision(Vector3 from, Vector3 desired)
    {
        Vector3 offset = desired - from;
        float dist = offset.magnitude;
        if (dist < 0.001f) return desired;

        Vector3 dir = offset / dist;
        if (Physics.SphereCast(from, collisionRadius, dir, out RaycastHit hit, dist, collisionMask))
        {
            return from + dir * Mathf.Max(0.1f, hit.distance - collisionPadding);
        }

        return desired;
    }
}