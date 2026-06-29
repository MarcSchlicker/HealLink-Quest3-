using UnityEngine;

public class ChildFollowParentWorld : MonoBehaviour
{
    [Header("Parent to follow")]
    public Transform parentTarget;

    [Header("Relative values (auto-filled, copyable)")]
    public Vector3 relativePosition;
    public Vector3 relativeRotation;
    public Vector3 relativeScale;

    void Start()
    {
        if (parentTarget == null && transform.parent != null)
            parentTarget = transform.parent;

        // Initial relative values berechnen
        UpdateRelativeValues();
    }

    void LateUpdate()
    {
        if (parentTarget == null) return;

        // 1. Position
        transform.position = parentTarget.TransformPoint(relativePosition);

        // 2. Rotation
        transform.rotation = parentTarget.rotation * Quaternion.Euler(relativeRotation);

        // 3. Scale
        Vector3 parentScale = parentTarget.lossyScale;
        transform.localScale = new Vector3(
            relativeScale.x * parentScale.x,
            relativeScale.y * parentScale.y,
            relativeScale.z * parentScale.z
        );
    }

    [ContextMenu("Update Relative Values From Current Transform")]
    public void UpdateRelativeValues()
    {
        if (parentTarget == null) return;

        // relative Position
        relativePosition = parentTarget.InverseTransformPoint(transform.position);

        // relative Rotation
        Quaternion relRot = Quaternion.Inverse(parentTarget.rotation) * transform.rotation;
        relativeRotation = relRot.eulerAngles;

        // relative Scale
        Vector3 parentScale = parentTarget.lossyScale;
        Vector3 worldScale = transform.lossyScale;

        relativeScale = new Vector3(
            worldScale.x / parentScale.x,
            worldScale.y / parentScale.y,
            worldScale.z / parentScale.z
        );
    }

    [ContextMenu("Copy World Transform To Console")]
    public void CopyWorldTransform()
    {
        Debug.Log(
            $"World Position: {transform.position}\n" +
            $"World Rotation: {transform.rotation.eulerAngles}\n" +
            $"World Scale: {transform.lossyScale}"
        );
    }
    // --- COPY LOCAL VALUES FOR EDITOR ---
    [ContextMenu("Print Local Transform For Editor")]
    public void PrintLocalForEditor()
    {
        Debug.Log(
            "Paste these values into the Transform in Edit Mode:\n" +
            $"localPosition = {transform.localPosition}\n" +
            $"localRotation = {transform.localEulerAngles}\n" +
            $"localScale    = {transform.localScale}"
        );
    }

}
