using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EosaBlade
{
    /// <summary>
    /// Multi-touch friendly mobile button supporting instant pointer-down/up events and subtle scale punch.
    /// </summary>
    [RequireComponent(typeof(Graphic))]
    public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public event Action Pressed;
        public event Action Released;

        private bool _isHeld;
        private Vector3 _baseScale;
        private RectTransform _rect;

        private void Awake()
        {
            _rect = transform as RectTransform;
            _baseScale = transform.localScale;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_isHeld) return;
            _isHeld = true;
            if (_rect != null) _rect.localScale = _baseScale * 0.93f;
            Pressed?.Invoke();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            ReleaseIfHeld();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            // Keep Parry held even if thumb drifts slightly; movement/other buttons release on up.
            // Releasing only on PointerUp matches standard mobile action game feel.
        }

        private void OnDisable()
        {
            ReleaseIfHeld();
        }

        private void ReleaseIfHeld()
        {
            if (!_isHeld) return;
            _isHeld = false;
            if (_rect != null) _rect.localScale = _baseScale;
            Released?.Invoke();
        }
    }
}
