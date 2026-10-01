using UnityEngine;

/// <summary>
/// Third-person camera with two modes, switching automatically with PlayerController's
/// lock-on state:
///
/// LOCKED OFF: standard free orbit. Right stick directly drives yaw/pitch around the
/// player, camera sits behind at a fixed distance/height — exactly like a normal
/// no-target action game.
///
/// LOCKED ON: yaw is driven by the target's position instead of the stick — the camera
/// continuously reorients to sit behind the player on the opposite side from the target,
/// keeping the player between camera and enemy. Right stick is freed up for target-switch
/// flicking (already handled in PlayerController) instead of controlling the camera.
///
/// Both the lock-on toggle and a flick-driven target switch trigger the same reorient
/// transition (instant snap or smoothed, independently tunable from ongoing follow
/// damping) via PlayerController.TargetChanged.
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
    [Tooltip("Used to read IsLockedOn, Target, and subscribe to TargetChanged.")]
    [SerializeField] private PlayerController playerController;
    [SerializeField] private InputReader input;
    [Tooltip("The actual rendered camera — used to read FOV/aspect for the framing math. Defaults to Camera.main if left empty.")]
    [SerializeField] private Camera cam;

    [Header("Orbit")]
    [SerializeField] private float distance = 5f;
    [SerializeField] private float height = 1.8f;
    [Tooltip("Height offset for the look-at point, so the camera aims at roughly chest/head height, not the ground.")]
    [SerializeField] private float lookHeightOffset = 1.5f;

    [Header("Free Look (Locked Off)")]
    [Tooltip("Degrees/sec at full stick deflection.")]
    [SerializeField] private float lookSensitivity = 120f;
    [SerializeField] private float minPitch = -20f;
    [SerializeField] private float maxPitch = 60f;
    private float freeYaw;
    private float freePitch = 15f;

    [Header("Locked-On Framing")]
    [SerializeField] private float lockedOnPitch = 15f;
    [Tooltip("Look-at blend between player (0) and target (1).")]
    [Range(0f, 1f)]
    [SerializeField] private float lookAtTargetBias = 0.4f;

    [Header("Ongoing Damping")]
    [SerializeField] private float positionDamping = 8f;
    [SerializeField] private float rotationDamping = 10f;

    [Header("Reorient Transition (lock-on engage, or target switch)")]
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
    private bool wasLockedOn;

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

    /// Fires on lock-on engaging AND on a flick-driven target switch — both are "suddenly
    /// need to reorient toward a (possibly new) target" moments, so both use the same
    /// transition mechanism rather than two separate systems.
    private void OnReorientTriggered(Transform newTarget)
    {
        if (instantSnapOnReorient) snapPending = true;
        else transitionTimer = transitionDuration;
    }

    private void LateUpdate()
    {
        if (player == null || cam == null) return;

        bool isLockedOn = playerController != null && playerController.IsLockedOn;
        Transform target = playerController != null ? playerController.Target : null;

        // Lock-on turning OFF has no corresponding event (TargetChanged only fires when the
        // target transform itself changes) — detected here instead, so free-look resumes
        // from the camera's current facing rather than snapping to some stale freeYaw.
        if (wasLockedOn && !isLockedOn)
        {
            Vector3 flatForward = transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude > 0.0001f)
                freeYaw = Quaternion.LookRotation(flatForward, Vector3.up).eulerAngles.y;
        }
        wasLockedOn = isLockedOn;

        Vector3 desiredPos;
        Quaternion desiredRot;

        if (isLockedOn && target != null)
        {
            Vector3 toTarget = target.position - player.position;
            toTarget.y = 0f;
            Vector3 behindDir = toTarget.sqrMagnitude > 0.001f ? -toTarget.normalized : -player.forward;

            Quaternion orbitRot = Quaternion.LookRotation(behindDir, Vector3.up) * Quaternion.Euler(lockedOnPitch, 0f, 0f);
            desiredPos = player.position + orbitRot * Vector3.forward * distance + Vector3.up * height;

            Vector3 lookPoint = Vector3.Lerp(player.position, target.position, lookAtTargetBias) + Vector3.up * lookHeightOffset;
            desiredRot = Quaternion.LookRotation((lookPoint - desiredPos).normalized, Vector3.up);
        }
        else
        {
            freeYaw += input.LookInput.x * lookSensitivity * Time.deltaTime;
            freePitch = Mathf.Clamp(freePitch - input.LookInput.y * lookSensitivity * Time.deltaTime, minPitch, maxPitch);

            Quaternion orbitRot = Quaternion.Euler(freePitch, freeYaw, 0f);
            desiredPos = player.position + orbitRot * Vector3.back * distance + Vector3.up * height;

            Vector3 lookPoint = player.position + Vector3.up * lookHeightOffset;
            desiredRot = Quaternion.LookRotation((lookPoint - desiredPos).normalized, Vector3.up);
        }

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
    /// camera position, pulling the camera closer if geometry is in the way. Wasn't needed
    /// for the old side-view rig (it always sat out at fighting-game distance), but a
    /// behind-the-player camera sits close enough to walls/geometry that this matters now.
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
