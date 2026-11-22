using UnityEngine;

public class MagicProjectile : MonoBehaviour
{
    [SerializeField] private float speed = 7.5f;
    [SerializeField] private int damage = 1;
    [SerializeField] private float lifetime = 5f;

    private Vector2 moveDirection = Vector2.right;
    private bool initialized;

    public void Launch(Vector2 direction, int dmg = 1)
    {
        moveDirection = direction.normalized;
        damage = dmg;
        initialized = true;
        Destroy(gameObject, lifetime);
    }

    private void Update()
    {
        if (!initialized) return;
        transform.position += (Vector3)(moveDirection * speed * Time.deltaTime);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other == null) return;

        // Ignore firing boss or any boss
        if (other.GetComponentInParent<BossController>() != null) return;

        // Ignore one-way platforms
        if (other.usedByEffector) return;

        // Hit player
        PlayerController player = other.GetComponentInParent<PlayerController>();
        if (player != null && !player.IsDead)
        {
            player.TakeDamage(damage, moveDirection.x);
            Destroy(gameObject);
            return;
        }

        // Hit solid ground
        if (other.gameObject.layer == LayerMask.NameToLayer("Ground"))
        {
            Destroy(gameObject);
            return;
        }

        // Hit decorations
        if (other.CompareTag("Decoration"))
        {
            Destroy(other.gameObject);
        }
    }
}
