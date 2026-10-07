using UnityEngine;
using UnityEngine.EventSystems;

// 화면 왼쪽 아래 가상 스틱. Value는 -1~1 범위의 이동 방향.
public class VirtualStick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    public RectTransform knob;
    public float radius = 60f;
    public Vector2 Value { get; private set; }

    RectTransform rt;

    void Awake() { rt = (RectTransform)transform; }

    public void OnPointerDown(PointerEventData e) { OnDrag(e); }

    public void OnDrag(PointerEventData e)
    {
        if (rt == null) rt = (RectTransform)transform;
        Vector2 local;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out local))
        {
            Vector2 v = Vector2.ClampMagnitude(local, radius);
            if (knob != null) knob.anchoredPosition = v;
            Value = v / radius;
        }
    }

    public void OnPointerUp(PointerEventData e)
    {
        Value = Vector2.zero;
        if (knob != null) knob.anchoredPosition = Vector2.zero;
    }
}
