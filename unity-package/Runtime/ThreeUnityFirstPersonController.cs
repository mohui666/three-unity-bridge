using UnityEngine;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class ThreeUnityFirstPersonController : MonoBehaviour
    {
        [SerializeField] private Camera viewCamera;
        [SerializeField] private Transform editableWorldRoot;
        [SerializeField] private string sceneTitle = "Three.js conversion";
        [SerializeField] private ThreeUnityRuntimeProfile runtimeProfile;
        [SerializeField] private bool enableBlockEditing;
        [SerializeField] private bool captureMouseOnStart = true;
        [SerializeField] private float moveSpeed = 5.5f;
        [SerializeField] private float sprintSpeed = 9f;
        [SerializeField] private float flySpeed = 8f;
        [SerializeField] private float jumpHeight = 1.25f;
        [SerializeField] private float gravity = -24f;
        [SerializeField] private float lookSensitivity = 2.1f;
        [SerializeField] private float interactionDistance = 7f;

        private CharacterController characterController;
        private float pitch;
        private float verticalVelocity;
        private Vector3 spawnPosition;
        private Quaternion spawnRotation;
        private bool flying;
        private int selectedBlockIndex;

        public void Configure(Camera camera, Transform worldRoot, string title, bool blockEditing)
        {
            viewCamera = camera;
            editableWorldRoot = worldRoot;
            sceneTitle = title;
            enableBlockEditing = blockEditing;
        }

        public void Configure(Camera camera, Transform worldRoot, ThreeUnityRuntimeProfile profile)
        {
            viewCamera = camera;
            editableWorldRoot = worldRoot;
            runtimeProfile = profile;
            sceneTitle = profile != null ? profile.gameObject.name : "Three.js conversion";
            enableBlockEditing = profile != null && profile.enableBlockEditing;
            if (profile == null) return;
            moveSpeed = profile.moveSpeed;
            sprintSpeed = profile.sprintSpeed;
            flySpeed = profile.flySpeed;
        }

        private void Awake()
        {
            characterController = GetComponent<CharacterController>();
            if (viewCamera == null) viewCamera = GetComponentInChildren<Camera>(true);
            spawnPosition = transform.position;
            spawnRotation = transform.rotation;
            pitch = NormalizeAngle(viewCamera != null ? viewCamera.transform.localEulerAngles.x : 0f);
        }

        private void Start()
        {
            if (captureMouseOnStart) CaptureMouse();
            else ReleaseMouse();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) ReleaseMouse();
            if (Input.GetKeyDown(KeyCode.R)) ResetPlayer();

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                if (Input.GetMouseButtonDown(0)) CaptureMouse();
                return;
            }

            if (AllowFly && Input.GetKeyDown(KeyCode.F)) flying = !flying;
            for (var index = 0; index < HotbarLength; index++)
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + index))) selectedBlockIndex = index;

            Look();
            Move();
            if (enableBlockEditing && Input.GetMouseButtonDown(0)) RemoveBlock();
            if (enableBlockEditing && Input.GetMouseButtonDown(1)) PlaceBlock();
            if (transform.position.y < -30f) ResetPlayer();
        }

        private void Look()
        {
            var yaw = Input.GetAxisRaw("Mouse X") * lookSensitivity;
            var pitchDelta = Input.GetAxisRaw("Mouse Y") * lookSensitivity;
            transform.Rotate(0f, yaw, 0f, Space.Self);
            pitch = Mathf.Clamp(pitch - pitchDelta, -88f, 88f);
            if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void Move()
        {
            var input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            input = Vector2.ClampMagnitude(input, 1f);

            if (flying)
            {
                var forward = viewCamera != null ? viewCamera.transform.forward : transform.forward;
                var right = viewCamera != null ? viewCamera.transform.right : transform.right;
                var direction = forward * input.y + right * input.x;
                if (Input.GetKey(KeyCode.Space)) direction += Vector3.up;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) direction -= Vector3.up;
                characterController.Move(Vector3.ClampMagnitude(direction, 1f) * flySpeed * Time.deltaTime);
                verticalVelocity = 0f;
                return;
            }

            var speed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : moveSpeed;
            var horizontal = (transform.right * input.x + transform.forward * input.y) * speed;

            if (characterController.isGrounded)
            {
                if (verticalVelocity < 0f) verticalVelocity = -2f;
                if (Input.GetButtonDown("Jump")) verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            verticalVelocity += gravity * Time.deltaTime;
            characterController.Move((horizontal + Vector3.up * verticalVelocity) * Time.deltaTime);
        }

        private void RemoveBlock()
        {
            if (!TryGetWorldHit(out var hit)) return;
            Destroy(hit.collider.gameObject);
        }

        private void PlaceBlock()
        {
            if (!TryGetWorldHit(out var hit)) return;
            var position = hit.point + hit.normal * 0.51f;
            position = new Vector3(Mathf.Round(position.x), Mathf.Round(position.y), Mathf.Round(position.z));
            if ((position - transform.position).sqrMagnitude < 2.5f) return;

            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "Placed Unity Block";
            block.transform.SetParent(editableWorldRoot, true);
            block.transform.position = position;
            var blockRenderer = block.GetComponent<MeshRenderer>();
            if (HotbarLength > 0)
            {
                var shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
                if (shader != null)
                {
                    var material = new Material(shader) { name = $"Placed {SelectedItem.name}" };
                    if (material.HasProperty("_Color")) material.SetColor("_Color", SelectedItem.color);
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", SelectedItem.color);
                    blockRenderer.material = material;
                }
            }
            else
            {
                var sourceRenderer = hit.collider.GetComponent<MeshRenderer>();
                if (sourceRenderer != null) blockRenderer.sharedMaterial = sourceRenderer.sharedMaterial;
            }
        }

        private bool TryGetWorldHit(out RaycastHit hit)
        {
            hit = default;
            if (viewCamera == null || editableWorldRoot == null) return false;
            var ray = viewCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f));
            if (!Physics.Raycast(ray, out hit, interactionDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            return hit.collider.transform.IsChildOf(editableWorldRoot);
        }

        private void CaptureMouse()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private static void ReleaseMouse()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void ResetPlayer()
        {
            characterController.enabled = false;
            transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            characterController.enabled = true;
            verticalVelocity = 0f;
        }

        private void OnGUI()
        {
            if (runtimeProfile != null && runtimeProfile.hudStyle == "voxel-hotbar") DrawVoxelHotbarHud();
            else DrawDiagnosticHud();

            if (Cursor.lockState == CursorLockMode.Locked) DrawCrosshair();
            else GUI.Box(new Rect(Screen.width * 0.5f - 130f, Screen.height * 0.5f - 25f, 260f, 50f), "Click Game View to start");
        }

        private void DrawDiagnosticHud()
        {
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, fontSize = 15 };
            GUI.Box(new Rect(16, 16, 460, enableBlockEditing ? 86 : 66),
                sceneTitle + "\nClick: capture mouse | WASD: move | Shift: sprint | Space: jump | R: reset" +
                (enableBlockEditing ? "\nLeft click: mine | Right click: place | Esc: release mouse" : "\nEsc: release mouse"), style);
        }

        private void DrawVoxelHotbarHud()
        {
            var panel = new Rect(28f, 18f, Screen.width - 56f, 54f);
            DrawRect(panel, new Color(0.025f, 0.08f, 0.1f, 0.92f));

            var modeStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 14, normal = { textColor = Color.white } };
            GUI.Box(new Rect(panel.x + 12f, panel.y + 9f, 98f, 36f), flying ? "Fly Mode" : "Walk Mode", modeStyle);
            GUI.Box(new Rect(panel.x + 120f, panel.y + 9f, 98f, 36f), HotbarLength > 0 ? "■  " + SelectedItem.name : "No items", modeStyle);
            if (HotbarLength > 0) DrawRect(new Rect(panel.x + 130f, panel.y + 22f, 10f, 10f), SelectedItem.color);

            var controls = flying ? "Space / Shift   Up / Down     F   Walk     Esc   Cursor" : "Space   Jump     Shift   Sprint     F   Fly     Esc   Cursor";
            var controlStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontSize = 13, normal = { textColor = Color.white } };
            GUI.Label(new Rect(panel.x + 230f, panel.y + 8f, panel.width - 246f, 38f), controls, controlStyle);

            var hotbarWidth = HotbarLength * 58f + 16f;
            var hotbar = new Rect((Screen.width - hotbarWidth) * 0.5f, Screen.height - 94f, hotbarWidth, 68f);
            DrawRect(hotbar, new Color(0.025f, 0.08f, 0.1f, 0.92f));
            for (var index = 0; index < HotbarLength; index++)
            {
                var slot = new Rect(hotbar.x + 8f + index * 58f, hotbar.y + 8f, 50f, 52f);
                DrawRect(slot, index == selectedBlockIndex ? Color.white : new Color(0.25f, 0.28f, 0.27f, 1f));
                DrawRect(new Rect(slot.x + 5f, slot.y + 5f, 40f, 38f), runtimeProfile.hotbar[index].color);
                GUI.Label(new Rect(slot.x + 36f, slot.y + 35f, 12f, 15f), (index + 1).ToString());
            }
        }

        private static void DrawCrosshair()
        {
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            DrawRect(new Rect(center.x - 1f, center.y - 8f, 2f, 16f), Color.white);
            DrawRect(new Rect(center.x - 8f, center.y - 1f, 16f, 2f), Color.white);
        }

        private static void DrawRect(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private int HotbarLength => runtimeProfile?.hotbar?.Length ?? 0;
        private bool AllowFly => runtimeProfile != null && runtimeProfile.allowFly;
        private ThreeUnityHotbarItem SelectedItem => runtimeProfile.hotbar[Mathf.Clamp(selectedBlockIndex, 0, HotbarLength - 1)];

        private static float NormalizeAngle(float value) => value > 180f ? value - 360f : value;
    }
}
