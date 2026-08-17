using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class CAController : MonoBehaviour
{
    [Header("Target Renderer (Quad)")]
    public Renderer targetRenderer;

    [Header("Compute Shader")]
    public ComputeShader cellularAutomaton;

    [Header("World")]
    public int width = 512;
    public int height = 512;

    [Header("Simulation")]
    public float updateRate = 10f;

    [Header("Decay Behaviour")]
    public bool decayCountUp = false;

    [Header("Automaton Rules (Birth/Survival/Decay)")]
    public string rules = "3/23/0";

    [Header("Automaton Identity")]
    public Vector3 automatonID = new Vector3(1f, 0f, 0f);

    public event Action OnCAStepped;

    public RenderTexture CurrentTexture => current;
    public int Decay { get; private set; }
    public Color[] LatestSnapshot { get; private set; }

    private RenderTexture current;
    private RenderTexture next;
    private RenderTexture tempOutput; // Persistent temp texture for direct modifications

    private int kernelInit = -1;
    private int kernelStep = -1;
    private int kernelPlayerClear = -1;
    private int kernelPlayerDraw = -1;
    private int kernelGunImpact = -1;
    private int kernelGunStep = -1;

    private float timer;

    // Readback management
    private bool readbackInProgress = false;
    private List<Action<Color[]>> pendingCallbacks = new List<Action<Color[]>>();

    void Awake()
    {
        kernelInit = FindKernelSafely("Init");
        kernelStep = FindKernelSafely("Step");
        kernelPlayerClear = FindKernelSafely("PlayerClear");
        kernelPlayerDraw = FindKernelSafely("PlayerDraw");
        kernelGunImpact = FindKernelSafely("GunImpact");
        kernelGunStep = FindKernelSafely("GunStep");

        current = CreateRenderTexture();
        next = CreateRenderTexture();
        tempOutput = CreateRenderTexture(); // Create persistent temp texture

        SetupComputeShader();
        Initialize();
        ApplyTextureToPlane();
    }

    int FindKernelSafely(string kernelName)
    {
        if (cellularAutomaton == null)
        {
            Debug.LogError("CA-REAPER: ComputeShader not assigned!");
            return -1;
        }
        if (!cellularAutomaton.HasKernel(kernelName))
        {
            Debug.LogWarning($"CA-REAPER: Kernel '{kernelName}' not found.");
            return -1;
        }
        return cellularAutomaton.FindKernel(kernelName);
    }

    void SetRules()
    {
        string[] parts = rules.Split('/');
        if (parts.Length != 3)
        {
            Debug.LogError("CA-REAPER: Invalid rule format! Use B/S/D");
            return;
        }

        int birthMask = ParseRuleMask(parts[0]);
        int survivalMask = ParseRuleMask(parts[1]);

        cellularAutomaton.SetInt("BirthMask", birthMask);
        cellularAutomaton.SetInt("SurvivalMask", survivalMask);

        Decay = int.Parse(parts[2]);
        cellularAutomaton.SetInt("Decay", Decay);
    }

    public int ParseRuleMask(string rule)
    {
        int mask = 0;
        foreach (char c in rule)
        {
            if (c >= '0' && c <= '8')
                mask |= (1 << (c - '0'));
        }
        return mask;
    }

    RenderTexture CreateRenderTexture()
    {
        RenderTexture texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat);
        texture.enableRandomWrite = true;
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.Create();
        return texture;
    }

    void SetupComputeShader()
    {
        cellularAutomaton.SetInt("Width", width);
        cellularAutomaton.SetInt("Height", height);
        cellularAutomaton.SetVector("AutomatonID", new Vector4(automatonID.x, automatonID.y, automatonID.z, 1));
        cellularAutomaton.SetInt("DecayCountUp", decayCountUp ? 1 : 0);
        SetRules();
    }

    void Initialize()
    {
        if (kernelInit < 0)
        {
            Debug.LogError("CA-REAPER: Init kernel not found!");
            return;
        }
        cellularAutomaton.SetTexture(kernelInit, "Result", current);
        cellularAutomaton.SetInt("RandomSeed", UnityEngine.Random.Range(0, int.MaxValue));
        DispatchKernel(kernelInit, "Init");
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= 1f / updateRate)
        {
            timer = 0f;
            Step();
        }
    }

    void Step()
    {
        if (kernelStep < 0)
        {
            Debug.LogError("CA-REAPER: Step kernel not found!");
            return;
        }

        cellularAutomaton.SetTexture(kernelStep, "Current", current);
        cellularAutomaton.SetTexture(kernelStep, "Result", next);
        DispatchKernel(kernelStep, "Step");

        RenderTexture temp = current;
        current = next;
        next = temp;

        targetRenderer.material.mainTexture = current;
        OnCAStepped?.Invoke();
    }

    void DispatchKernel(int kernelIndex, string kernelName)
    {
        if (kernelIndex < 0)
        {
            Debug.LogError($"CA-REAPER: Cannot dispatch {kernelName} - invalid kernel index");
            return;
        }
        int groupsX = Mathf.CeilToInt(width / 8f);
        int groupsY = Mathf.CeilToInt(height / 8f);
        cellularAutomaton.Dispatch(kernelIndex, groupsX, groupsY, 1);
    }

    public void ModifyTextureDirect(int kernel, int groupsX, int groupsY)
    {
        if (kernel < 0)
        {
            Debug.LogError("CA-REAPER: Cannot modify texture - invalid kernel");
            return;
        }
        if (current == null)
        {
            Debug.LogError("CA-REAPER: Current texture is null!");
            return;
        }

        // Use persistent tempOutput instead of creating new temporary texture
        cellularAutomaton.SetTexture(kernel, "Current", current);
        cellularAutomaton.SetTexture(kernel, "Result", tempOutput);
        cellularAutomaton.Dispatch(kernel, groupsX, groupsY, 1);

        // Swap current and tempOutput so the result becomes the new current
        RenderTexture swap = current;
        current = tempOutput;
        tempOutput = swap;

        targetRenderer.material.mainTexture = current;
    }

    public int GetKernelIndex(string kernelName)
    {
        switch (kernelName)
        {
            case "Init": return kernelInit;
            case "Step": return kernelStep;
            case "PlayerClear": return kernelPlayerClear;
            case "PlayerDraw": return kernelPlayerDraw;
            case "GunImpact": return kernelGunImpact;
            case "GunStep": return kernelGunStep;
            default:
                Debug.LogError($"CA-REAPER: Unknown kernel name '{kernelName}'");
                return -1;
        }
    }

    public void RequestColorData(Action<Color[]> callback)
    {
        if (CurrentTexture == null)
        {
            callback?.Invoke(null);
            return;
        }

        // Add callback to pending list
        pendingCallbacks.Add(callback);
        
        // If a readback is already in progress, just queue the callback
        if (readbackInProgress) return;

        readbackInProgress = true;
        AsyncGPUReadback.Request(CurrentTexture, 0, request =>
        {
            Color[] data = null;
            if (!request.hasError)
            {
                var raw = request.GetData<Color>();
                data = new Color[raw.Length];
                raw.CopyTo(data);
                LatestSnapshot = data; // Cache the latest snapshot
            }

            // Distribute data to all queued callbacks
            var callbacks = pendingCallbacks;
            pendingCallbacks = new List<Action<Color[]>>();
            readbackInProgress = false;

            foreach (var cb in callbacks)
            {
                cb?.Invoke(data);
            }
        });
    }

    public bool IsSolid(Color cell)
    {
        float a = cell.a;
        if (a >= 20f && a < 30f) return true;
        if (Decay > 1) return a > 0.5f && a < Decay - 0.5f;
        return a > 0.5f && a < 1.5f;
    }

    public bool IsGunCA(Color cell) => cell.a >= 20f && cell.a < 30f;

    void ApplyTextureToPlane()
    {
        if (targetRenderer == null)
        {
            Debug.LogError("CA-REAPER: Target Renderer is not assigned!");
            return;
        }
        targetRenderer.material.mainTexture = current;
    }

    void OnDestroy()
    {
        if (current != null) current.Release();
        if (next != null) next.Release();
        if (tempOutput != null) tempOutput.Release();
    }
}