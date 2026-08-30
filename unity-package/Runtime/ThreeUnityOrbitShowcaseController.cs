using UnityEngine;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class ThreeUnityOrbitShowcaseController : MonoBehaviour
    {
        [SerializeField] private string sceneTitle = "Three.js conversion";
        [SerializeField] private Vector3 target;
        [SerializeField] private float distance = 35f;
        [SerializeField] private float yaw = 35f;
        [SerializeField] private float pitch = 42f;
        [SerializeField] private float panSpeed = 14f;

        public void Configure(string title, Vector3 focus, float initialDistance)
        {
            sceneTitle = title;
            target = focus;
            distance = Mathf.Max(3f, initialDistance);
            ApplyTransform();
        }

        private void Start()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            ApplyTransform();
        }

        private void Update()
        {
            var fast = Input.GetKey(KeyCode.LeftShift) ? 3f : 1f;
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            target += (forward * Input.GetAxisRaw("Vertical") + right * Input.GetAxisRaw("Horizontal")) * panSpeed * fast * Time.deltaTime;
            if (Input.GetKey(KeyCode.Q)) target += Vector3.down * panSpeed * Time.deltaTime;
            if (Input.GetKey(KeyCode.E)) target += Vector3.up * panSpeed * Time.deltaTime;
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxisRaw("Mouse X") * 3f;
                pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * 3f, 10f, 85f);
            }
            distance = Mathf.Clamp(distance * Mathf.Exp(-Input.mouseScrollDelta.y * 0.12f), 2f, 300f);
            ApplyTransform();
        }

        private void ApplyTransform()
        {
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
        }

        private void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, fontSize = 15 };
            GUI.Box(new Rect(16, 16, 520, 66), sceneTitle +
                "\nWASD: pan | Q/E: lower/raise | Shift: fast | Right drag: orbit | Wheel: zoom", style);
        }
    }
}
