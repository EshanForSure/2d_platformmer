using System;
using UnityEngine;

namespace PenitentCharacter
{
    [RequireComponent(typeof(Collider2D))]
    public class PenitentHitbox : MonoBehaviour
    {
        public Collider2D hitCollider { get; private set; }
        public int damage = 25;
        public float knockback = 5f;

        /// <summary>Fired when this hitbox touches something that is not the owner (used by audio + telemetry).</summary>
        public event Action<PenitentHitbox, Collider2D> HitTarget;

        private void Awake()
        {
            hitCollider = GetComponent<Collider2D>();
            if (hitCollider != null)
            {
                hitCollider.isTrigger = true;
                hitCollider.enabled = false;
            }
        }

        public void Activate(int dmg, float kb)
        {
            damage = dmg;
            knockback = kb;
            if (hitCollider != null) hitCollider.enabled = true;
        }

        public void Deactivate()
        {
            if (hitCollider != null) hitCollider.enabled = false;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            // Ignore own root character
            if (other.transform.root == transform.root) return;

            Debug.Log($"[PenitentHitbox] Hit target: {other.gameObject.name} for {damage} damage with knockback {knockback}!");
            HitTarget?.Invoke(this, other);
        }

        private void OnDrawGizmos()
        {
            if (hitCollider != null && hitCollider.enabled)
            {
                Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.5f);
                if (hitCollider is BoxCollider2D box)
                {
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawCube(box.offset, box.size);
                }
                else if (hitCollider is CircleCollider2D circle)
                {
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawSphere(circle.offset, circle.radius);
                }
            }
        }
    }
}
