using UnityEngine;

/// <summary>
/// Sparking Zero-style camera: orbits the midpoint between player and enemy, positioned to
/// one side of the axis connecting them (not behind the player), so both fighters stay
/// visible side-by-side rather than one filling the foreground. Distance is computed
/// exactly from the camera's actual FOV, so both points are mathematically guaranteed to
/// fit in frame rather than approximated with a linear guess. Drives the transform
/// directly — attach to a CinemachineCamera with both Position Control and Rotation
/// Control set to "Do Nothing".
///
/// Runs at a very early execution order so it's guaranteed to finish updating the transform
/// before CinemachineBrain processes the frame — without this, Unity doesn't guarantee
/// ordering between this script's LateUpdate and the Brain's, and if the Brain happens to
/// run first, it pushes last frame's stale position downstream (this is exactly what was
/// causing AuraManager's aura-capture camera to visibly drift off the character).
/// </summary>
[DefaultExecutionOrder(-1000)]
public class CombatSideCameraRig : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Transform player;
    [SerializeField] private Transform enemy;
    [Tooltip("Needed so this rig's enemy reference stays in sync with lock-on target switches — without this, the camera keeps framing whatever it was originally pointed at even after the player locks onto or flicks to a different target.")]
    [SerializeField] private PlayerController playerController;
    [SerializeField] private InputReader input;
    [Tooltip("The actual rendered camera — used to read FOV/aspect for the framing math. Defaults to Camera.main if left empty.")]
    [SerializeField] private Camera cam;

    [Header("Positioning")]
    [Tooltip("Minimum distance from the midpoint, even at zero separation.")]
    [SerializeField] private float minDistance = 4f;
    [Tooltip("Extra horizontal room left on each side beyond the two fighters, in world units.")]
    [SerializeField] private float framingPadding = 1.5f;
    [Tooltip("Height above the midpoint the camera sits at, at zero separation.")]
    [SerializeField] private float baseHeight = 1.8f;
    [Tooltip("Extra height added per unit of separation.")]
    [SerializeField] private float heightPerSeparation = 0.05f;
    [Tooltip("0 = pure side-on (90° from the player-enemy axis). Lower values pull the " +
             "camera toward a more 3/4-angle view instead of a flat profile shot.")]
    [Range(0f, 90f)]
    [SerializeField] private float sideAngle = 75f;
    [Tooltip("Max degrees/sec the player-enemy axis direction is allowed to visibly change. " +
             "Without this, the camera can spin extremely fast at close range, since the raw " +
             "direction between two nearby points is highly sensitive to small movements.")]
    [SerializeField] private float maxAxisTurnSpeed = 180f;

    [Header("Framing Bias")]
    [Tooltip("Look-at bias when far apart (0 = player, 1 = enemy). Favors the player at range.")]
    [Range(0f, 1f)]
    [SerializeField] private float farBias = 0.3f;
    [Tooltip("Look-at bias when close together. Favors the enemy up close.")]
    [Range(0f, 1f)]
    [SerializeField] private float nearBias = 0.6f;
    [Tooltip("Separation at which farBias fully applies. Below this, bias eases toward nearBias.")]
    [SerializeField] private float biasMaxSeparation = 15f;
    [Tooltip("Height offset for the look-at point, so the camera aims at roughly chest/head height, not the ground.")]
    [SerializeField] private float lookHeightOffset = 1f;

    [Header("Damping")]
    [SerializeField] private float positionDamping = 8f;
    [SerializeField] private float rotationDamping = 10f;

    [Header("Target Switch Transition")]
    [Tooltip("If true, the camera instantly snaps to frame the new target the moment lock-on " +
             "switches. If false, it transitions using the smoothed settings below instead.")]
    [SerializeField] private bool instantSnapOnTargetSwitch = false;
    [Tooltip("Position damping used ONLY during the transition right after a target switch — " +
             "separate from the normal ongoing follow damping above, so switch feel can be " +
             "tuned independently. Higher = faster/snappier.")]
    [SerializeField] private float targetSwitchPositionDamping = 15f;
    [Tooltip("Rotation damping used ONLY during the transition right after a target switch.")]
    [SerializeField] private float targetSwitchRotationDamping = 12f;
    [Tooltip("How long after a switch the above transition-specific damping values are used, " +
             "before reverting to the normal ongoing follow damping.")]
    [SerializeField] private float targetSwitchTransitionDuration = 0.3f;
    private float targetSwitchTransitionTimer;
    private bool snapPendingThisFrame;

    private int side = 1; // +1 or -1, which side of the axis the camera sits on
    private Vector3 currentVelocity;
    private Vector3 smoothedAxisDir = Vector3.forward;

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
    }

    private void OnEnable()
    {
        if (playerController != null)
        {
            playerController.TargetChanged += OnTargetChanged;
            enemy = playerController.Target; // in case lock-on already has a target assigned before this rig ever gets a TargetChanged event
        }
    }

    private void OnDisable()
    {
        if (playerController != null) playerController.TargetChanged -= OnTargetChanged;
    }

    private void OnTargetChanged(Transform newTarget)
    {
        enemy = newTarget;
        if (instantSnapOnTargetSwitch) snapPendingThisFrame = true;
        else targetSwitchTransitionTimer = targetSwitchTransitionDuration;
    }

    public void FlipSide() => side *= -1;

    private void LateUpdate()
    {
        if (player == null || enemy == null || cam == null) return;

        Vector3 midpoint = Vector3.Lerp(player.position, enemy.position, 0.5f);

        Vector3 toEnemy = enemy.position - player.position;
        toEnemy.y = 0f;
        float separation = toEnemy.magnitude;
        Vector3 rawAxisDir = separation > 0.001f ? toEnemy.normalized : player.forward;

        // Smoothed, capped-speed axis direction — prevents the fast spin that raw direction
        // produces at close range, where tiny position changes swing the angle a lot.
        smoothedAxisDir = Vector3.RotateTowards(smoothedAxisDir, rawAxisDir,
            maxAxisTurnSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f).normalized;

        Vector3 perpendicular = Vector3.Cross(Vector3.up, smoothedAxisDir);
        Vector3 sideDir = Quaternion.AngleAxis((90f - sideAngle) * side, Vector3.up) * (perpendicular * side);

        // Exact distance from FOV, not a linear approximation — guarantees both points fit
        // horizontally in frame (plus padding) rather than hoping a scale factor is enough.
        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad / 2f) * cam.aspect);
        float requiredDistance = (separation / 2f + framingPadding) / Mathf.Tan(horizontalFovRad / 2f);
        float distance = Mathf.Max(minDistance, requiredDistance);

        float height = baseHeight + separation * heightPerSeparation;

        Vector3 desiredPos = midpoint + sideDir.normalized * distance + Vector3.up * height;

        // Bias eases from nearBias (favor enemy, close) to farBias (favor player, far apart).
        float biasT = biasMaxSeparation > 0f ? Mathf.Clamp01(separation / biasMaxSeparation) : 0f;
        float lookBias = Mathf.Lerp(nearBias, farBias, biasT);

        Vector3 lookPoint = Vector3.Lerp(player.position, enemy.position, lookBias) + Vector3.up * lookHeightOffset;
        Quaternion desiredRot = Quaternion.LookRotation((lookPoint - desiredPos).normalized, Vector3.up);

        if (snapPendingThisFrame)
        {
            transform.position = desiredPos;
            transform.rotation = desiredRot;
            snapPendingThisFrame = false;
        }
        else
        {
            bool inSwitchTransition = targetSwitchTransitionTimer > 0f;
            float posDamping = inSwitchTransition ? targetSwitchPositionDamping : positionDamping;
            float rotDamping = inSwitchTransition ? targetSwitchRotationDamping : rotationDamping;
            if (inSwitchTransition) targetSwitchTransitionTimer -= Time.deltaTime;

            transform.position = Vector3.SmoothDamp(transform.position, desiredPos, ref currentVelocity, 1f / posDamping);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotDamping * Time.deltaTime);
        }
    }
}