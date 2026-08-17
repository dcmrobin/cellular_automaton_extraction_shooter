using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class GunController : MonoBehaviour
{
    public CAController caController;
    public EnemyManager enemyManager;
    public Transform muzzle;
    public string rules = "";
    public Vector3 automatonID = new Vector3(1f, 0f, 0f);
    public event Action<Vector2Int, int> OnShotFired;
    public int fireRate = 1;
    public int AOE = 1;
    public int spread = 0;
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

    private bool snapshotReady;
    private bool readbackInProgress = false;

    void Start()
    {
        GenerateGunValues();
        Debug.Log("Gun rules: " + rules);

        kernelGunImpact = caController.GetKernelIndex("GunImpact");
        kernelGunStep = caController.GetKernelIndex("GunStep");

        if (kernelGunImpact < 0 || kernelGunStep < 0)
            Debug.LogWarning("CA-REAPER: Gun kernels not found.");

        SetComputeShaderForGun();
        fireCooldown = 0;

        if (shotLine == null)
        {
            GameObject lineObj = new GameObject("ShotLine");
            lineObj.transform.SetParent(transform);
            shotLine = lineObj.AddComponent<LineRenderer>();
        }

        SetupShotLine();
        caController.OnCAStepped += StepGunCA;
    }

    void OnDestroy()
    {
        caController.OnCAStepped -= StepGunCA;
    }

    void RequestSnapshotIfNeeded()
    {
        if (!snapshotReady && !readbackInProgress)
        {
            readbackInProgress = true;
            caController.RequestColorData(data =>
            {
                readbackInProgress = false;
                if (data != null)
                {
                    snapshotReady = true;
                }
            });
        }
    }

    void SetupShotLine()
    {
        shotLine.material = new Material(Shader.Find("Sprites/Default"));
        shotLine.positionCount = 2;
        shotLine.enabled = false;
        shotLineColor = new Color(automatonID.x, automatonID.y, automatonID.z);
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
        if (Input.GetMouseButton(0) && fireCooldown <= 0)
        {
            // Request fresh snapshot before firing if we don't have one
            RequestSnapshotIfNeeded();
            FireGun();
            fireCooldown = 1f / Mathf.Max(1, fireRate);
        }
    }

    public void FireGun()
    {
        var audio = GetComponent<AudioSource>();
        if (audio != null)
        {
            float t = Mathf.Clamp01((AOE - 1f) / 19f);
            audio.pitch = Mathf.Lerp(2f, 0.1f, t);
            audio.Play();
        }

        Vector2 rayDirection = transform.right + new Vector3(0f, UnityEngine.Random.Range(-spread, spread) * 0.01f, 0f);
        Vector2 rayOrigin = muzzle.position;

        Vector2Int? hitCell = RaycastThroughGrid(rayOrigin, rayDirection);
        if (hitCell.HasValue)
        {
            Vector2Int gridPos = hitCell.Value;
            Vector3 worldPos = GridToWorld(gridPos);
            //Debug.Log($"Hit cell at grid position: {gridPos}");
            ShowShotLine(rayOrigin, worldPos);
            ImpactAt(gridPos);
            OnShotFired?.Invoke(gridPos, AOE);
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
        if (caController.LatestSnapshot == null) return null;

        Vector2Int currentGrid = WorldToGrid(origin);
        if (IsCellBlocking(currentGrid)) return currentGrid;

        float stepX = direction.x >= 0 ? 1 : -1;
        float stepY = direction.y >= 0 ? 1 : -1;

        Bounds bounds = caController.targetRenderer.bounds;
        float cellWidth = bounds.size.x / caController.width;
        float cellHeight = bounds.size.y / caController.height;

        float tMaxX, tMaxY;
        if (direction.x != 0)
        {
            float nextBoundaryX = (currentGrid.x + (stepX > 0 ? 1 : 0)) * cellWidth + bounds.min.x;
            tMaxX = (nextBoundaryX - origin.x) / direction.x;
        }
        else tMaxX = float.MaxValue;

        if (direction.y != 0)
        {
            float nextBoundaryY = (currentGrid.y + (stepY > 0 ? 1 : 0)) * cellHeight + bounds.min.y;
            tMaxY = (nextBoundaryY - origin.y) / direction.y;
        }
        else tMaxY = float.MaxValue;

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

            if (IsCellBlocking(currentGrid))
                return currentGrid;
        }
        return null;
    }

    bool IsCellBlocking(Vector2Int pos)
    {
        // Check if there's a solid CA cell here
        if (IsCellAlive(pos))
            return true;
        
        // Check if there's an enemy cell here
        if (enemyManager != null && enemyManager.IsCellOccupiedByEnemy(pos))
            return true;
        
        return false;
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
    }

    bool HasGunCACells()
    {
        if (caController.LatestSnapshot == null) return false;
        for (int i = 0; i < caController.LatestSnapshot.Length; i++)
        {
            if (caController.IsGunCA(caController.LatestSnapshot[i]))
                return true;
        }
        return false;
    }

    void StepGunCA()
    {
        if (!HasGunCACells()) return;

        int groupsX = Mathf.CeilToInt(caController.width / 8f);
        int groupsY = Mathf.CeilToInt(caController.height / 8f);

        caController.ModifyTextureDirect(kernelGunStep, groupsX, groupsY);
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
        if (caController.LatestSnapshot == null) return false;
        if (pos.x < 0 || pos.x >= caController.width || pos.y < 0 || pos.y >= caController.height) return false;
        return caController.IsSolid(caController.LatestSnapshot[pos.y * caController.width + pos.x]);
    }

    Vector2Int WorldToGrid(Vector3 worldPos)
    {
        Bounds b = caController.targetRenderer.bounds;
        float u = (worldPos.x - b.min.x) / b.size.x;
        float v = (worldPos.y - b.min.y) / b.size.y;
        return new Vector2Int(Mathf.FloorToInt(u * caController.width), Mathf.FloorToInt(v * caController.height));
    }

    Vector3 GridToWorld(Vector2Int gridPos)
    {
        Bounds b = caController.targetRenderer.bounds;
        float u = (gridPos.x + 0.5f) / caController.width;
        float v = (gridPos.y + 0.5f) / caController.height;
        return new Vector3(Mathf.Lerp(b.min.x, b.max.x, u), Mathf.Lerp(b.min.y, b.max.y, v), 0);
    }

    public void GenerateGunValues()
    {
        if (rules == "")
        {
            string birthStr = GenerateRuleDigits(true);
            string survivalStr = GenerateRuleDigits(false);
            int decayNum = UnityEngine.Random.Range(0, 8);
            rules = $"{birthStr}/{survivalStr}/{decayNum}";
        }

        automatonID = new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value);
        if (automatonID.x == 0 && automatonID.y == 0 && automatonID.z == 1)
            automatonID = new Vector3(1f, 1f, 1f);

        fireRate = UnityEngine.Random.Range(1, UnityEngine.Random.value > 0.6f ? 15 : 30);
        spread = UnityEngine.Random.Range(0, UnityEngine.Random.value > 0.7f ? 10 : 50);
        AOE = UnityEngine.Random.Range(1, 20);
    }

    string GenerateRuleDigits(bool isBirth)
    {
        int num = UnityEngine.Random.Range(0, 88888888);
        string str = num.ToString();
        List<char> unique = new List<char>();
        foreach (char ch in str)
        {
            if (isBirth && (ch == '9' || ch == '0' || ch == '1')) continue;
            if (!isBirth && ch == '9') continue;
            if (!unique.Contains(ch)) unique.Add(ch);
        }
        return new string(unique.ToArray());
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
        Decay = int.Parse(parts[2]);

        var shader = caController.cellularAutomaton;
        shader.SetInt("CurrentGunBirthMask", birthMask);
        shader.SetInt("CurrentGunSurvivalMask", survivalMask);
        shader.SetInt("CurrentGunDecay", Decay);
        shader.SetVector("CurrentGunAutomatonID", automatonID);
    }
}