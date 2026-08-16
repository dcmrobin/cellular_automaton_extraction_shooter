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
    private Vector2Int[] preferredOffsets;
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
        shader.SetInt("PlayerRedistributeEnabled", 0);
        shader.SetFloat("PlayerRedistributeFactor", 0.0f);

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
        shader.SetInt("PlayerRedistributeEnabled", 0);
        shader.SetFloat("PlayerRedistributeFactor", 0.0f);

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

        // Build a preferred ordering of offsets that packs cells from the
        // centre outward. This is used when redistributing remaining cells
        // after some are eaten so the body compacts around the vital cell.
        preferredOffsets = new Vector2Int[offsetCount];
        Array.Copy(offsets, preferredOffsets, offsetCount);
        Array.Sort(preferredOffsets, (a, b) =>
        {
            int da = a.x * a.x + a.y * a.y;
            int db = b.x * b.x + b.y * b.y;
            if (da != db) return da - db;
            if (a.y != b.y) return a.y - b.y;
            return a.x - b.x;
        });

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

    void RedistributeAliveSegments()
    {
        // Count alive segments (vital is guaranteed alive here)
        int aliveCountNow = 0;
        for (int i = 0; i < offsetCount; i++) if (offsetAlive[i]) aliveCountNow++;

        if (aliveCountNow == 0)
            return;

        // Build new offsets: pack alive segments into the preferredOffsets[0..aliveCountNow-1]
        var newOffsets = new Vector2Int[offsetCount];
        var newAlive = new bool[offsetCount];

        for (int i = 0; i < offsetCount; i++)
        {
            if (i < aliveCountNow)
            {
                newOffsets[i] = preferredOffsets[i];
                newAlive[i] = true;
            }
            else
            {
                newOffsets[i] = preferredOffsets[i];
                newAlive[i] = false;
            }
        }

        // Ensure vital remains identified (preferredOffsets[0] should be zero)
        vitalOffsetIndex = -1;
        for (int i = 0; i < offsetCount; i++)
        {
            if (newOffsets[i] == Vector2Int.zero)
            {
                vitalOffsetIndex = i;
                break;
            }
        }

        // Replace arrays and upload to GPU
        offsets = newOffsets;
        offsetAlive = newAlive;

        // upload offsets and alive buffers
        offsetsBuffer.SetData(offsets);

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
        {
            RedistributeAliveSegments();
        }
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