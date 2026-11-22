using UnityEngine;

namespace EosaBlade
{
    /// <summary>
    /// Clamps HUD root inside Screen.safeArea for notched/punch-hole Android & iOS displays.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class SafeAreaFitter : MonoBehaviour
    {
        private RectTransform _rect;
        private Rect _lastSafeArea;
        private Vector2Int _lastScreenSize;

        private void Awake()
        {
            _rect = GetComponent<RectTransform>();
            Apply();
        }

        private void Update()
        {
            if (Screen.safeArea != _lastSafeArea || Screen.width != _lastScreenSize.x || Screen.height != _lastScreenSize.y)
            {
                Apply();
            }
        }

        private void Apply()
        {
            if (_rect == null || Screen.width <= 0 || Screen.height <= 0) return;

            Rect safe = Screen.safeArea;
            _lastSafeArea = safe;
            _lastScreenSize = new Vector2Int(Screen.width, Screen.height);

            Vector2 minAnchor = safe.position;
            Vector2 maxAnchor = safe.position + safe.size;
            minAnchor.x /= Screen.width;
            minAnchor.y /= Screen.height;
            maxAnchor.x /= Screen.width;
            maxAnchor.y /= Screen.height;

            _rect.anchorMin = minAnchor;
            _rect.anchorMax = maxAnchor;
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
        }
    }
}
