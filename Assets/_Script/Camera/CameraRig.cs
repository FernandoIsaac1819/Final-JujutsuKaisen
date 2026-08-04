using UnityEngine;

/// <summary>
/// Simple rigid follow camera: always positioned behind the character, always looking
/// at the character. No player rotation control, no mode switching, no blending —
/// replaces CombatCameraRig / FreeOrbitCameraRig / CameraModeController entirely.
/// Attach directly to Main Camera (no CinemachineBrain needed for this).
/// </summary>
public class CameraRig : MonoBehaviour
{
    [SerializeField] private Transform player;

    [Header("Position")]
    [SerializeField] private float distance = 4.5f;
    [SerializeField] private float height = 1.7f;

    [Header("Follow Smoothing")]
    [SerializeField] private float positionSmoothTime = 0.08f;
    [SerializeField] private float rotationSmoothTime = 0.08f;

    private Vector3 positionVelocity;
    private float rotationVelocity;

    private void LateUpdate()
    {
        Vector3 targetPos = player.position + Vector3.up * height + (-player.forward) * distance;
        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref positionVelocity, positionSmoothTime);

        Vector3 lookTarget = player.position + Vector3.up * height * 0.6f;
        Vector3 dir = lookTarget - transform.position;
        if (dir.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime / Mathf.Max(rotationSmoothTime, 0.0001f));
        }
    }
}
