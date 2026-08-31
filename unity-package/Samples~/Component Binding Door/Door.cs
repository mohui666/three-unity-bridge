using UnityEngine;

namespace ThreeUnity.Bridge.Samples.ComponentBindingDoor
{
    [DisallowMultipleComponent]
    public sealed class Door : MonoBehaviour
    {
        [SerializeField] private float openAngle;
        [SerializeField] private float duration;
        [SerializeField] private bool startsOpen;

        private Quaternion closedRotation;
        private float currentAngle;
        private float targetAngle;

        public float OpenAngle => openAngle;
        public float Duration => duration;
        public bool StartsOpen => startsOpen;

        public void Configure(float configuredOpenAngle, float configuredDuration, bool configuredStartsOpen)
        {
            openAngle = configuredOpenAngle;
            duration = configuredDuration;
            startsOpen = configuredStartsOpen;
        }

        private void Start()
        {
            closedRotation = transform.localRotation;
            currentAngle = startsOpen ? openAngle : 0f;
            targetAngle = openAngle;
            ApplyRotation();
        }

        private void Update()
        {
            currentAngle = Mathf.MoveTowards(
                currentAngle,
                targetAngle,
                Mathf.Abs(openAngle) * Time.deltaTime / duration);
            ApplyRotation();
        }

        private void ApplyRotation()
        {
            transform.localRotation = closedRotation * Quaternion.AngleAxis(currentAngle, Vector3.up);
        }
    }
}
