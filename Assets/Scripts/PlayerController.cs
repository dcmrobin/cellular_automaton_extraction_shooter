using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class PlayerController : MonoBehaviour
{
    [Header("References")]
    public CAController caController;

    [Header("Shape")]
    [Tooltip("Radius (in cells) of the filled circle that makes up the player's body.")]
    public int radius = 4;

    [Header("Identity")]
    public Color playerColor = new Color(0.2f, 0.6f, 1f);
    [Tooltip("Colour of the single central vital cell. Losing it is an instant kill.")]
    public Color vitalColor = new Color(1f, 1f, 0.2f);

    [Header("Movement")]
    [Tooltip("Grid cells moved per second while a direction key is held.")]
    public float moveRate = 8f;

    public event Action OnPlayerDied;
    public Vector2Int Origin => origin;
    public bool IsDead => isDead;

    public float HealthFraction
    {
        get
        {
            if (offsetAlive == null || offsetCount == 0)
                return 0f;

            int alive = 0;
            for (int i = 0; i < offsetCount; i++)
                if (offsetAlive[i]) alive++;

            return alive / (float)offsetCount;
        }
    }

    private Vector2Int origin;
    private Vector2Int prevOrigin;
    private float moveTimer;
    private bool isDead;

    private Vector2Int[] offsets;
    private bool[] offsetAlive;
    private int[] aliveInts;
    private int offsetCount;
    private int vitalOffsetIndex = -1;

    private ComputeBuffer offsetsBuffer;
    private ComputeBuffer aliveBuffer;

    private int kernelPlayerClear;
    private int kernelPlayerDraw;

    private Color[] solidSnapshot;
    private bool snapshotReady;
    private bool drawnOnce;


    void Start()
    {
        var shader = caController.cellularAutomaton;

        kernelPlayerClear = shader.FindKernel("PlayerClear");
        kernelPlayerDraw = shader.FindKernel("PlayerDraw");

        origin = new Vector2Int(caController.width / 2, caController.height / 2);
        prevOrigin = origin;

        BuildOffsetsBuffer();
    }


    void OnEnable()
    {
        if (caController != null)
            caController.OnCAStepped += HandleCAStepped;
    }


    void OnDisable()
    {
        if (caController != null)
            caController.OnCAStepped -= HandleCAStepped;
    }


    void Update()
    {
        if (!drawnOnce)
        {
            if (caController.CurrentTexture == null)
                return;

            DrawPlayer();
            prevOrigin = origin;
            drawnOnce = true;
        }

        HandleMovement();
    }


    void HandleMovement()
    {
        if (isDead)
            return;

        moveTimer += Time.deltaTime;

        float interval = 1f / Mathf.Max(0.01f, moveRate);

        if (moveTimer < interval)
            return;

        Vector2Int dir = Vector2Int.zero;

        if (Input.GetKey(KeyCode.W)) dir.y += 1;
        if (Input.GetKey(KeyCode.S)) dir.y -= 1;
        if (Input.GetKey(KeyCode.A)) dir.x -= 1;
        if (Input.GetKey(KeyCode.D)) dir.x += 1;

        if (dir == Vector2Int.zero)
        {
            moveTimer = interval;
            return;
        }

        moveTimer = 0f;

        Vector2Int candidate = origin + dir;

        if (IsSolidAt(candidate.x, candidate.y))
            return;

        origin = candidate;

        ClearPlayer(prevOrigin);
        DrawPlayer();
        prevOrigin = origin;
    }


    void HandleCAStepped()
    {
        if (isDead)
            return;

        DrawPlayer();
        prevOrigin = origin;

        RequestSnapshot();
    }


    void DrawPlayer()
    {
        RenderTexture world = caController.CurrentTexture;
        if (world == null || offsetsBuffer == null)
            return;

        var shader = caController.cellularAutomaton;

        shader.SetTexture(kernelPlayerDraw, "World", world);
        shader.SetBuffer(kernelPlayerDraw, "PlayerOffsets", offsetsBuffer);
        shader.SetBuffer(kernelPlayerDraw, "PlayerAlive", aliveBuffer);
        shader.SetInt("PlayerOffsetCount", offsetCount);
        shader.SetInts("PlayerOrigin", origin.x, origin.y);
        shader.SetVector("PlayerColor", playerColor);
        shader.SetVector("VitalColor", vitalColor);
        shader.SetInt("VitalOffsetIndex", vitalOffsetIndex);

        Dispatch(kernelPlayerDraw);
    }


    void ClearPlayer(Vector2Int atOrigin)
    {
        RenderTexture world = caController.CurrentTexture;
        if (world == null || offsetsBuffer == null)
            return;

        var shader = caController.cellularAutomaton;

        shader.SetTexture(kernelPlayerClear, "World", world);
        shader.SetBuffer(kernelPlayerClear, "PlayerOffsets", offsetsBuffer);
        shader.SetInt("PlayerOffsetCount", offsetCount);
        shader.SetInts("PlayerPrevOrigin", atOrigin.x, atOrigin.y);

        Dispatch(kernelPlayerClear);
    }


    void Dispatch(int kernel)
    {
        int groups = Mathf.Max(1, Mathf.CeilToInt(offsetCount / 64f));
        caController.cellularAutomaton.Dispatch(kernel, groups, 1, 1);
    }


    void BuildOffsetsBuffer()
    {
        var list = new List<Vector2Int>();
        int r2 = radius * radius;

        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= r2)
                    list.Add(new Vector2Int(x, y));
            }
        }

        offsets = list.ToArray();
        offsetCount = offsets.Length;

        offsetAlive = new bool[offsetCount];
        aliveInts = new int[offsetCount];

        for (int i = 0; i < offsetCount; i++)
        {
            offsetAlive[i] = true;
            aliveInts[i] = 1;

            if (offsets[i] == Vector2Int.zero)
                vitalOffsetIndex = i;
        }

        offsetsBuffer?.Release();
        offsetsBuffer = new ComputeBuffer(offsetCount, sizeof(int) * 2);
        offsetsBuffer.SetData(offsets);

        aliveBuffer?.Release();
        aliveBuffer = new ComputeBuffer(offsetCount, sizeof(int));
        aliveBuffer.SetData(aliveInts);
    }

    void UploadAliveBuffer()
    {
        for (int i = 0; i < offsetCount; i++)
            aliveInts[i] = offsetAlive[i] ? 1 : 0;

        aliveBuffer.SetData(aliveInts);
    }


    // --- Eaten-cell detection, piggybacked on the movement-collision snapshot ---

    void RequestSnapshot()
    {
        if (caController.CurrentTexture == null)
            return;

        Vector2Int requestOrigin = origin;

        AsyncGPUReadback.Request(caController.CurrentTexture, 0, request =>
        {
            OnSnapshotReceived(request, requestOrigin);
        });
    }

    void OnSnapshotReceived(AsyncGPUReadbackRequest request, Vector2Int snapshotOrigin)
    {
        if (request.hasError)
            return;

        var data = request.GetData<Color>();

        if (solidSnapshot == null || solidSnapshot.Length != data.Length)
            solidSnapshot = new Color[data.Length];

        data.CopyTo(solidSnapshot);
        snapshotReady = true;

        CheckForEatenCells(snapshotOrigin);
    }

    void CheckForEatenCells(Vector2Int atOrigin)
    {
        if (isDead)
            return;

        bool anyChanged = false;
        bool vitalEaten = false;

        for (int i = 0; i < offsetCount; i++)
        {
            if (!offsetAlive[i])
                continue;

            Vector2Int pos = atOrigin + offsets[i];

            if (IsSolidInSnapshot(pos.x, pos.y))
            {
                offsetAlive[i] = false;
                anyChanged = true;

                if (i == vitalOffsetIndex)
                    vitalEaten = true;
            }
        }

        if (vitalEaten)
        {
            Die(atOrigin);
            return;
        }

        if (anyChanged)
            UploadAliveBuffer();
    }

    void Die(Vector2Int atOrigin)
    {
        if (isDead)
            return;

        isDead = true;

        for (int i = 0; i < offsetCount; i++)
            offsetAlive[i] = false;

        UploadAliveBuffer();

        // Clear at both the snapshot-time origin and the live origin, in
        // case the player moved in the gap between the two - cheap, and
        // the second call is a no-op if they're the same.
        ClearPlayer(atOrigin);
        ClearPlayer(origin);

        Debug.Log("CA-REAPER: Vital cell eaten - player died.");
        OnPlayerDied?.Invoke();
    }


    bool IsSolidInSnapshot(int x, int y)
    {
        if (!snapshotReady)
            return false;

        if (x < 0 || x >= caController.width || y < 0 || y >= caController.height)
            return false;

        float a = solidSnapshot[y * caController.width + x].a;

        if (caController.Decay > 1)
            return a > 0.5f && a < caController.Decay - 0.5f;

        return a > 0.5f;
    }

    bool IsSolidAt(int x, int y)
    {
        if (x < 0 || x >= caController.width || y < 0 || y >= caController.height)
            return true; // world edge acts as a wall for movement

        return IsSolidInSnapshot(x, y);
    }


    void OnDestroy()
    {
        offsetsBuffer?.Release();
        aliveBuffer?.Release();
    }
}