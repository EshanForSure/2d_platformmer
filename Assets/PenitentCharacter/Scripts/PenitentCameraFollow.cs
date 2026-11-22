using UnityEngine;

namespace PenitentCharacter
{
    public class PenitentCameraFollow : MonoBehaviour
    {
        public Transform target;
        public Vector3 offset = new Vector3(0f, 1.8f, -10f);
        public float smoothSpeed = 8f;

        private void LateUpdate()
        {
            if (target == null)
            {
                var player = FindFirstObjectByType<PenitentPlayerController>();
                if (player != null) target = player.transform;
                else return;
            }

            Vector3 desiredPosition = target.position + offset;
            transform.position = Vector3.Lerp(transform.position, desiredPosition, smoothSpeed * Time.deltaTime);
        }
    }
}
