using System;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [Header("References")]
    public CAController caController;

    [Header("Shape")]
    public int radius = 4;

    [Header("Identity")]
    public Color playerColor = new Color(0.2f, 0.6f, 1f);
    public Color vitalColor = new Color(1f, 1f, 0.2f);

    [Header("Movement")]
    public float moveRate = 8f;

    public event Action OnPlayerDied;
    public Vector2Int Origin => origin;
    public bool IsDead => isDead;
    public Vector2Int[] Offsets => offsets;
    public bool[] OffsetAlive => offsetAlive;
    public float HealthFraction
    {
        get
        {
            if (offsetAlive == null || offsetCount == 0) return 0f;
            int alive = 0;
            for (int i = 0; i < offsetCount; i++) if (offsetAlive[i]) alive++;
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

    // Double buffering for compute buffers
    private ComputeBuffer[] offsetsBuffers;
    private ComputeBuffer[] aliveBuffers;
    private int currentBufferIndex = 0;
    private int nextBufferIndex = 1;
    
    // Deferred update flags
    private bool buffersNeedUpdate = false;
    private bool offsetsChanged = false;
    private bool aliveChanged = false;

    private int kernelPlayerClear;
    private int kernelPlayerDraw;

    private Color[] solidSnapshot;
    private Color[] previousSnapshot;
    private bool snapshotReady;
    private bool drawnOnce;
    private bool collisionCheckInProgress = false;

    void Start()
    {
        // Use the safe method to get kernel indices
        if (caController != null)
        {
            kernelPlayerClear = caController.GetKernelIndex("PlayerClear");
            kernelPlayerDraw = caController.GetKernelIndex("PlayerDraw");
            
            if (kernelPlayerClear < 0 || kernelPlayerDraw < 0)
            {
                Debug.LogError($"CA-REAPER: Player kernels not found! Clear: {kernelPlayerClear}, Draw: {kernelPlayerDraw}");
                return;
            }
        }
        else
        {
            Debug.LogError("CA-REAPER: CAController is null in PlayerController!");
            return;
        }

        origin = new Vector2Int(caController.width / 2, caController.height / 2);
        prevOrigin = origin;
        BuildOffsetsBuffer();
    }

    public bool IsCellOccupied(Vector2Int worldPos)
    {
        if (isDead) return false;
        for (int i = 0; i < offsetCount; i++)
            if (offsetAlive[i] && origin + offsets[i] == worldPos)
                return true;
        return false;
    }

    void Dispatch(int kernel, int bufferIndex)
    {
        if (kernel < 0)
        {
            Debug.LogWarning($"CA-REAPER: Player kernel invalid (index: {kernel}), skipping dispatch");
            return;
        }
        
        if (caController == null || caController.cellularAutomaton == null)
        {
            Debug.LogWarning("CA-REAPER: CAController or ComputeShader is null");
            return;
        }
        
        try
        {
            int groups = Mathf.Max(1, Mathf.CeilToInt(offsetCount / 64f));
            caController.cellularAutomaton.Dispatch(kernel, groups, 1, 1);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"CA-REAPER: Error dispatching Player kernel {kernel}: {e.Message}");
        }
    }

    void OnEnable()
    {
        if (caController != null) caController.OnCAStepped += HandleCAStepped;
    }

    void OnDisable()
    {
        if (caController != null) caController.OnCAStepped -= HandleCAStepped;
    }

    void LateUpdate()
    {
        // Apply deferred buffer updates at end of frame to minimize sync points
        if (buffersNeedUpdate)
        {
            ApplyBufferUpdates();
        }
    }

    void Update()
    {
        if (!drawnOnce)
        {
            if (caController.CurrentTexture == null) return;
            DrawPlayer();
            prevOrigin = origin;
            drawnOnce = true;
            RequestSnapshot();
        }
        HandleMovement();
    }

    void HandleMovement()
    {
        if (isDead) return;
        moveTimer += Time.deltaTime;
        float interval = 1f / Mathf.Max(0.01f, moveRate);
        if (moveTimer < interval) return;

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
        if (!CanFitAt(candidate)) return;

        origin = candidate;
        ClearPlayer(prevOrigin);
        DrawPlayer();
        prevOrigin = origin;
    }

    bool CanFitAt(Vector2Int newOrigin)
    {
        // Use cached snapshot if available
        if (caController.LatestSnapshot != null)
        {
            for (int i = 0; i < offsetCount; i++)
            {
                if (!offsetAlive[i]) continue;
                Vector2Int pos = newOrigin + offsets[i];
                if (pos.x < 0 || pos.x >= caController.width || pos.y < 0 || pos.y >= caController.height) return false;
                if (caController.IsSolid(caController.LatestSnapshot[pos.y * caController.width + pos.x])) return false;
            }
            return true;
        }
        else if (snapshotReady)
        {
            // Fallback to local snapshot
            for (int i = 0; i < offsetCount; i++)
            {
                if (!offsetAlive[i]) continue;
                Vector2Int pos = newOrigin + offsets[i];
                if (pos.x < 0 || pos.x >= caController.width || pos.y < 0 || pos.y >= caController.height) return false;
                if (caController.IsSolid(solidSnapshot[pos.y * caController.width + pos.x])) return false;
            }
            return true;
        }
        return true; // If no snapshot available, allow movement
    }

    void HandleCAStepped()
    {
        if (isDead) return;
        
        // Only check for collision if we're not already checking
        if (!collisionCheckInProgress)
        {
            CheckForCAEatingPlayer();
        }
        
        DrawPlayer();
        prevOrigin = origin;
    }

    void CheckForCAEatingPlayer()
    {
        if (collisionCheckInProgress) return;
        
        collisionCheckInProgress = true;
        
        // Use the centralized readback system
        caController.RequestColorData(currentData =>
        {
            collisionCheckInProgress = false;
            
            if (currentData == null) return;
            if (!snapshotReady || previousSnapshot == null)
            {
                // First snapshot, just store it
                if (previousSnapshot == null)
                {
                    previousSnapshot = new Color[currentData.Length];
                    currentData.CopyTo(previousSnapshot, 0);
                }
                snapshotReady = true;
                return;
            }

            bool anyEaten = false;
            bool vitalEaten = false;

            for (int i = 0; i < offsetCount; i++)
            {
                if (!offsetAlive[i]) continue;
                Vector2Int pos = origin + offsets[i];
                if (pos.x < 0 || pos.x >= caController.width || pos.y < 0 || pos.y >= caController.height) continue;
                int index = pos.y * caController.width + pos.x;
                bool wasEmpty = !caController.IsSolid(previousSnapshot[index]);
                bool nowSolid = caController.IsSolid(currentData[index]);
                if (wasEmpty && nowSolid)
                {
                    offsetAlive[i] = false;
                    anyEaten = true;
                    if (i == vitalOffsetIndex) vitalEaten = true;
                }
            }

            Array.Copy(currentData, previousSnapshot, currentData.Length);
            snapshotReady = true;

            if (vitalEaten)
            {
                Die(origin);
                return;
            }

            if (anyEaten)
            {
                RedistributeAliveSegments();
                ClearPlayer(prevOrigin);
                DrawPlayer();
            }
        });
    }

    void DrawPlayer()
    {
        RenderTexture world = caController.CurrentTexture;
        if (world == null)
        {
            Debug.LogWarning("CA-REAPER: World texture is null in DrawPlayer");
            return;
        }

        var shader = caController.cellularAutomaton;
        if (shader == null)
        {
            Debug.LogWarning("CA-REAPER: ComputeShader is null in DrawPlayer");
            return;
        }

        if (kernelPlayerDraw < 0)
        {
            kernelPlayerDraw = caController.GetKernelIndex("PlayerDraw");
            if (kernelPlayerDraw < 0)
            {
                Debug.LogError("CA-REAPER: PlayerDraw kernel not found!");
                return;
            }
        }

        int bufferIdx = GetCurrentBufferIndex();
        
        try
        {
            // Set all required textures and buffers
            shader.SetTexture(kernelPlayerDraw, "World", world);
            shader.SetBuffer(kernelPlayerDraw, "PlayerOffsets", offsetsBuffers[bufferIdx]);
            shader.SetBuffer(kernelPlayerDraw, "PlayerAlive", aliveBuffers[bufferIdx]);
            
            // Set all required parameters
            shader.SetInt("PlayerOffsetCount", offsetCount);
            shader.SetInts("PlayerOrigin", origin.x, origin.y);
            shader.SetVector("PlayerColor", playerColor);
            shader.SetVector("VitalColor", vitalColor);
            shader.SetInt("VitalOffsetIndex", vitalOffsetIndex);
            shader.SetInt("PlayerRedistributeEnabled", 0);
            shader.SetFloat("PlayerRedistributeFactor", 0f);
            shader.SetInt("Width", caController.width);
            shader.SetInt("Height", caController.height);
            shader.SetInt("Decay", caController.Decay);
            
            Dispatch(kernelPlayerDraw, bufferIdx);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"CA-REAPER: Error in DrawPlayer: {e.Message}");
        }
    }

    void ClearPlayer(Vector2Int atOrigin)
    {
        RenderTexture world = caController.CurrentTexture;
        if (world == null) return;

        var shader = caController.cellularAutomaton;
        if (shader == null || kernelPlayerClear < 0) return;

        if (kernelPlayerClear < 0)
        {
            kernelPlayerClear = caController.GetKernelIndex("PlayerClear");
            if (kernelPlayerClear < 0) return;
        }

        int bufferIdx = GetCurrentBufferIndex();
        
        try
        {
            shader.SetTexture(kernelPlayerClear, "World", world);
            shader.SetBuffer(kernelPlayerClear, "PlayerOffsets", offsetsBuffers[bufferIdx]);
            shader.SetInt("PlayerOffsetCount", offsetCount);
            shader.SetInts("PlayerPrevOrigin", atOrigin.x, atOrigin.y);
            shader.SetInt("PlayerRedistributeEnabled", 0);
            shader.SetFloat("PlayerRedistributeFactor", 0f);
            shader.SetInt("Width", caController.width);
            shader.SetInt("Height", caController.height);
            shader.SetInt("Decay", caController.Decay);
            
            Dispatch(kernelPlayerClear, bufferIdx);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"CA-REAPER: Error in ClearPlayer: {e.Message}");
        }
    }

    void BuildOffsetsBuffer()
    {
        var list = new List<Vector2Int>();
        int r2 = radius * radius;
        for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
                if (x * x + y * y <= r2)
                    list.Add(new Vector2Int(x, y));

        offsets = list.ToArray();
        offsetCount = offsets.Length;

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
            if (offsets[i] == Vector2Int.zero) vitalOffsetIndex = i;
        }

        // Create double-buffered compute buffers
        offsetsBuffers = new ComputeBuffer[2];
        aliveBuffers = new ComputeBuffer[2];
        
        for (int i = 0; i < 2; i++)
        {
            offsetsBuffers[i] = new ComputeBuffer(offsetCount, sizeof(int) * 2);
            offsetsBuffers[i].SetData(offsets);
            
            aliveBuffers[i] = new ComputeBuffer(offsetCount, sizeof(int));
            aliveBuffers[i].SetData(aliveInts);
        }
        
        currentBufferIndex = 0;
        nextBufferIndex = 1;
    }

    int GetCurrentBufferIndex()
    {
        return currentBufferIndex;
    }

    void ApplyBufferUpdates()
    {
        if (!buffersNeedUpdate) return;
        
        // Swap buffer indices
        int temp = currentBufferIndex;
        currentBufferIndex = nextBufferIndex;
        nextBufferIndex = temp;
        
        // Update the buffer that will be used next frame
        if (offsetsChanged)
        {
            offsetsBuffers[nextBufferIndex].SetData(offsets);
            offsetsChanged = false;
        }
        
        if (aliveChanged)
        {
            for (int i = 0; i < offsetCount; i++) aliveInts[i] = offsetAlive[i] ? 1 : 0;
            aliveBuffers[nextBufferIndex].SetData(aliveInts);
            aliveChanged = false;
        }
        
        buffersNeedUpdate = false;
    }

    void QueueBufferUpdate(bool offsetsNeedUpdate, bool aliveNeedUpdate)
    {
        offsetsChanged |= offsetsNeedUpdate;
        aliveChanged |= aliveNeedUpdate;
        buffersNeedUpdate = true;
    }

    void RedistributeAliveSegments()
    {
        int aliveCount = 0;
        for (int i = 0; i < offsetCount; i++) if (offsetAlive[i]) aliveCount++;
        if (aliveCount == 0) return;

        var newOffsets = new Vector2Int[offsetCount];
        var newAlive = new bool[offsetCount];
        for (int i = 0; i < offsetCount; i++)
        {
            newOffsets[i] = preferredOffsets[i];
            newAlive[i] = i < aliveCount;
        }

        vitalOffsetIndex = -1;
        for (int i = 0; i < offsetCount; i++)
            if (newOffsets[i] == Vector2Int.zero)
            {
                vitalOffsetIndex = i;
                break;
            }

        offsets = newOffsets;
        offsetAlive = newAlive;
        
        // Queue buffer updates instead of immediate SetData
        QueueBufferUpdate(true, true);
    }

    void RequestSnapshot()
    {
        caController.RequestColorData(data =>
        {
            if (data == null) return;
            if (solidSnapshot == null || solidSnapshot.Length != data.Length)
                solidSnapshot = new Color[data.Length];
            data.CopyTo(solidSnapshot, 0);
            snapshotReady = true;

            if (previousSnapshot == null)
            {
                previousSnapshot = new Color[data.Length];
                data.CopyTo(previousSnapshot, 0);
            }
        });
    }

    void Die(Vector2Int atOrigin)
    {
        if (isDead) return;
        isDead = true;
        for (int i = 0; i < offsetCount; i++) offsetAlive[i] = false;
        
        // Queue buffer update instead of immediate SetData
        QueueBufferUpdate(false, true);
        
        ClearPlayer(atOrigin);
        ClearPlayer(origin);
        Debug.Log("CA-REAPER: Vital cell eaten - player died.");
        OnPlayerDied?.Invoke();
    }

    bool IsSolidAt(int x, int y)
    {
        if (x < 0 || x >= caController.width || y < 0 || y >= caController.height) return true;
        
        // Use cached snapshot if available
        if (caController.LatestSnapshot != null)
            return caController.IsSolid(caController.LatestSnapshot[y * caController.width + x]);
        
        if (!snapshotReady) return false;
        return caController.IsSolid(solidSnapshot[y * caController.width + x]);
    }

    void OnDestroy()
    {
        if (offsetsBuffers != null)
        {
            foreach (var buffer in offsetsBuffers)
            {
                buffer?.Release();
            }
        }
        
        if (aliveBuffers != null)
        {
            foreach (var buffer in aliveBuffers)
            {
                buffer?.Release();
            }
        }
    }

    /// <summary>
    /// Removes player cells at the given world positions (e.g., eaten by enemy).
    /// Handles vital death and redistribution.
    /// </summary>
    public void RemoveCellsAt(IEnumerable<Vector2Int> worldPositions)
    {
        if (isDead) return;

        bool anyRemoved = false;
        bool vitalRemoved = false;

        foreach (Vector2Int pos in worldPositions)
        {
            for (int i = 0; i < offsetCount; i++)
            {
                if (!offsetAlive[i]) continue;
                Vector2Int cellPos = origin + offsets[i];
                if (cellPos == pos)
                {
                    offsetAlive[i] = false;
                    anyRemoved = true;
                    if (i == vitalOffsetIndex)
                        vitalRemoved = true;
                    break; // cell found, move to next position
                }
            }
        }

        if (!anyRemoved) return;

        // Queue buffer update for alive changes
        QueueBufferUpdate(false, true);

        if (vitalRemoved)
        {
            Die(origin);
            return;
        }

        // If vital not removed, redistribute remaining cells
        RedistributeAliveSegments();
    }
}