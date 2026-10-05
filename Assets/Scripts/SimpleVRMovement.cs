using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Universal VR Movement script for Unity Editor testing.
/// Attach this script to "XR Origin Hands (XR Rig)" in the Hierarchy.
/// Compatible with CharacterController, XR Rig, and Unity New Input System.
/// </summary>
public class SimpleVRMovement : MonoBehaviour
{
    [Header("Movement Settings")]
    [Tooltip("Movement speed in meters per second.")]
    public float moveSpeed = 4.0f;

    [Tooltip("Rotation speed in degrees per second.")]
    public float rotateSpeed = 80.0f;

    [Header("Jump & Gravity Settings")]
    [Tooltip("Height of the jump in meters.")]
    public float jumpHeight = 1.2f;

    [Tooltip("Gravity strength applied when grounded or airborne.")]
    public float gravity = -9.81f;

    [Header("Mouse Look Settings")]
    [Tooltip("Enable mouse look when holding Right Mouse Button.")]
    public bool enableMouseLook = true;
    public float mouseSensitivity = 2.0f;

    [Tooltip("Editor-only free flight with Space/Shift. Leave off for grounded walkthroughs.")]
    public bool allowVerticalMovement = false;

    private CharacterController characterController;
    private float rotationX = 0f;
    private float rotationY = 0f;
    private Transform movementRoot;
    private Transform view;
    private Quaternion initialViewRotation;
    private float verticalVelocity = 0f;

    void Start()
    {
        // Auto-detect CharacterController on this GameObject or Parent
        characterController = GetComponent<CharacterController>();
        if (characterController == null)
        {
            characterController = GetComponentInParent<CharacterController>();
        }

        movementRoot = characterController != null ? characterController.transform : transform;
        var camera = GetComponentInChildren<Camera>();
        view = camera != null ? camera.transform : null;
        if (view != null) initialViewRotation = view.localRotation;
        Vector3 currentRotation = movementRoot.eulerAngles;
        rotationY = currentRotation.y;
        rotationX = 0f;
    }

    void Update()
    {
        // Real headset input and Android locomotion remain owned by XR Interaction Toolkit.
        if (!Application.isEditor || UnityEngine.XR.XRSettings.isDeviceActive) return;
        float horizontal = 0f;
        float vertical = 0f;
        bool moveUp = false;
        bool moveDown = false;
        bool jumpPressed = false;
        bool rotateLeft = false;
        bool rotateRight = false;
        bool mouseLookActive = false;
        float mouseX = 0f;
        float mouseY = 0f;

#if ENABLE_INPUT_SYSTEM
        // New Input System handling (Keyboard.current & Mouse.current)
        var kbd = Keyboard.current;
        if (kbd != null)
        {
            if (kbd.wKey.isPressed || kbd.upArrowKey.isPressed) vertical += 1f;
            if (kbd.sKey.isPressed || kbd.downArrowKey.isPressed) vertical -= 1f;
            if (kbd.dKey.isPressed || kbd.rightArrowKey.isPressed) horizontal += 1f;
            if (kbd.aKey.isPressed || kbd.leftArrowKey.isPressed) horizontal -= 1f;

            if (kbd.spaceKey.wasPressedThisFrame) jumpPressed = true;
            if (kbd.spaceKey.isPressed) moveUp = true;
            if (kbd.leftShiftKey.isPressed) moveDown = true;

            if (kbd.qKey.isPressed) rotateLeft = true;
            if (kbd.eKey.isPressed) rotateRight = true;
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                mouseLookActive = true;
                Vector2 delta = mouse.delta.ReadValue();
                mouseX = delta.x * 0.1f;
                mouseY = delta.y * 0.1f;
            }
        }
#else
        // Legacy Input Manager fallback
        horizontal = Input.GetAxis("Horizontal");
        vertical = Input.GetAxis("Vertical");
        jumpPressed = Input.GetKeyDown(KeyCode.Space);
        moveUp = Input.GetKey(KeyCode.Space);
        moveDown = Input.GetKey(KeyCode.LeftShift);
        rotateLeft = Input.GetKey(KeyCode.Q);
        rotateRight = Input.GetKey(KeyCode.E);
        mouseLookActive = Input.GetMouseButton(1);
        mouseX = Input.GetAxis("Mouse X");
        mouseY = Input.GetAxis("Mouse Y");
#endif

        // Calculate direction relative to camera / rig orientation
        Vector3 inputDir = new Vector3(horizontal, 0, vertical);
        if (inputDir.magnitude > 1f) inputDir.Normalize();

        // Looking down must never turn walking into downward flight.
        Vector3 forward = Vector3.ProjectOnPlane(view != null ? view.forward : movementRoot.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.001f) forward = movementRoot.forward;
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        Vector3 moveVector = (forward * inputDir.z + right * inputDir.x) * moveSpeed;

        if (allowVerticalMovement)
        {
            if (moveUp) moveVector.y += moveSpeed;
            if (moveDown) moveVector.y -= moveSpeed;
        }
        else
        {
            // Grounded Jump & Gravity handling
            bool isGrounded = characterController != null && characterController.isGrounded;
            if (isGrounded)
            {
                if (verticalVelocity < 0f)
                {
                    verticalVelocity = -2f;
                }
                if (jumpPressed)
                {
                    verticalVelocity = Mathf.Sqrt(jumpHeight * 2f * Mathf.Abs(gravity));
                }
            }
            else
            {
                verticalVelocity += gravity * Time.deltaTime;
            }

            moveVector.y = verticalVelocity;
        }

        // Apply Movement using CharacterController if attached, else position
        if (characterController != null && characterController.enabled)
        {
            characterController.Move(moveVector * Time.deltaTime);
        }
        // Never bypass collisions when the controller is absent or temporarily disabled.

        // Apply Keyboard Rotation
        rotationY = movementRoot.eulerAngles.y;
        if (rotateLeft) rotationY -= rotateSpeed * Time.deltaTime;
        if (rotateRight) rotationY += rotateSpeed * Time.deltaTime;

        // Apply Mouse Look
        if (enableMouseLook && mouseLookActive)
        {
            rotationY += mouseX * mouseSensitivity * 10f;
            rotationX -= mouseY * mouseSensitivity * 10f;
            rotationX = Mathf.Clamp(rotationX, -85f, 85f);

        }
        // Keep the CharacterController upright; pitch only the view.
        movementRoot.rotation = Quaternion.Euler(0, rotationY, 0);
    }

    void LateUpdate()
    {
        if (!Application.isEditor || UnityEngine.XR.XRSettings.isDeviceActive || !enableMouseLook || view == null) return;
        view.localRotation = initialViewRotation * Quaternion.Euler(rotationX, 0, 0);
    }
}
