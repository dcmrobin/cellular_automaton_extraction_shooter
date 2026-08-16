using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    public enum PlayPlane { XZ_TopDown, XY_FacingCamera }

    [Header("Target")]
    public PlayerController player;
    public CAController caController;

    [Header("Follow")]
    [Tooltip("How snappy the follow is - higher catches up faster.")]
    public float followSpeed = 5f;

    [Tooltip("Camera offset from the tracked point, along the axis perpendicular to the play plane.")]
    public float distance = 10f;

    [Header("Scene Layout")]
    [Tooltip("Which plane the CA quad lies in. If the camera doesn't track correctly, flip this.")]
    public PlayPlane playPlane = PlayPlane.XZ_TopDown;

    void LateUpdate()
    {
        if (player == null || caController == null || caController.targetRenderer == null)
            return;

        Vector3 worldPos = GridToWorld(player.Origin);
        player.transform.position = new Vector3(worldPos.x, worldPos.y, 0f);

        Vector3 desired = playPlane == PlayPlane.XZ_TopDown
            ? new Vector3(worldPos.x, worldPos.y + distance, worldPos.z)
            : new Vector3(worldPos.x, worldPos.y, worldPos.z - distance);

        transform.position = Vector3.Lerp(
            transform.position,
            desired,
            1f - Mathf.Exp(-followSpeed * Time.deltaTime)
        );
        // Rotation is left alone deliberately, so whatever angle/tilt you've
        // already set up on the camera is preserved.
    }

    Vector3 GridToWorld(Vector2Int gridPos)
    {
        Bounds b = caController.targetRenderer.bounds;

        float u = gridPos.x / (float)caController.width;
        float v = gridPos.y / (float)caController.height;

        if (playPlane == PlayPlane.XZ_TopDown)
        {
            float x = Mathf.Lerp(b.min.x, b.max.x, u);
            float z = Mathf.Lerp(b.min.z, b.max.z, v);
            return new Vector3(x, b.center.y, z);
        }
        else
        {
            float x = Mathf.Lerp(b.min.x, b.max.x, u);
            float y = Mathf.Lerp(b.min.y, b.max.y, v);
            return new Vector3(x, y, b.center.z);
        }
    }
}