using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class GunController : MonoBehaviour
{
    public CAController caController;
    public string rules = "";
    public Vector3 automatonID = new Vector3(1f, 0f, 0f);
    public int fireRate = 1;
    public int AOE = 1;
    public int Decay { get; private set; }

    private int kernelGunImpact;
    private int kernelGunStep;
    private float fireCooldown;
    
    [Header("Visual Effects")]
    public LineRenderer shotLine;
    public float shotLineDuration = 0.1f;
    public Color shotLineColor = Color.yellow;
    public float shotLineWidth = 0.1f;
    
    [Header("Raycast Settings")]
    public float maxShotDistance = 50f;
    
    private Color[] caSnapshot;
    private bool snapshotReady;
    private bool isGunCAActive = false;

    void Start() 
    {
        GenerateGunValues();
        Debug.Log("Gun rules: " + rules);
        
        kernelGunImpact = caController.GetKernelIndex("GunImpact");
        kernelGunStep = caController.GetKernelIndex("GunStep");
        
        if (kernelGunImpact < 0 || kernelGunStep < 0)
        {
            Debug.LogWarning("CA-REAPER: Gun kernels not found. Gun won't work without them.");
        }
        
        SetComputeShaderForGun();
        fireCooldown = 0;
        
        if (shotLine == null)
        {
            GameObject lineObj = new GameObject("ShotLine");
            lineObj.transform.SetParent(transform);
            shotLine = lineObj.AddComponent<LineRenderer>();
        }
        
        SetupShotLine();
        
        InvokeRepeating("UpdateSnapshot", 0.1f, 0.1f);
        
        // Subscribe to CA steps to update gun CA
        caController.OnCAStepped += StepGunCA;
    }

    void OnDestroy()
    {
        caController.OnCAStepped -= StepGunCA;
    }

    void UpdateSnapshot()
    {
        if (caController.CurrentTexture == null)
            return;
        
        AsyncGPUReadback.Request(caController.CurrentTexture, 0, request =>
        {
            if (request.hasError)
                return;
            
            var data = request.GetData<Color>();
            
            if (caSnapshot == null || caSnapshot.Length != data.Length)
                caSnapshot = new Color[data.Length];
            
            data.CopyTo(caSnapshot);
            snapshotReady = true;
        });
    }

    void SetupShotLine()
    {
        shotLine.material = new Material(Shader.Find("Sprites/Default"));
        shotLine.positionCount = 2;
        shotLine.enabled = false;
        shotLineColor.r = automatonID.x;
        shotLineColor.g = automatonID.y;
        shotLineColor.b = automatonID.z;
        shotLine.startColor = shotLineColor;
        shotLine.endColor = shotLineColor;
        shotLineWidth = AOE * 0.003f;
        shotLine.startWidth = shotLineWidth;
        shotLine.endWidth = shotLineWidth;
    }

    void Update() 
    {
        Vector3 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        mousePos.z = 0;
        
        Vector3 dir = mousePos - transform.position;
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
        
        fireCooldown -= Time.deltaTime;
        
        if (Input.GetMouseButtonDown(0) && fireCooldown <= 0)
        {
            FireGun();
            fireCooldown = 1f / Mathf.Max(1, fireRate);
        }
    }

    public void FireGun() 
    {
        Vector2 rayDirection = transform.right;
        Vector2 rayOrigin = transform.position;
        
        Vector2Int? hitCell = RaycastThroughGrid(rayOrigin, rayDirection);
        
        if (hitCell.HasValue)
        {
            Vector2Int gridPos = hitCell.Value;
            Vector3 worldPos = GridToWorld(gridPos);
            
            Debug.Log($"Hit cell at grid position: {gridPos}");
            ShowShotLine(rayOrigin, worldPos);
            ImpactAt(gridPos);
        }
        else
        {
            Debug.Log("No cell hit - line continues");
            Vector3 endPoint = rayOrigin + (Vector2)(rayDirection * maxShotDistance);
            ShowShotLine(rayOrigin, endPoint);
        }
    }

    Vector2Int? RaycastThroughGrid(Vector2 origin, Vector2 direction)
    {
        if (!snapshotReady)
            return null;
        
        Vector2Int currentGrid = WorldToGrid(origin);
        
        if (IsCellAlive(currentGrid))
            return currentGrid;
        
        float stepX = direction.x >= 0 ? 1 : -1;
        float stepY = direction.y >= 0 ? 1 : -1;
        
        Vector3 worldOrigin = origin;
        Bounds bounds = caController.targetRenderer.bounds;
        float cellWidth = bounds.size.x / caController.width;
        float cellHeight = bounds.size.y / caController.height;
        
        float tMaxX, tMaxY;
        
        if (direction.x != 0)
        {
            float nextBoundaryX = (currentGrid.x + (stepX > 0 ? 1 : 0)) * cellWidth + bounds.min.x;
            tMaxX = (nextBoundaryX - worldOrigin.x) / direction.x;
        }
        else
        {
            tMaxX = float.MaxValue;
        }
        
        if (direction.y != 0)
        {
            float nextBoundaryY = (currentGrid.y + (stepY > 0 ? 1 : 0)) * cellHeight + bounds.min.y;
            tMaxY = (nextBoundaryY - worldOrigin.y) / direction.y;
        }
        else
        {
            tMaxY = float.MaxValue;
        }
        
        float tDeltaX = Mathf.Abs(cellWidth / direction.x);
        float tDeltaY = Mathf.Abs(cellHeight / direction.y);
        
        float maxT = maxShotDistance / Mathf.Max(0.001f, direction.magnitude);
        float t = 0;
        
        while (t < maxT)
        {
            if (tMaxX < tMaxY)
            {
                currentGrid.x += (int)stepX;
                t = tMaxX;
                tMaxX += tDeltaX;
            }
            else
            {
                currentGrid.y += (int)stepY;
                t = tMaxY;
                tMaxY += tDeltaY;
            }
            
            if (currentGrid.x < 0 || currentGrid.x >= caController.width ||
                currentGrid.y < 0 || currentGrid.y >= caController.height)
                return null;
            
            if (IsCellAlive(currentGrid))
                return currentGrid;
        }
        
        return null;
    }

    void ImpactAt(Vector2Int impactPoint)
    {
        impactPoint.x = Mathf.Clamp(impactPoint.x, 0, caController.width - 1);
        impactPoint.y = Mathf.Clamp(impactPoint.y, 0, caController.height - 1);
        
        var shader = caController.cellularAutomaton;
        shader.SetInts("GunImpactCenter", impactPoint.x, impactPoint.y);
        shader.SetInt("GunImpactRadius", AOE);
        shader.SetInt("GunStepCount", 0);
        
        int groupsX = Mathf.CeilToInt(caController.width / 8f);
        int groupsY = Mathf.CeilToInt(caController.height / 8f);
        
        caController.ModifyTextureDirect(kernelGunImpact, groupsX, groupsY);
        
        isGunCAActive = true;
        
        UpdateSnapshot();
    }

    bool HasGunCACells()
    {
        if (!snapshotReady)
            return false;
        
        // Quick check - look for any gun CA cells in snapshot
        for (int i = 0; i < caSnapshot.Length; i++)
        {
            if (caSnapshot[i].a >= 20.0f && caSnapshot[i].a < 30.0f)
                return true;
        }
        
        return false;
    }

    void StepGunCA()
    {
        // Check if there are any gun CA cells
        if (!HasGunCACells())
        {
            return;
        }
        
        var shader = caController.cellularAutomaton;
        
        // Debug the current gun parameters
        Debug.Log($"Stepping gun CA with Birth={rules.Split('/')[0]}, Survival={rules.Split('/')[1]}");
        
        int groupsX = Mathf.CeilToInt(caController.width / 8f);
        int groupsY = Mathf.CeilToInt(caController.height / 8f);
        
        caController.ModifyTextureDirect(kernelGunStep, groupsX, groupsY);
        
        UpdateSnapshot();
    }

    void ShowShotLine(Vector3 start, Vector3 end)
    {
        StartCoroutine(DisplayShotLine(start, end));
    }

    IEnumerator DisplayShotLine(Vector3 start, Vector3 end)
    {
        shotLine.enabled = true;
        shotLine.SetPosition(0, start);
        shotLine.SetPosition(1, end);
        
        yield return new WaitForSeconds(shotLineDuration);
        
        shotLine.enabled = false;
    }

    bool IsCellAlive(Vector2Int pos)
    {
        if (!snapshotReady)
            return false;
        
        if (pos.x < 0 || pos.x >= caController.width || 
            pos.y < 0 || pos.y >= caController.height)
            return false;
        
        Color pixel = caSnapshot[pos.y * caController.width + pos.x];
        float alpha = pixel.a;
        
        if (alpha >= 20.0f && alpha < 30.0f)
            return true; // Gun CA
        
        if (caController.Decay > 1)
        {
            return alpha > 0.5f && alpha < caController.Decay - 0.5f;
        }
        
        return alpha > 0.5f && alpha < 1.5f;
    }

    Vector2Int WorldToGrid(Vector3 worldPos)
    {
        Bounds b = caController.targetRenderer.bounds;
        
        float u = (worldPos.x - b.min.x) / b.size.x;
        float v = (worldPos.y - b.min.y) / b.size.y;
        
        int x = Mathf.FloorToInt(u * caController.width);
        int y = Mathf.FloorToInt(v * caController.height);
        
        return new Vector2Int(x, y);
    }

    Vector3 GridToWorld(Vector2Int gridPos)
    {
        Bounds b = caController.targetRenderer.bounds;
        
        float u = (gridPos.x + 0.5f) / caController.width;
        float v = (gridPos.y + 0.5f) / caController.height;
        
        float x = Mathf.Lerp(b.min.x, b.max.x, u);
        float y = Mathf.Lerp(b.min.y, b.max.y, v);
        
        return new Vector3(x, y, 0);
    }

    public void GenerateGunValues() 
    {
        if (rules == "")
        {
            int birthNum = UnityEngine.Random.Range(0, 88888888);
            string birthStr = birthNum.ToString();
            var birthList = new List<char>();
            foreach (char ch in birthStr)
            {
                if (ch == '9' || ch == '0' || ch == '1') continue; // could use some tweaking
                if (!birthList.Contains(ch)) birthList.Add(ch);
            }
            birthStr = new string(birthList.ToArray());

            int survivalNum = UnityEngine.Random.Range(0, 88888888);
            string survivalStr = survivalNum.ToString();
            var survivalList = new List<char>();
            foreach (char ch in survivalStr)
            {
                if (ch == '9') continue;
                if (!survivalList.Contains(ch)) survivalList.Add(ch);
            }
            survivalStr = new string(survivalList.ToArray());

            int decayNum = UnityEngine.Random.Range(0, 8);
            string decayStr = decayNum.ToString();

            rules = birthStr + "/" + survivalStr + "/" + decayStr;
        }

        automatonID = new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value);
        if (automatonID.x == 0 && automatonID.y == 0 && automatonID.z == 1) 
        {
            automatonID = new Vector3(1f, 1f, 1f);
        }
        fireRate = UnityEngine.Random.Range(1, 50);
        AOE = UnityEngine.Random.Range(1, 20);
    }

    public void SetComputeShaderForGun() 
    {
        string[] parts = rules.Split('/');
        
        if (parts.Length != 3)
        {
            Debug.LogError($"Invalid gun rules format: {rules}. Using defaults.");
            rules = "3/23/0";
            parts = rules.Split('/');
        }
        
        int birthMask = caController.ParseRuleMask(parts[0]);
        int survivalMask = caController.ParseRuleMask(parts[1]);
        
        Debug.Log($"Gun rules: Birth={parts[0]} (mask {birthMask}), Survival={parts[1]} (mask {survivalMask}), Decay={parts[2]}");
        
        caController.cellularAutomaton.SetInt("CurrentGunBirthMask", birthMask);
        caController.cellularAutomaton.SetInt("CurrentGunSurvivalMask", survivalMask);

        Decay = int.Parse(parts[2]);
        caController.cellularAutomaton.SetInt("CurrentGunDecay", Decay);

        caController.cellularAutomaton.SetVector("CurrentGunAutomatonID", automatonID);
    }
}