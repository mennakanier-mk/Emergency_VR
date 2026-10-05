using UnityEngine;
using Unity.XR.CoreUtils;

/// <summary>
/// VR Test Movement Script
/// يسمح بتحريك الـ XR Origin بالكيبورد لتجربة بيئة الـ VR في الـ Editor
/// 
/// كيفية الاستخدام / How to use:
///   1. حط الـ script ده على نفس الـ GameObject اللي فيه XR Origin (XR Rig)
///   2. شغل Play في Unity
///   3. تحكم بالحركة:
///      - W / Arrow Up     → قدام  (Forward)
///      - S / Arrow Down   → ورا   (Backward)
///      - A / Arrow Left   → شمال  (Left)
///      - D / Arrow Right  → يمين  (Right)
///      - Q / E            → تدوير يسار / يمين (Rotate)
///      - Space            → فوق   (Up)
///      - Left Ctrl        → تحت   (Down)
///      - Left Shift       → ضغطه للسرعة الزيادة (Sprint)
/// </summary>
public class VRTestMovement : MonoBehaviour
{
    [Header("Movement Settings")]
    [Tooltip("سرعة الحركة الأساسية")]
    public float moveSpeed = 3f;

    [Tooltip("سرعة الحركة عند الضغط على Shift")]
    public float sprintSpeed = 7f;

    [Tooltip("سرعة الدوران (Rotate) بالدرجات في الثانية")]
    public float rotateSpeed = 60f;

    [Header("Movement Options")]
    [Tooltip("الحركة تبع اتجاه الكاميرا أو المحاور الثابتة؟")]
    public bool cameraRelativeMovement = true;

    // ─── Private ───────────────────────────────────────────────────
    XROrigin  m_XROrigin;
    Transform m_CameraTransform;

    void Awake()
    {
        // جرب تلاقي XROrigin على نفس الـ object أو في الـ children
        m_XROrigin = GetComponent<XROrigin>();
        if (m_XROrigin == null)
            m_XROrigin = GetComponentInChildren<XROrigin>();

        if (m_XROrigin == null)
        {
            Debug.LogError("[VRTestMovement] مش لاقي XROrigin! تأكد إن الـ script مضاف على XR Origin (XR Rig)");
            enabled = false;
            return;
        }

        m_CameraTransform = m_XROrigin.Camera != null ? m_XROrigin.Camera.transform : Camera.main?.transform;
    }

    void Update()
    {
        HandleMovement();
        HandleRotation();
    }

    void HandleMovement()
    {
        // قراءة الـ input
        float horizontal = 0f;
        float vertical   = 0f;
        float upDown     = 0f;

        // أمام / ورا
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    vertical   += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  vertical   -= 1f;

        // يمين / شمال
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) horizontal += 1f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  horizontal -= 1f;

        // فوق / تحت
        if (Input.GetKey(KeyCode.Space))        upDown += 1f;
        if (Input.GetKey(KeyCode.LeftControl))  upDown -= 1f;

        if (horizontal == 0f && vertical == 0f && upDown == 0f)
            return;

        // سرعة بناءً على Shift
        float speed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : moveSpeed;

        // اتجاه الحركة
        Vector3 moveDir;

        if (cameraRelativeMovement && m_CameraTransform != null)
        {
            // اتجاه الأمام من الكاميرا (بس على المستوى الأفقي)
            Vector3 camForward = m_CameraTransform.forward;
            Vector3 camRight   = m_CameraTransform.right;

            // استبعد المحور الرأسي من forward عشان ما نطيرش فوق
            camForward.y = 0f;
            camForward.Normalize();
            camRight.y = 0f;
            camRight.Normalize();

            moveDir = (camForward * vertical) + (camRight * horizontal) + (Vector3.up * upDown);
        }
        else
        {
            // حركة على المحاور الثابتة للعالم
            moveDir = new Vector3(horizontal, upDown, vertical);
        }

        // تطبيق الحركة على الـ XR Origin
        transform.position += moveDir * (speed * Time.deltaTime);
    }

    void HandleRotation()
    {
        float rotInput = 0f;

        if (Input.GetKey(KeyCode.Q)) rotInput -= 1f;
        if (Input.GetKey(KeyCode.E)) rotInput += 1f;

        if (rotInput == 0f) return;

        // دوران حول المحور Y (يسار / يمين)
        transform.Rotate(0f, rotInput * rotateSpeed * Time.deltaTime, 0f, Space.World);
    }

    // ─── رسم Gizmo في الـ Editor عشان يبين الاتجاهات ───────────────
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawRay(transform.position, transform.forward * 2f);
        Gizmos.color = Color.red;
        Gizmos.DrawRay(transform.position, transform.right * 1f);
        Gizmos.color = Color.blue;
        Gizmos.DrawRay(transform.position, Vector3.up * 1f);
    }
}
