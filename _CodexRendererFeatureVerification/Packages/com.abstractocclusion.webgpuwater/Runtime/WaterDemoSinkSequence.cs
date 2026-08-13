using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [DisallowMultipleComponent]
    public sealed class WaterDemoSinkSequence : MonoBehaviour
    {
        const float MinimumDurationSeconds = 1f;
        [SerializeField] Transform sinkingTarget;
        [SerializeField] float descentDistance = 70f;
        [SerializeField] float descentDuration = 24f;
        [SerializeField] float startDelay = 2f;

        Vector3 _startPosition;
        float _startTime;

        void Awake()
        {
            if (sinkingTarget == null)
            {
                Debug.LogError("WaterDemoSinkSequence requires a sinking target.", this);
                enabled = false;
                return;
            }
            _startPosition = sinkingTarget.position;
            _startTime = Time.time;
        }

        void Update()
        {
            if (ResetPressed()) ResetSequence();
            float elapsed = Mathf.Max(0f, Time.time - _startTime - startDelay);
            float duration = Mathf.Max(MinimumDurationSeconds, descentDuration);
            float progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
            sinkingTarget.position = _startPosition + Vector3.down * (descentDistance * progress);
        }

        void ResetSequence()
        {
            sinkingTarget.position = _startPosition;
            _startTime = Time.time;
        }

        static bool ResetPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard != null && keyboard.rKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.R);
#endif
        }
    }
}
