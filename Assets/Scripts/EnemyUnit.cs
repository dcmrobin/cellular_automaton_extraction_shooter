using System;
using System.Collections.Generic;
using UnityEngine;

public class EnemyUnit
{
    public Vector2Int Origin { get; private set; }
    public bool IsDead { get; private set; }
    public int Radius { get; private set; }

    public Color EnemyColor { get; private set; }
    public Color VitalColor { get; private set; }

    public Vector2Int[] Offsets { get; private set; }
    public bool[] OffsetAlive { get; private set; }
    public int VitalOffsetIndex { get; private set; }
    public int OffsetCount => Offsets.Length;

    private ComputeBuffer offsetsBuffer;
    private ComputeBuffer aliveBuffer;
    private int[] aliveInts;

    private Vector2Int prevOrigin;
    private float moveTimer;
    private float moveInterval;

    private int kernelEnemyDraw;
    private int kernelEnemyClear;
    private int kernelEnemyImpact;
    private int mapWidth;
    private int mapHeight;

    // Called by EnemyManager
    public void Initialize(ComputeShader shader, int radius, Vector2Int startOrigin, Color color, Color vitalColor, int width, int height)
    {
        Radius = radius;
        Origin = startOrigin;
        prevOrigin = startOrigin;
        EnemyColor = color;
        VitalColor = vitalColor;
        IsDead = false;
        mapHeight = height;
        mapWidth = width;

        BuildOffsets();
        CreateBuffers();

        kernelEnemyDraw = shader.FindKernel("EnemyDraw");
        kernelEnemyClear = shader.FindKernel("EnemyClear");
        kernelEnemyImpact = shader.FindKernel("EnemyImpact");
    }

    private void BuildOffsets()
    {
        var list = new List<Vector2Int>();
        int r2 = Radius * Radius;
        for (int y = -Radius; y <= Radius; y++)
            for (int x = -Radius; x <= Radius; x++)
                if (x * x + y * y <= r2)
                    list.Add(new Vector2Int(x, y));

        Offsets = list.ToArray();
        OffsetAlive = new bool[Offsets.Length];
        for (int i = 0; i < Offsets.Length; i++)
        {
            OffsetAlive[i] = true;
            if (Offsets[i] == Vector2Int.zero)
                VitalOffsetIndex = i;
        }
    }

    private void CreateBuffers()
    {
        aliveInts = new int[OffsetCount];
        for (int i = 0; i < OffsetCount; i++) aliveInts[i] = 1;

        offsetsBuffer = new ComputeBuffer(OffsetCount, sizeof(int) * 2);
        offsetsBuffer.SetData(Offsets);

        aliveBuffer = new ComputeBuffer(OffsetCount, sizeof(int));
        aliveBuffer.SetData(aliveInts);
    }

    public void Draw(ComputeShader shader, RenderTexture world)
    {
        if (IsDead) return;
        shader.SetTexture(kernelEnemyDraw, "World", world);
        shader.SetBuffer(kernelEnemyDraw, "EnemyOffsets", offsetsBuffer);
        shader.SetBuffer(kernelEnemyDraw, "EnemyAlive", aliveBuffer);
        shader.SetInt("EnemyOffsetCount", OffsetCount);
        shader.SetInts("EnemyOrigin", Origin.x, Origin.y);
        shader.SetVector("EnemyColor", EnemyColor);
        shader.SetVector("EnemyVitalColor", VitalColor);
        shader.SetInt("EnemyVitalOffsetIndex", VitalOffsetIndex);
        Dispatch(shader, kernelEnemyDraw);
    }

    public void Clear(ComputeShader shader, RenderTexture world)
    {
        if (IsDead) return;
        shader.SetTexture(kernelEnemyClear, "World", world);
        shader.SetBuffer(kernelEnemyClear, "EnemyOffsets", offsetsBuffer);
        shader.SetInt("EnemyOffsetCount", OffsetCount);
        shader.SetInts("EnemyPrevOrigin", prevOrigin.x, prevOrigin.y);
        Dispatch(shader, kernelEnemyClear);
    }

    public void HandleShot(ComputeShader shader, RenderTexture world, Vector2Int impactCenter, int impactRadius, Color gunColor, Color mainColor)
    {
        if (IsDead) return;

        // Determine if vital is within radius
        bool vitalHit = false;
        for (int i = 0; i < OffsetCount; i++)
        {
            if (!OffsetAlive[i]) continue;
            Vector2Int pos = Origin + Offsets[i];
            if (Vector2Int.Distance(pos, impactCenter) <= impactRadius)
            {
                if (i == VitalOffsetIndex)
                {
                    vitalHit = true;
                    break;
                }
            }
        }

        // Dispatch impact kernel
        shader.SetTexture(kernelEnemyImpact, "World", world);
        shader.SetBuffer(kernelEnemyImpact, "EnemyOffsets", offsetsBuffer);
        shader.SetBuffer(kernelEnemyImpact, "EnemyAlive", aliveBuffer);
        shader.SetInt("EnemyOffsetCount", OffsetCount);
        shader.SetInts("EnemyOrigin", Origin.x, Origin.y);
        shader.SetInts("GunImpactCenter", impactCenter.x, impactCenter.y);
        shader.SetInt("GunImpactRadius", impactRadius);
        shader.SetInt("EnemyVitalHit", vitalHit ? 1 : 0);
        shader.SetVector("GunAutomatonID", gunColor);
        shader.SetVector("MainAutomatonID", mainColor);
        Dispatch(shader, kernelEnemyImpact);

        // Update alive status on CPU
        for (int i = 0; i < OffsetCount; i++)
        {
            if (!OffsetAlive[i]) continue;
            Vector2Int pos = Origin + Offsets[i];
            float dist = Vector2Int.Distance(pos, impactCenter);
            if (dist <= impactRadius)
            {
                OffsetAlive[i] = false; // turned to gun CA
            }
            else if (vitalHit)
            {
                OffsetAlive[i] = false; // turned to main CA
            }
        }

        // Upload new alive buffer
        for (int i = 0; i < OffsetCount; i++) aliveInts[i] = OffsetAlive[i] ? 1 : 0;
        aliveBuffer.SetData(aliveInts);

        // If all cells dead, mark enemy as dead
        bool anyAlive = false;
        for (int i = 0; i < OffsetCount; i++) if (OffsetAlive[i]) { anyAlive = true; break; }
        if (!anyAlive) IsDead = true;
    }

    public void MoveTowards(Vector2Int target, float deltaTime)
    {
        if (IsDead) return;

        moveTimer += deltaTime;
        if (moveTimer < moveInterval) return;
        moveTimer = 0f;

        Vector2Int dir = Vector2Int.zero;
        Vector2Int diff = target - Origin;
        if (Mathf.Abs(diff.x) > Mathf.Abs(diff.y))
            dir.x = diff.x > 0 ? 1 : -1;
        else
            dir.y = diff.y > 0 ? 1 : -1;

        // Optional: check bounds (assuming no solid collision)
        Vector2Int newOrigin = Origin + dir;
        // Simple bounds check for all cells? We'll just check origin within a margin.
        if (newOrigin.x < 0 || newOrigin.x >= mapWidth || newOrigin.y < 0 || newOrigin.y >= mapHeight) return; // hardcoded? Better pass world size.
        // For simplicity, assume bounds are handled by EnemyManager.

        prevOrigin = Origin;
        Origin = newOrigin;
    }

    public bool TryEatPlayer(PlayerController player)
    {
        if (IsDead || player.IsDead) return false;
        var overlaps = GetOverlappingPlayerPositions(player.Origin, player.Offsets, player.OffsetAlive);
        if (overlaps.Count > 0)
        {
            player.RemoveCellsAt(overlaps);
            return true;
        }
        return false;
    }

    public void ReleaseBuffers()
    {
        offsetsBuffer?.Release();
        aliveBuffer?.Release();
    }

    private void Dispatch(ComputeShader shader, int kernel)
    {
        int groups = Mathf.Max(1, Mathf.CeilToInt(OffsetCount / 64f));
        shader.Dispatch(kernel, groups, 1, 1);
    }

    public List<Vector2Int> GetOverlappingPlayerPositions(Vector2Int playerOrigin, Vector2Int[] playerOffsets, bool[] playerAlive)
    {
        var result = new List<Vector2Int>();
        if (IsDead) return result;

        for (int i = 0; i < OffsetCount; i++)
        {
            if (!OffsetAlive[i]) continue;
            Vector2Int enemyPos = Origin + Offsets[i];

            for (int j = 0; j < playerOffsets.Length; j++)
            {
                if (!playerAlive[j]) continue;
                if (playerOrigin + playerOffsets[j] == enemyPos)
                {
                    result.Add(enemyPos);
                    break;
                }
            }
        }
        return result;
    }

    public bool IsCellOccupied(Vector2Int pos)
    {
        if (IsDead) return false;
        for (int i = 0; i < OffsetCount; i++)
        {
            if (!OffsetAlive[i]) continue;
            if (Origin + Offsets[i] == pos)
                return true;
        }
        return false;
    }
}