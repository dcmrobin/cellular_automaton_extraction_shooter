using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using System.Linq;

public class GunPickupManager : MonoBehaviour
{
    [Header("References")]
    public CAController caController;
    public GunController gunController;
    public PlayerController playerController;
    public EnemyManager enemyManager;
    public GameObject gunPickupPrefab;

    [Header("Detection")]
    public Texture2D templateTexture;
    public float scanInterval = 3f;
    [Range(0.5f, 1f)]
    public float matchThreshold = 0.7f;
    public int scanStride = 2;

    [Header("Stats Derivation")]
    public AnimationCurve fireRateCurve = AnimationCurve.Linear(0, 1, 1, 30);
    public AnimationCurve spreadCurve = AnimationCurve.Linear(0, 0, 1, 50);
    public AnimationCurve AOECurve = AnimationCurve.Linear(0, 1, 1, 20);

    // GPU resources
    private int kernelScanForGuns = -1;
    private int kernelClearGunCells = -1;
    
    private int templateWidth, templateHeight;
    private float scanTimer = 0f;
    private List<GunPickup> activePickups = new List<GunPickup>();

    // GPU data structures
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MatchResult
    {
        public Vector2Int center;
        public int cellCount;
        public int minX;
        public int maxX;
        public int minY;
        public int maxY;
    }

    public enum GunPart { Body, Barrel, Grip, Handle, Sight }

    [Header("Organic Growth")]
    public int maxBarrelExtension = 15;
    public int maxBarrelGirth = 2;
    public int maxGripExtension = 4;
    public int maxGripGirth = 2;

    // Template-local part classification, computed once.
    private Dictionary<Vector2Int, GunPart> templatePartMap;
    private HashSet<Vector2Int> templateSolidLocal;

    private ComputeBuffer matchResultsBuffer;
    private ComputeBuffer matchCountBuffer;
    private int[] matchCountData = new int[1];
    private MatchResult[] matchResultsData;
    private const int MAX_MATCHES = 256;

    void Start()
    {
        if (caController == null) caController = FindObjectOfType<CAController>();
        if (gunController == null) gunController = FindObjectOfType<GunController>();
        if (playerController == null) playerController = FindObjectOfType<PlayerController>();
        if (enemyManager == null) enemyManager = FindObjectOfType<EnemyManager>();

        if (templateTexture == null)
        {
            Debug.LogError("GunPickupManager: No template texture assigned!");
            enabled = false;
            return;
        }

        // Get template dimensions
        templateWidth = templateTexture.width;
        templateHeight = templateTexture.height;

        BuildTemplatePartMap();

        // Find kernels using the safe method
        if (caController != null)
        {
            kernelScanForGuns = caController.GetKernelIndex("ScanForGuns");
            kernelClearGunCells = caController.GetKernelIndex("ClearGunCells");
            
            Debug.Log($"GunPickupManager: ScanForGuns kernel index: {kernelScanForGuns}");
            Debug.Log($"GunPickupManager: ClearGunCells kernel index: {kernelClearGunCells}");
        }

        // Initialize GPU buffers
        InitializeGPUBuffers();

        // Upload template to GPU
        UploadTemplateToGPU();
    }

    void OnDestroy()
    {
        // Release GPU buffers
        matchResultsBuffer?.Release();
        matchCountBuffer?.Release();
    }

    void InitializeGPUBuffers()
    {
        matchResultsBuffer = new ComputeBuffer(MAX_MATCHES, System.Runtime.InteropServices.Marshal.SizeOf(typeof(MatchResult)));
        matchResultsData = new MatchResult[MAX_MATCHES];
        
        matchCountBuffer = new ComputeBuffer(1, sizeof(int));
        matchCountData[0] = 0;
        matchCountBuffer.SetData(matchCountData);
    }

    void UploadTemplateToGPU()
    {
        ComputeShader shader = caController.cellularAutomaton;
        if (shader == null || kernelScanForGuns < 0) return;

        // Upload template as a texture (already available)
        shader.SetTexture(kernelScanForGuns, "TemplateTexture", templateTexture);
        
        // Set template dimensions
        shader.SetInt("TemplateWidth", templateWidth);
        shader.SetInt("TemplateHeight", templateHeight);
        shader.SetInt("ScanStride", scanStride);
        shader.SetFloat("MatchThreshold", matchThreshold);
        
        // Set output buffers
        shader.SetBuffer(kernelScanForGuns, "MatchResults", matchResultsBuffer);
        shader.SetBuffer(kernelScanForGuns, "MatchCount", matchCountBuffer);
        
        // Also set for clear kernel
        if (kernelClearGunCells >= 0)
        {
            shader.SetBuffer(kernelClearGunCells, "MatchResults", matchResultsBuffer);
            shader.SetBuffer(kernelClearGunCells, "MatchCount", matchCountBuffer);
        }
    }

    void Update()
    {
        scanTimer += Time.deltaTime;
        if (scanTimer >= scanInterval)
        {
            scanTimer = 0f;
            ScanForGunsGPU();
        }
    }

    List<MatchResult> DeduplicateMatches(MatchResult[] matches, int count)
    {
        var sorted = new List<MatchResult>();
        for (int i = 0; i < count && i < MAX_MATCHES; i++)
            sorted.Add(matches[i]);
        // Prefer the most complete match when several overlap
        sorted.Sort((a, b) => b.cellCount.CompareTo(a.cellCount));

        var accepted = new List<MatchResult>();
        foreach (var candidate in sorted)
        {
            bool isDuplicate = false;
            foreach (var kept in accepted)
            {
                if (BoundingBoxesOverlapSignificantly(candidate, kept))
                {
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
                accepted.Add(candidate);
        }
        return accepted;
    }

    bool BoundingBoxesOverlapSignificantly(MatchResult a, MatchResult b)
    {
        int overlapMinX = Mathf.Max(a.minX, b.minX);
        int overlapMaxX = Mathf.Min(a.maxX, b.maxX);
        int overlapMinY = Mathf.Max(a.minY, b.minY);
        int overlapMaxY = Mathf.Min(a.maxY, b.maxY);

        if (overlapMaxX <= overlapMinX || overlapMaxY <= overlapMinY)
            return false;

        int overlapArea = (overlapMaxX - overlapMinX) * (overlapMaxY - overlapMinY);
        int areaA = Mathf.Max(1, (a.maxX - a.minX) * (a.maxY - a.minY));
        int areaB = Mathf.Max(1, (b.maxX - b.minX) * (b.maxY - b.minY));
        int smallerArea = Mathf.Min(areaA, areaB);

        return overlapArea >= smallerArea * 0.4f;
    }

    void ScanForGunsGPU()
    {
        if (caController == null || caController.cellularAutomaton == null)
            return;
        
        if (kernelScanForGuns < 0)
        {
            Debug.LogWarning("GunPickupManager: ScanForGuns kernel not available!");
            return;
        }

        ComputeShader shader = caController.cellularAutomaton;
        
        try
        {
            // Reset match count
            matchCountData[0] = 0;
            matchCountBuffer.SetData(matchCountData);
            
            // Set all required textures and parameters
            shader.SetTexture(kernelScanForGuns, "World", caController.CurrentTexture);
            shader.SetTexture(kernelScanForGuns, "TemplateTexture", templateTexture);
            
            shader.SetInt("Width", caController.width);
            shader.SetInt("Height", caController.height);
            shader.SetInt("Decay", caController.Decay);
            
            shader.SetInt("TemplateWidth", templateWidth);
            shader.SetInt("TemplateHeight", templateHeight);
            shader.SetInt("ScanStride", scanStride);
            shader.SetFloat("MatchThreshold", matchThreshold);
            
            shader.SetBuffer(kernelScanForGuns, "MatchResults", matchResultsBuffer);
            shader.SetBuffer(kernelScanForGuns, "MatchCount", matchCountBuffer);
            
            int groupsX = Mathf.CeilToInt((caController.width - templateWidth) / (float)(scanStride * 8));
            int groupsY = Mathf.CeilToInt((caController.height - templateHeight) / (float)(scanStride * 8));
            groupsX = Mathf.Max(1, groupsX);
            groupsY = Mathf.Max(1, groupsY);
            
            shader.Dispatch(kernelScanForGuns, groupsX, groupsY, 1);
            
            matchCountBuffer.GetData(matchCountData);
            int matchCount = matchCountData[0];
            
            if (matchCount == 0) return;
            
            matchResultsBuffer.GetData(matchResultsData, 0, 0, Math.Min(matchCount, MAX_MATCHES));

            var dedupedMatches = DeduplicateMatches(matchResultsData, matchCount);

            // Overlap-check only here - do NOT clear anything yet. Clearing has
            // to wait until after we've generated the gun shape from a snapshot
            // that still shows these cells intact.
            var acceptedMatches = new List<MatchResult>();

            foreach (MatchResult match in dedupedMatches)
            {
                if (match.cellCount < 5) continue;

                bool overlapsPlayer = false;
                if (playerController != null && !playerController.IsDead)
                {
                    for (int y = match.minY; y <= match.maxY && !overlapsPlayer; y++)
                    {
                        for (int x = match.minX; x <= match.maxX && !overlapsPlayer; x++)
                        {
                            Vector2Int pos = new Vector2Int(x, y);
                            if (playerController.IsCellOccupied(pos))
                                overlapsPlayer = true;
                        }
                    }
                }
                if (overlapsPlayer) continue;

                bool overlapsEnemy = false;
                if (enemyManager != null)
                {
                    for (int y = match.minY; y <= match.maxY && !overlapsEnemy; y++)
                    {
                        for (int x = match.minX; x <= match.maxX && !overlapsEnemy; x++)
                        {
                            Vector2Int pos = new Vector2Int(x, y);
                            if (enemyManager.IsCellOccupiedByEnemy(pos))
                                overlapsEnemy = true;
                        }
                    }
                }
                if (overlapsEnemy) continue;

                acceptedMatches.Add(match);
            }

            if (acceptedMatches.Count == 0) return;

            // Take ONE fresh snapshot now, while all accepted matches' cells are
            // still intact in World. Generate every gun from this snapshot, and
            // only clear a match's cells from the CA after its pickup has
            // actually been created - never before.
            caController.RequestColorData(freshSnapshot =>
            {
                if (freshSnapshot == null) return;

                foreach (MatchResult match in acceptedMatches)
                {
                    bool created = CreatePickupFromMatch(match, freshSnapshot);
                    if (created)
                    {
                        ClearCellsFromCA(match);
                    }
                }
            });
        }
        catch (System.Exception e)
        {
            Debug.LogError($"GunPickupManager: Error in ScanForGunsGPU: {e.Message}");
        }
    }

    void ClearCellsFromCA(MatchResult match)
    {
        if (kernelClearGunCells >= 0 && caController.cellularAutomaton != null)
        {
            try
            {
                ComputeShader shader = caController.cellularAutomaton;
                
                // Reset match count
                matchCountData[0] = 1; // Only clear this one match
                matchCountBuffer.SetData(matchCountData);
                
                // Set match results
                MatchResult[] singleMatch = new MatchResult[] { match };
                matchResultsBuffer.SetData(singleMatch);
                
                // Set parameters
                shader.SetTexture(kernelClearGunCells, "World", caController.CurrentTexture);
                shader.SetBuffer(kernelClearGunCells, "MatchResults", matchResultsBuffer);
                shader.SetBuffer(kernelClearGunCells, "MatchCount", matchCountBuffer);
                shader.SetInt("Width", caController.width);
                shader.SetInt("Height", caController.height);
                shader.SetInt("Decay", caController.Decay);
                
                // Dispatch
                int groups = Mathf.CeilToInt(1 / 64f);
                shader.Dispatch(kernelClearGunCells, Mathf.Max(1, groups), 1, 1);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error clearing cells with GPU: {e.Message}");
            }
        }
        else
        {
            // Fallback to CPU method
            ClearCellsCPU(match);
        }
    }

    void ClearCellsCPU(MatchResult match)
    {
        // Use the latest snapshot to clear cells
        caController.RequestColorData(data =>
        {
            if (data == null) return;
            
            int width = caController.width;
            int height = caController.height;
            
            // Clear cells in the bounding box
            int startY = Math.Max(0, match.minY);
            int endY = Math.Min(height - 1, match.maxY);
            int startX = Math.Max(0, match.minX);
            int endX = Math.Min(width - 1, match.maxX);
            
            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = y * width + x;
                    Color cell = data[index];
                    
                    // Check if it's part of the gun
                    if (caController.IsSolid(cell) || caController.IsGunCA(cell))
                    {
                        // Check if it's connected to the center (approximate)
                        Vector2Int pos = new Vector2Int(x, y);
                        float dist = Vector2Int.Distance(pos, new Vector2Int(match.center.x, match.center.y));
                        if (dist < Math.Max(templateWidth, templateHeight) * 2f)
                        {
                            // Clear it
                            float deadState = (caController.Decay > 1) ? (float)caController.Decay : 0f;
                            data[index] = new Color(0, 0, 0, deadState);
                        }
                    }
                }
            }
        });
    }

    bool CreatePickupFromMatch(MatchResult match, Color[] snapshot)
    {
        HashSet<Vector2Int> matchedCells = ExtractCellsFromMatch(match, snapshot);
        if (matchedCells.Count < 5) return false;
        if (!VerifyShapeMatchesTemplate(matchedCells, new Vector2Int(match.center.x, match.center.y)))
            return false;

        HashSet<Vector2Int> gunCells = GenerateGunShape(match, snapshot);
        if (gunCells.Count < 5) return false;

        GunData data = DeriveGunStats(gunCells);
        Sprite sprite = CreateSpriteFromCells(gunCells, data, snapshot);

        GameObject pickupObj = Instantiate(gunPickupPrefab);
        GunPickup pickup = pickupObj.GetComponent<GunPickup>();
        if (pickup != null)
        {
            pickup.Initialize(data, sprite, new Vector2Int(match.center.x, match.center.y), caController, gunCells);
            activePickups.Add(pickup);
            return true;
        }

        return false;
    }

    HashSet<Vector2Int> ExtractCellsFromMatch(MatchResult match, Color[] snapshot)
    {
        HashSet<Vector2Int> cells = new HashSet<Vector2Int>();
        
        if (snapshot == null) return cells;
        
        int width = caController.width;
        
        // BFS to extract connected component
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        Vector2Int start = new Vector2Int(match.center.x, match.center.y);
        queue.Enqueue(start);
        
        while (queue.Count > 0)
        {
            Vector2Int pos = queue.Dequeue();
            if (cells.Contains(pos)) continue;
            
            if (pos.x < 0 || pos.x >= caController.width || 
                pos.y < 0 || pos.y >= caController.height) continue;
            
            int index = pos.y * width + pos.x;
            if (!caController.IsSolid(snapshot[index]) && !caController.IsGunCA(snapshot[index])) continue;
            
            cells.Add(pos);
            
            Vector2Int[] neighbors = new Vector2Int[]
            {
                pos + Vector2Int.up,
                pos + Vector2Int.down,
                pos + Vector2Int.left,
                pos + Vector2Int.right
            };
            
            foreach (var neighbor in neighbors)
            {
                if (!cells.Contains(neighbor))
                    queue.Enqueue(neighbor);
            }
        }
        
        return cells;
    }

    GunData DeriveGunStats(HashSet<Vector2Int> cells)
    {
        int cellCount = cells.Count;
        
        // Calculate bounding box
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var p in cells)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }
        
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        float aspect = (float)width / height;
        bool isWide = width > height + 3;
        float density = cellCount / (float)(width * height);
        
        // Derive stats
        int fireRate = Mathf.RoundToInt(fireRateCurve.Evaluate(Mathf.Clamp01(density)));
        int spread = Mathf.RoundToInt(spreadCurve.Evaluate(Mathf.Clamp01(isWide ? (aspect/10) + 0.25f : (aspect/10) - 0.25f)));
        int AOE = Mathf.RoundToInt(AOECurve.Evaluate(Mathf.Clamp01(cellCount / 100f)));
        int decay = Mathf.Max(1, Mathf.RoundToInt(cellCount / 100f));
        
        // Generate rules
        string birthStr = GenerateRuleFromShape(cells, true);
        string survivalStr = GenerateRuleFromShape(cells, false);
        string rules = $"{birthStr}/{survivalStr}/{decay}";
        
        // Gun color (variation of main CA color)
        Vector3 mainColor = caController.automatonID;
        Vector3 gunColor = new Vector3(
            Mathf.Clamp01(mainColor.x + UnityEngine.Random.Range(-0.8f, 0.8f)),
            Mathf.Clamp01(mainColor.y + UnityEngine.Random.Range(-0.8f, 0.8f)),
            Mathf.Clamp01(mainColor.z + UnityEngine.Random.Range(-0.8f, 0.8f))
        );
        
        return new GunData(rules, gunColor, fireRate, spread, AOE, decay);
    }

    void GrowPart(
        HashSet<Vector2Int> gunCells,
        Dictionary<Vector2Int, GunPart> partOf,
        GunPart part,
        Vector2Int growDir,
        Vector2Int[] thickenDirs,
        int maxExtension,
        int maxGirth,
        Color[] snapshot)
    {
        // ---- Length: unchanged. Per-column tapering here is intentional -
        // it's what lets the barrel trail off naturally where CA runs out,
        // exactly as originally requested.
        List<Vector2Int> active = new List<Vector2Int>();
        foreach (var kvp in partOf)
        {
            if (kvp.Value != part) continue;
            Vector2Int forward = kvp.Key + growDir;
            if (!partOf.ContainsKey(forward) || partOf[forward] != part)
                active.Add(kvp.Key);
        }
        if (active.Count == 0) return;

        for (int step = 0; step < maxExtension && active.Count > 0; step++)
        {
            var next = new List<Vector2Int>();
            foreach (var cell in active)
            {
                Vector2Int candidate = cell + growDir;
                if (!InBounds(candidate) || gunCells.Contains(candidate)) continue;
                if (!IsMainCAAlive(candidate, snapshot)) continue;

                gunCells.Add(candidate);
                partOf[candidate] = part;
                next.Add(candidate);
            }
            active = next;
        }

        // ---- Girth: uniform across the WHOLE part, not per-cell. -----
        // Old approach let each column's tip decide independently whether it
        // had CA support to thicken, which produced inconsistent width along
        // the barrel (some columns 3-thick, neighbours 1-thick). Instead, each
        // depth ring is all-or-nothing: only add ring d to every column if
        // EVERY cell in that ring is CA-supported. The instant one cell fails,
        // stop growing that direction for the whole part - this keeps the
        // girth constant along the entire length instead of patchy.
        foreach (var thickenDir in thickenDirs)
        {
            // Snapshot of the part's footprint before this direction's
            // thickening (includes the length growth above).
            List<Vector2Int> baseCells = new List<Vector2Int>();
            foreach (var kvp in partOf)
                if (kvp.Value == part) baseCells.Add(kvp.Key);

            for (int depth = 1; depth <= maxGirth; depth++)
            {
                var ring = new List<Vector2Int>();
                bool ringFullySupported = true;

                foreach (var cell in baseCells)
                {
                    Vector2Int candidate = cell + thickenDir * depth;

                    // Already claimed by the gun (e.g. overlapping another
                    // part's growth) - not a support failure, just skip it.
                    if (gunCells.Contains(candidate)) continue;

                    if (!InBounds(candidate) || !IsMainCAAlive(candidate, snapshot))
                    {
                        ringFullySupported = false;
                        break;
                    }
                    ring.Add(candidate);
                }

                if (!ringFullySupported) break;

                foreach (var candidate in ring)
                {
                    gunCells.Add(candidate);
                    partOf[candidate] = part;
                }
            }
        }
    }

    bool InBounds(Vector2Int pos) =>
        pos.x >= 0 && pos.x < caController.width && pos.y >= 0 && pos.y < caController.height;

    bool IsMainCAAlive(Vector2Int pos, Color[] snapshot)
    {
        int index = pos.y * caController.width + pos.x;
        if (index < 0 || index >= snapshot.Length) return false;
        float a = snapshot[index].a;
        // Strictly "alive" - excludes dead cells, decaying cells, AND gun CA
        // (a >= 20). Mirrors the compute shader's own `isAlive` check in Step
        // (state > 0.5 && state < 1.5), per your spec: alive only, not decaying.
        return a > 0.5f && a < 1.5f;
    }

    HashSet<Vector2Int> GenerateGunShape(MatchResult match, Color[] snapshot)
    {
        var gunCells = new HashSet<Vector2Int>();
        var partOf = new Dictionary<Vector2Int, GunPart>();

        int startX = match.minX;
        int startY = match.minY;

        // 1. Stamp the base template silhouette into world space, unmodified.
        // This guarantees the gun always reads as a gun before any growth happens.
        foreach (var local in templateSolidLocal)
        {
            Vector2Int world = new Vector2Int(startX + local.x, startY + local.y);
            gunCells.Add(world);
            partOf[world] = templatePartMap[local];
        }

        if (snapshot == null) return gunCells;

        // 2. Extend the barrel to the right and thicken it vertically -
        //    but only where real, currently-alive main CA cells exist to grow into.
        GrowPart(gunCells, partOf, GunPart.Barrel,
                growDir: new Vector2Int(1, 0),
                thickenDirs: new[] { Vector2Int.up, Vector2Int.down },
                maxExtension: maxBarrelExtension,
                maxGirth: maxBarrelGirth,
                snapshot: snapshot);

        // 3. Same treatment for the grip, extending/thickening downward.
        GrowPart(gunCells, partOf, GunPart.Grip,
                growDir: new Vector2Int(0, -1),
                thickenDirs: new[] { Vector2Int.left, Vector2Int.right },
                maxExtension: maxGripExtension,
                maxGirth: maxGripGirth,
                snapshot: snapshot);

        // Stash the part map for CreateSpriteFromCells to use for coloring.
        lastGunPartMap = partOf;
        return gunCells;
    }

    // Populated by GenerateGunShape immediately before CreateSpriteFromCells is called.
    private Dictionary<Vector2Int, GunPart> lastGunPartMap;

    void BuildTemplatePartMap()
    {
        if (!templateTexture.isReadable)
        {
            Debug.LogError("GunPickupManager: templateTexture must have Read/Write Enabled in its import settings for part classification.");
            return;
        }

        templatePartMap = new Dictionary<Vector2Int, GunPart>();
        templateSolidLocal = new HashSet<Vector2Int>();

        int centerX = templateWidth / 2;
        int centerY = templateHeight / 2;

        for (int y = 0; y < templateHeight; y++)
        {
            for (int x = 0; x < templateWidth; x++)
            {
                Color px = templateTexture.GetPixel(x, y);

                // The gun shape is white-on-transparent: RGB can be white
                // everywhere (including "empty" regions, depending on how the
                // PNG was authored), so alpha is the only reliable signal for
                // "is this pixel actually part of the gun." Testing px.r here
                // was why every pixel in the sprite rect counted as solid.
                if (px.a <= 0.5f) continue;

                Vector2Int local = new Vector2Int(x, y);
                templateSolidLocal.Add(local);

                float relX = (float)(x - centerX) / Mathf.Max(1, centerX);
                float relY = (float)(y - centerY) / Mathf.Max(1, centerY);

                GunPart part = GunPart.Body;
                if (relX > 0.2f && Mathf.Abs(relY) < 0.3f) part = GunPart.Barrel;
                else if (relX < -0.2f && relY < -0.2f) part = GunPart.Handle;
                else if (relY < -0.3f && Mathf.Abs(relX) < 0.3f) part = GunPart.Grip;
                else if (relY > 0.3f && Mathf.Abs(relX) < 0.2f) part = GunPart.Sight;

                templatePartMap[local] = part;
            }
        }

        Debug.Log($"GunPickupManager: template part map built - {templateSolidLocal.Count} solid cells out of {templateWidth * templateHeight} total.");
    }

    string GenerateRuleFromShape(HashSet<Vector2Int> cells, bool isBirth)
    {
        int maxRuleLength = Mathf.Clamp(Mathf.CeilToInt(cells.Count / 10f), 1, isBirth ? 6 : 9);
        int cellCountInfluence = Mathf.Abs(cells.Count * 7919) % 88888888;
        int randomValue = UnityEngine.Random.Range(0, 88888888);
        int num = (cellCountInfluence + randomValue) % 88888888;
        List<char> unique = new List<char>();

        foreach (char ch in num.ToString())
        {
            if (isBirth && (ch == '9' || ch == '0' || ch == '1')) continue;
            if (!isBirth && ch == '9') continue;
            if (unique.Contains(ch)) continue;

            unique.Add(ch);
            if (unique.Count >= maxRuleLength) break;
        }

        return new string(unique.ToArray());
    }

    bool VerifyShapeMatchesTemplate(HashSet<Vector2Int> cells, Vector2Int center)
    {
        // Calculate the bounding box of the cells
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var p in cells)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }
        
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        
        // Check if size matches template roughly
        if (width > templateWidth * 3 || height > templateHeight * 3) return false;
        if (width < templateWidth / 3 || height < templateHeight / 3) return false;
        
        // Check cell count ratio
        float cellDensity = cells.Count / (float)(width * height);
        if (cellDensity < 0.3f) return false;
        
        return true;
    }

    Sprite CreateSpriteFromCells(HashSet<Vector2Int> cells, GunData data, Color[] snapshot)
    {
        // Calculate bounding box
        int minX = int.MaxValue, maxX = int.MinValue;
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var p in cells)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }
        
        // Add padding for visual effects
        int padding = 2;
        int w = maxX - minX + 1 + padding * 2;
        int h = maxY - minY + 1 + padding * 2;
        
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        
        Color[] colors = new Color[w * h];
        
        // Base colors
        Color baseColor = new Color(data.automatonID.x, data.automatonID.y, data.automatonID.z, 1f);
        Color darkColor = new Color(data.automatonID.x * 0.3f, data.automatonID.y * 0.3f, data.automatonID.z * 0.3f, 1f);
        Color lightColor = new Color(
            Mathf.Clamp01(data.automatonID.x + 0.3f), 
            Mathf.Clamp01(data.automatonID.y + 0.3f), 
            Mathf.Clamp01(data.automatonID.z + 0.3f), 
            1f
        );
        Color accentColor = new Color(
            Mathf.Clamp01(data.automatonID.x + 0.5f), 
            Mathf.Clamp01(data.automatonID.y + 0.2f), 
            Mathf.Clamp01(data.automatonID.z + 0.5f), 
            1f
        );
        
        // Get snapshot for alpha values
        //Color[] snapshot = caController.LatestSnapshot;
        int width = caController.width;
        
        // First pass: determine which cells are part of the gun
        bool[,] isGunCell = new bool[w, h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Vector2Int pos = new Vector2Int(minX + x - padding, minY + y - padding);
                isGunCell[x, y] = cells.Contains(pos);
            }
        }
        
        // Second pass: fill in dead cells (holes) within the gun shape
        bool[,] filledCells = (bool[,])isGunCell.Clone();
        
        // Fill holes: any empty cell surrounded by solid cells on 4 sides becomes filled
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                if (!isGunCell[x, y])
                {
                    bool surrounded = isGunCell[x-1, y] && isGunCell[x+1, y] && 
                                    isGunCell[x, y-1] && isGunCell[x, y+1];
                    if (surrounded)
                    {
                        filledCells[x, y] = true;
                    }
                }
            }
        }
        
        // Calculate center for part detection
        int centerX = w / 2;
        int centerY = h / 2;
        
        // Generate colors for each pixel
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Vector2Int pos = new Vector2Int(minX + x - padding, minY + y - padding);
                bool isSolid = filledCells[x, y];
                
                if (isSolid)
                {
                    // Get alpha from CA state if available
                    float alpha = 0.85f;
                    float state = 1f;
                    
                    if (snapshot != null && pos.x >= 0 && pos.x < width && pos.y >= 0 && pos.y < caController.height)
                    {
                        int index = pos.y * width + pos.x;
                        Color cellColor = snapshot[index];
                        state = cellColor.a;
                        
                        if (state > 0.5f && state < 1.5f)
                            alpha = 0.95f; // Alive
                        else if (state >= 1.5f && state < caController.Decay)
                            alpha = 0.7f - (state - 1.5f) / (caController.Decay - 1.5f) * 0.3f; // Decaying
                        else
                            alpha = 0.5f; // Dead but filled
                    }
                    
                    // Keep relX/relY - the separator and edge-glow logic further down still
                    // uses them for line placement. But part *color* now comes from the real
                    // generated part map instead of a positional guess.
                    float relX = (float)(x - centerX) / centerX;
                    float relY = (float)(y - centerY) / centerY;

                    GunPart partHere = GunPart.Body;
                    lastGunPartMap?.TryGetValue(pos, out partHere);

                    Color partColor = baseColor;
                    switch (partHere)
                    {
                        case GunPart.Barrel: partColor = lightColor; break;
                        case GunPart.Handle: partColor = darkColor; break;
                        case GunPart.Grip:   partColor = accentColor; break;
                        case GunPart.Sight:  partColor = lightColor; break;
                        default:             partColor = baseColor; break;
                    }
                    
                    // Add horizontal lines (futuristic look)
                    bool isHorizontalLine = false;
                    int lineSpacing = 3;
                    for (int lineY = 0; lineY < h; lineY += lineSpacing)
                    {
                        if (Mathf.Abs(y - lineY) <= 1 && isGunCell[x, lineY])
                        {
                            isHorizontalLine = true;
                            break;
                        }
                    }
                    
                    if (isHorizontalLine && !(relX > 0.2f && Mathf.Abs(relY) < 0.3f)) // Don't line the barrel
                    {
                        partColor = Color.Lerp(partColor, darkColor, 0.4f);
                    }
                    
                    // Add part separators (dark lines between parts)
                    bool isSeparator = false;
                    // Vertical separator between barrel and body
                    if (Mathf.Abs(x - centerX - 2) <= 1 && relX > 0 && relX < 0.3f && Mathf.Abs(relY) < 0.4f)
                        isSeparator = true;
                    // Horizontal separator between grip and body
                    if (Mathf.Abs(y - centerY + 2) <= 1 && relY < -0.1f && relY > -0.3f && Mathf.Abs(relX) < 0.3f)
                        isSeparator = true;
                    
                    if (isSeparator)
                    {
                        partColor = darkColor;
                    }
                    
                    // Add glow effect on the edges
                    bool isEdge = false;
                    // Check if this is on the edge of the gun
                    if (x == 0 || x == w - 1 || y == 0 || y == h - 1)
                    {
                        // Check if neighboring cell is empty
                        if (x > 0 && !filledCells[x-1, y]) isEdge = true;
                        else if (x < w-1 && !filledCells[x+1, y]) isEdge = true;
                        else if (y > 0 && !filledCells[x, y-1]) isEdge = true;
                        else if (y < h-1 && !filledCells[x, y+1]) isEdge = true;
                    }
                    
                    if (isEdge)
                    {
                        // Brighten the edge
                        partColor = Color.Lerp(partColor, lightColor, 0.3f);
                    }
                    
                    // Apply alpha
                    colors[y * w + x] = new Color(partColor.r, partColor.g, partColor.b, alpha);
                }
                else
                {
                    colors[y * w + x] = Color.clear;
                }
            }
        }
        
        tex.SetPixels(colors);
        tex.Apply();
        
        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 1f);
        sprite.name = "GunPickup";
        return sprite;
    }
}