using System;
using System.Collections.Generic;
using UnityEngine;

public class EnemyManager : MonoBehaviour
{
    [Header("References")]
    public CAController caController;
    public PlayerController playerController;
    public GunController gunController;

    [Header("Enemy Settings")]
    public int enemyRadius = 4;          // same as player radius
    public Color enemyColor = new Color(1f, 0.5f, 0f);
    public Color enemyVitalColor = new Color(1f, 0f, 1f);
    public float detectionRange = 15f;   // in cells
    public float moveInterval = 0.5f;    // seconds per step
    public int maxEnemies = 5;

    [Header("Spawning")]
    public float spawnInterval = 5f;
    public float densityThreshold = 0.6f;    // 60% of cells must be alive
    public float snapshotRequestInterval = 1.5f; // refresh snapshot for spawning

    private List<EnemyUnit> enemies = new List<EnemyUnit>();
    private float spawnTimer = 0f;
    private float snapshotRequestTimer = 0f;

    // Snapshot and integral image for density queries
    private Color[] enemySnapshot;
    private int[] integralImage;
    private int integralWidth;   // width + 1
    private int integralHeight;  // height + 1
    private bool snapshotReadyForSpawn = false;

    void Start()
    {
        caController.OnCAStepped += HandleCAStepped;
        gunController.OnShotFired += HandleGunShot;

        // Initialize integral image dimensions
        integralWidth = caController.width + 1;
        integralHeight = caController.height + 1;
        integralImage = new int[integralWidth * integralHeight];
    }

    void OnDestroy()
    {
        caController.OnCAStepped -= HandleCAStepped;
        gunController.OnShotFired -= HandleGunShot;
        foreach (var enemy in enemies)
            enemy.ReleaseBuffers();
    }

    void Update()
    {
        // Periodically request a fresh snapshot for spawning (no duplicate requests thanks to central manager)
        snapshotRequestTimer += Time.deltaTime;
        if (snapshotRequestTimer >= snapshotRequestInterval)
        {
            snapshotRequestTimer = 0f;
            RequestSnapshotForSpawning();
        }

        // Spawn timer
        spawnTimer += Time.deltaTime;
        if (spawnTimer >= spawnInterval && enemies.Count < maxEnemies && snapshotReadyForSpawn)
        {
            spawnTimer = 0f;
            TrySpawnEnemy();
        }

        // Move enemies
        foreach (var enemy in enemies)
        {
            if (enemy.IsDead) continue;
            float distance = Vector2Int.Distance(enemy.Origin, playerController.Origin);
            if (distance <= detectionRange)
                enemy.MoveTowards(playerController.Origin, Time.deltaTime);
        }

        // Collect all positions where enemies overlap player cells
        var eatenPositions = new HashSet<Vector2Int>();
        foreach (var enemy in enemies)
        {
            if (enemy.IsDead) continue;
            var overlaps = enemy.GetOverlappingPlayerPositions(playerController.Origin, playerController.Offsets, playerController.OffsetAlive);
            foreach (var pos in overlaps)
                eatenPositions.Add(pos);
        }

        if (eatenPositions.Count > 0)
        {
            playerController.RemoveCellsAt(eatenPositions);
        }
    }

    void RequestSnapshotForSpawning()
    {
        caController.RequestColorData(data =>
        {
            if (data != null)
            {
                enemySnapshot = data;
                BuildIntegralImage();
                snapshotReadyForSpawn = true;
            }
        });
    }

    void BuildIntegralImage()
    {
        int width = caController.width;
        int height = caController.height;

        // Clear integral image
        System.Array.Clear(integralImage, 0, integralImage.Length);

        // Fill integral image (standard prefix sum)
        for (int y = 0; y < height; y++)
        {
            int rowSum = 0;
            int rowOffset = y * width;
            int integralRow = (y + 1) * integralWidth;
            for (int x = 0; x < width; x++)
            {
                bool alive = IsAliveMainCA(enemySnapshot[rowOffset + x]);
                rowSum += alive ? 1 : 0;
                int idx = integralRow + (x + 1);
                integralImage[idx] = integralImage[(y * integralWidth) + (x + 1)] + rowSum;
            }
        }
    }

    bool IsAliveMainCA(Color cell)
    {
        // Fully alive main CA: alpha between 0.5 and 1.5 (not decaying, not dead)
        return cell.a > 0.5f && cell.a < 1.5f;
    }

    // Returns the alive count in a square region using the integral image
    int GetAliveCountInSquare(int centerX, int centerY, int radius)
    {
        int width = caController.width;
        int height = caController.height;

        int x1 = Mathf.Clamp(centerX - radius, 0, width - 1);
        int x2 = Mathf.Clamp(centerX + radius, 0, width - 1);
        int y1 = Mathf.Clamp(centerY - radius, 0, height - 1);
        int y2 = Mathf.Clamp(centerY + radius, 0, height - 1);

        // Integral image coordinates are +1
        int ix1 = x1;
        int ix2 = x2 + 1;
        int iy1 = y1;
        int iy2 = y2 + 1;

        int A = integralImage[iy1 * integralWidth + ix1];
        int B = integralImage[iy1 * integralWidth + ix2];
        int C = integralImage[iy2 * integralWidth + ix1];
        int D = integralImage[iy2 * integralWidth + ix2];

        return D - B - C + A;
    }

    void TrySpawnEnemy()
    {
        Vector2Int? spawnPos = FindDenseSpawnLocation();
        if (spawnPos.HasValue)
        {
            var enemy = new EnemyUnit();
            enemy.Initialize(
                caController.cellularAutomaton,
                enemyRadius,
                spawnPos.Value,
                enemyColor,
                enemyVitalColor,
                caController.width,
                caController.height,
                moveInterval
            );
            enemies.Add(enemy);
        }
    }

    Vector2Int? FindDenseSpawnLocation()
    {
        if (!snapshotReadyForSpawn || enemySnapshot == null)
            return null;

        int width = caController.width;
        int height = caController.height;
        int radius = enemyRadius;

        // We'll scan every cell but only as centers (could be optimized with stride > 1)
        var candidates = new List<Vector2Int>();
        for (int y = radius; y < height - radius; y++)
        {
            for (int x = radius; x < width - radius; x++)
            {
                int squareSide = 2 * radius + 1;
                int totalCells = squareSide * squareSide;
                int aliveCount = GetAliveCountInSquare(x, y, radius);
                float density = (float)aliveCount / totalCells;

                if (density >= densityThreshold)
                {
                    candidates.Add(new Vector2Int(x, y));
                }
            }
        }

        if (candidates.Count == 0)
            return null;

        // Pick a random candidate
        int index = UnityEngine.Random.Range(0, candidates.Count);
        return candidates[index];
    }

    void HandleCAStepped()
    {
        // Clear old positions, then draw new
        foreach (var enemy in enemies)
        {
            enemy.Clear(caController.cellularAutomaton, caController.CurrentTexture);
        }
        foreach (var enemy in enemies)
        {
            enemy.Draw(caController.cellularAutomaton, caController.CurrentTexture);
        }
    }

    void HandleGunShot(Vector2Int impactCenter, int impactRadius)
    {
        foreach (var enemy in enemies)
        {
            Color gunColor = new Color(gunController.automatonID.x, gunController.automatonID.y, gunController.automatonID.z, 1f);
            Color mainColor = new Color(caController.automatonID.x, caController.automatonID.y, caController.automatonID.z, 1f);

            enemy.HandleShot(
                caController.cellularAutomaton,
                caController.CurrentTexture,
                impactCenter,
                impactRadius,
                gunColor,
                mainColor
            );

            if (enemy.IsDead)
                enemy.ReleaseBuffers();
        }
        enemies.RemoveAll(e => e.IsDead);
    }

    public bool IsCellOccupiedByEnemy(Vector2Int pos)
    {
        foreach (var enemy in enemies)
        {
            if (enemy.IsDead) continue;
            if (enemy.IsCellOccupied(pos))
                return true;
        }
        return false;
    }
}