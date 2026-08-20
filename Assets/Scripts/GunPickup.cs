using System.Collections.Generic;
using UnityEngine;

public class GunPickup : MonoBehaviour
{
    public GunData gunData;
    public SpriteRenderer spriteRenderer;
    public Vector2Int gridPosition;
    public Vector2Int muzzlePosition;
    public float pickupCooldown;
    
    // Store reference to CAController
    private CAController caController;
    private HashSet<Vector2Int> gunCells;

    void Awake()
    {
        if (spriteRenderer == null)
            spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer == null)
            spriteRenderer = gameObject.AddComponent<SpriteRenderer>();
    }

    void Update()
    {
        pickupCooldown -= Time.deltaTime;
        if (pickupCooldown > 0) return;
        
        // Check if player is near this pickup
        if (caController == null) return;
        
        PlayerController player = FindObjectOfType<PlayerController>();
        if (player == null || player.IsDead) return;
        
        // Get player's occupied cells
        Vector2Int playerOrigin = player.Origin;
        Vector2Int[] playerOffsets = player.Offsets;
        bool[] playerAlive = player.OffsetAlive;
        
        // Check if any gun cell overlaps with any player cell
        bool playerOverlaps = false;
        foreach (Vector2Int worldGunCell in gunCells)
        {
            for (int i = 0; i < playerOffsets.Length; i++)
            {
                if (!playerAlive[i]) continue;
                Vector2Int playerCell = playerOrigin + playerOffsets[i];

                if (worldGunCell == playerCell)
                {
                    playerOverlaps = true;
                    Debug.Log($"Gun pickup: Overlap detected at cell {worldGunCell}");
                    break;
                }
            }
            if (playerOverlaps) break;
        }
        
        if (playerOverlaps)
        {
            PickupGun(player);
        }
    }

    void PickupGun(PlayerController player)
    {
        pickupCooldown = 0.5f;
        
        GunController gun = FindObjectOfType<GunController>();
        if (gun != null && caController != null)
        {
            Debug.Log($"Picking up gun! Rules: {gunData.rules}, FireRate: {gunData.fireRate}");
            
            gun.SetGunData(gunData);
            
            if (gun.muzzle != null)
            {
                Bounds b = caController.targetRenderer.bounds;
                float cellWidth = b.size.x / caController.width;
                float cellHeight = b.size.y / caController.height;

                Vector2Int deltaGrid = muzzlePosition - gridPosition;
                Vector3 localOffset = new Vector3(deltaGrid.x * cellWidth, deltaGrid.y * cellHeight, 0f);

                gun.muzzle.localPosition = localOffset;
                gun.body.sprite = GetComponent<SpriteRenderer>().sprite;
            }

            // Full reset of gun CA across the entire world on pickup - not just
            // the cells belonging to the gun that was just picked up.
            caController.ClearAllGunCA();
            
            Destroy(gameObject);
        }
        else
        {
            Debug.LogWarning("Gun pickup failed: GunController or CAController is null!");
        }
    }

    public void Initialize(GunData data, Sprite sprite, Vector2Int pos, CAController ca, HashSet<Vector2Int> cells)
    {
        gunData = data;
        spriteRenderer.sprite = sprite;
        gridPosition = pos;
        caController = ca; // Store reference
        
        // Find muzzle position (rightmost cell)
        muzzlePosition = FindMuzzlePosition(cells);
        
        // Position at grid center
        transform.position = GridToWorld(pos, ca);
        
        // Scale sprite to match CA cell size
        Bounds b = ca.targetRenderer.bounds;
        float cellWidth = b.size.x / ca.width;
        float cellHeight = b.size.y / ca.height;
        transform.localScale = new Vector3(cellWidth, cellHeight, 1f);

        this.gunCells = new HashSet<Vector2Int>(cells); // Store cells
        
        // Add trigger collider
        //BoxCollider2D collider = GetComponent<BoxCollider2D>();
        //if (collider == null) collider = gameObject.AddComponent<BoxCollider2D>();
        //collider.isTrigger = true;
        //collider.size = sprite.bounds.size;
    }

    Vector2Int FindMuzzlePosition(HashSet<Vector2Int> cells)
    {
        // Find the rightmost cell(s)
        int maxX = int.MinValue;
        List<Vector2Int> rightmost = new List<Vector2Int>();
        
        foreach (var p in cells)
        {
            if (p.x > maxX)
            {
                maxX = p.x;
                rightmost.Clear();
                rightmost.Add(p);
            }
            else if (p.x == maxX)
            {
                rightmost.Add(p);
            }
        }
        
        // If multiple cells at maxX, use the middle one
        if (rightmost.Count > 1)
        {
            rightmost.Sort((a, b) => a.y.CompareTo(b.y));
            return rightmost[rightmost.Count / 2];
        }
        else if (rightmost.Count == 1)
        {
            return rightmost[0];
        }
        
        // Fallback to center
        return gridPosition;
    }

    Vector3 GridToWorld(Vector2Int gridPos, CAController ca)
    {
        Bounds b = ca.targetRenderer.bounds;
        float u = (gridPos.x + 0.5f) / ca.width;
        float v = (gridPos.y + 0.5f) / ca.height;
        return new Vector3(
            Mathf.Lerp(b.min.x, b.max.x, u),
            Mathf.Lerp(b.min.y, b.max.y, v),
            0
        );
    }
}

[System.Serializable]
public struct GunData
{
    public string rules;          // "B/S/D"
    public Vector3 automatonID;
    public int fireRate;
    public int spread;
    public int AOE;
    public int decay;

    public GunData(string rules, Vector3 automatonID, int fireRate, int spread, int AOE, int decay)
    {
        this.rules = rules;
        this.automatonID = automatonID;
        this.fireRate = fireRate;
        this.spread = spread;
        this.AOE = AOE;
        this.decay = decay;
    }
}