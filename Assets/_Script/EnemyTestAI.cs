using UnityEngine;

/// <summary>
/// Test-only AI. Wanders to random points within a radius of its starting position, purely
/// to give the camera's dual-target framing continuously changing distance/angle to react
/// to while testing. No pathfinding, no combat behavior — not meant to survive into real AI.
/// </summary>
public class EnemyTestAI : MonoBehaviour
{
    [Header("Wander")]
    [Tooltip("How far from its starting position the enemy will wander.")]
    [SerializeField] private float wanderRadius = 6f;
    [SerializeField] private float moveSpeed = 2f;
    [SerializeField] private float destinationReachedThreshold = 0.3f;
    [Tooltip("Seconds to pause at each destination before picking a new one.")]
    [SerializeField] private float waitTimeAtDestination = 1f;

    private Vector3 origin;
    private Vector3 currentDestination;
    private float waitTimer;

    private void Start()
    {
        origin = transform.position;
        PickNewDestination();
    }

    private void Update()
    {
        float distance = Vector3.Distance(transform.position, currentDestination);
        if (distance <= destinationReachedThreshold)
        {
            waitTimer -= Time.deltaTime;
            if (waitTimer <= 0f) PickNewDestination();
            return;
        }

        Vector3 dir = currentDestination - transform.position;
        dir.y = 0f;
        dir.Normalize();

        transform.position += dir * moveSpeed * Time.deltaTime;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir);
    }

    private void PickNewDestination()
    {
        Vector2 randomCircle = Random.insideUnitCircle * wanderRadius;
        currentDestination = origin + new Vector3(randomCircle.x, 0f, randomCircle.y);
        waitTimer = waitTimeAtDestination;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(Application.isPlaying ? origin : transform.position, wanderRadius);
    }
}
