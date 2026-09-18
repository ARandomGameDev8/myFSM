// Test 02 — the WASD object every spawned chaser hunts.
//
// Deliberately not an FSM: the point of this test is how many AIs the runtime
// can tick, so the target has to be free — it just moves a transform. The
// chasers aim at this transform's position, which means walking away makes them
// spread out and re-path every tick (that is the load being measured).

using UnityEngine;

namespace MyFSM.Tests
{
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        public float speed = 8f;
        [Tooltip("Keep the object at this height (0 = plain 2D-plane movement in XZ).")]
        public float height = 0.5f;
        [Tooltip("Camera-relative movement instead of world axes.")]
        public bool cameraRelative = true;

        [Header("Bounds (walking off the plane stops the stress test being fair)")]
        public bool clampToSquare = true;
        public float halfExtent = 45f;

        private void Update()
        {
            float x = 0f;
            float z = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) z -= 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) z += 1f;

            Vector3 direction = new Vector3(x, 0f, z);
            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            if (cameraRelative && Camera.main != null)
            {
                Vector3 forward = Camera.main.transform.forward;
                Vector3 right = Camera.main.transform.right;
                forward.y = 0f;
                right.y = 0f;
                forward.Normalize();
                right.Normalize();
                direction = forward * direction.z + right * direction.x;
            }

            Vector3 next = transform.position + direction * speed * Time.deltaTime;
            next.y = height;
            if (clampToSquare)
            {
                next.x = Mathf.Clamp(next.x, -halfExtent, halfExtent);
                next.z = Mathf.Clamp(next.z, -halfExtent, halfExtent);
            }
            transform.position = next;
        }
    }
}
