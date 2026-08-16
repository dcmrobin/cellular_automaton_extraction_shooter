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
    private Color[] previousSnapshot; // For detecting CA growth into player
    private bool snapshotReady;
    private bool drawnOnce;

    void Start()
    {
        var shader = caController.cellularAutomaton;

        // Use the safe kernel lookup from CAController
        kernelPlayerClear = caController.GetKernelIndex("PlayerClear");
        kernelPlayerDraw = caController.GetKernelIndex("PlayerDraw");

        if (kernelPlayerClear < 0 || kernelPlayerDraw < 0)
        {
            Debug.LogError("CA-REAPER: Player kernels not found! Check compute shader.");
            return;
        }

        origin = new Vector2Int(caController.width / 2, caController.height / 2);
        prevOrigin = origin;

        BuildOffsetsBuffer();
    }

    void Dispatch(int kernel)
    {
        if (kernel < 0)
        {
            Debug.LogError("CA-REAPER: Cannot dispatch player kernel - invalid index");
            return;
        }

        int groups = Mathf.Max(1, Mathf.CeilToInt(offsetCount / 64f));
        caController.cellularAutomaton.Dispatch(kernel, groups, 1, 1);
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
            
            // Initialize snapshot after first draw
            RequestSnapshot();
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

        // Check if the ENTIRE player shape can fit at the new position
        if (!CanFitAt(candidate))
            return; // Can't move - player is blocked

        origin = candidate;

        ClearPlayer(prevOrigin);
        DrawPlayer();
        prevOrigin = origin;
    }

    bool CanFitAt(Vector2Int newOrigin)
    {
        // Check if any alive cell would overlap with solid CA cells
        for (int i = 0; i < offsetCount; i++)
        {
            if (!offsetAlive[i])
                continue;

            Vector2Int pos = newOrigin + offsets[i];

            // Check world bounds
            if (pos.x < 0 || pos.x >= caController.width || 
                pos.y < 0 || pos.y >= caController.height)
                return false;

            // Check if position is occupied by CA
            if (IsSolidAt(pos.x, pos.y))
                return false;
        }

        return true;
    }

    void HandleCAStepped()
    {
        if (isDead)
            return;

        // Check for cells eaten by CA growth BEFORE redrawing
        CheckForCAEatingPlayer();

        // Redraw player on top of the new CA state
        DrawPlayer();
        prevOrigin = origin;

        // Get fresh snapshot for movement collision
        RequestSnapshot();
    }

    void CheckForCAEatingPlayer()
    {
        if (!snapshotReady || previousSnapshot == null)
            return;

        // Request current CA state to compare with previous
        AsyncGPUReadback.Request(caController.CurrentTexture, 0, request =>
        {
            if (request.hasError)
                return;

            var currentData = request.GetData<Color>();
            
            bool anyEaten = false;
            bool vitalEaten = false;

            // Check each alive player cell to see if CA grew into it
            for (int i = 0; i < offsetCount; i++)
            {
                if (!offsetAlive[i])
                    continue;

                Vector2Int pos = origin + offsets[i];
                
                if (pos.x < 0 || pos.x >= caController.width || 
                    pos.y < 0 || pos.y >= caController.height)
                    continue;

                int index = pos.y * caController.width + pos.x;
                
                // Check if this cell went from empty to solid (CA grew into player)
                bool wasEmpty = !IsSolidInSnapshot(previousSnapshot[index]);
                bool nowSolid = IsSolidInSnapshot(currentData[index]);
                
                if (wasEmpty && nowSolid)
                {
                    // CA grew into this player cell - it's eaten
                    offsetAlive[i] = false;
                    anyEaten = true;
                    
                    if (i == vitalOffsetIndex)
                        vitalEaten = true;
                }
            }

            // Update previous snapshot for next comparison
            currentData.CopyTo(previousSnapshot);

            if (vitalEaten)
            {
                Die(origin);
                return;
            }

            if (anyEaten)
            {
                // Redistribute remaining cells to compact the body
                RedistributeAliveSegments();
                UploadAliveBuffer();
                
                // Clear and redraw with new shape
                ClearPlayer(prevOrigin);
                DrawPlayer();
            }
        });
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

        // Build preferred ordering (center outward) for redistribution
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
        int aliveCountNow = 0;
        for (int i = 0; i < offsetCount; i++) 
            if (offsetAlive[i]) 
                aliveCountNow++;

        if (aliveCountNow == 0)
            return;

        // Pack alive cells into the most compact arrangement
        var newOffsets = new Vector2Int[offsetCount];
        var newAlive = new bool[offsetCount];

        for (int i = 0; i < offsetCount; i++)
        {
            newOffsets[i] = preferredOffsets[i];
            newAlive[i] = i < aliveCountNow;
        }

        // Find new vital offset index (should be 0 since preferredOffsets[0] is center)
        vitalOffsetIndex = -1;
        for (int i = 0; i < offsetCount; i++)
        {
            if (newOffsets[i] == Vector2Int.zero)
            {
                vitalOffsetIndex = i;
                break;
            }
        }

        offsets = newOffsets;
        offsetAlive = newAlive;

        offsetsBuffer.SetData(offsets);
        UploadAliveBuffer();
    }

    void RequestSnapshot()
    {
        if (caController.CurrentTexture == null)
            return;

        AsyncGPUReadback.Request(caController.CurrentTexture, 0, request =>
        {
            if (request.hasError)
                return;

            var data = request.GetData<Color>();

            if (solidSnapshot == null || solidSnapshot.Length != data.Length)
                solidSnapshot = new Color[data.Length];

            data.CopyTo(solidSnapshot);
            snapshotReady = true;
            
            // Initialize previous snapshot if this is the first time
            if (previousSnapshot == null)
            {
                previousSnapshot = new Color[data.Length];
                data.CopyTo(previousSnapshot);
            }
        });
    }

    void Die(Vector2Int atOrigin)
    {
        if (isDead)
            return;

        isDead = true;

        for (int i = 0; i < offsetCount; i++)
            offsetAlive[i] = false;

        UploadAliveBuffer();

        ClearPlayer(atOrigin);
        ClearPlayer(origin);

        Debug.Log("CA-REAPER: Vital cell eaten - player died.");
        OnPlayerDied?.Invoke();
    }

    bool IsSolidInSnapshot(Color cell)
    {
        float a = cell.a;

        // Check for gun CA (alpha 20-30)
        if (a >= 20.0f && a < 30.0f)
            return true;

        // Check for main CA
        if (caController.Decay > 1)
            return a > 0.5f && a < caController.Decay - 0.5f;

        return a > 0.5f;
    }

    bool IsSolidAt(int x, int y)
    {
        if (x < 0 || x >= caController.width || y < 0 || y >= caController.height)
            return true; // world edge acts as a wall

        if (!snapshotReady)
            return false;

        return IsSolidInSnapshot(solidSnapshot[y * caController.width + x]);
    }

    void OnDestroy()
    {
        offsetsBuffer?.Release();
        aliveBuffer?.Release();
    }
}