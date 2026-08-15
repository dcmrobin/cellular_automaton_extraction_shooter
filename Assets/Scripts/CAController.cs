using UnityEngine;

public class CAController : MonoBehaviour
{
    public Renderer targetRenderer;
    [Header("Compute Shader")]
    public ComputeShader cellularAutomaton;

    [Header("World")]
    public int width = 512;
    public int height = 512;

    [Header("Simulation")]
    public float updateRate = 10f;

    [Header("Automaton Rules (Birth/Survival/Decay)")]
    public string rules = "3/23/0";

    [Header("Automaton Identity")]
    public Vector3 automatonID = new Vector3(1f, 0f, 0f);

    private RenderTexture current;
    private RenderTexture next;

    private int kernelInit;
    private int kernelStep;

    private float timer;


    void Start()
    {
        kernelInit = cellularAutomaton.FindKernel("Init");
        kernelStep = cellularAutomaton.FindKernel("Step");

        current = CreateRenderTexture();
        next = CreateRenderTexture();

        SetupComputeShader();

        Initialize();

        ApplyTextureToPlane();
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

        cellularAutomaton.SetInt("Decay", int.Parse(parts[2]));
    }

    int ParseRuleMask(string rule)
    {
        int mask = 0;

        foreach (char c in rule)
        {
            if (c >= '0' && c <= '8')
            {
                int number = c - '0';
                mask |= (1 << number);
            }
        }

        return mask;
    }


    RenderTexture CreateRenderTexture()
    {
        RenderTexture texture = new RenderTexture(
            width,
            height,
            0,
            RenderTextureFormat.ARGBFloat
        );

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

        cellularAutomaton.SetVector(
            "AutomatonID",
            new Vector4(
                automatonID.x,
                automatonID.y,
                automatonID.z,
                1
            )
        );

        SetRules();
    }


    void Initialize()
    {
        cellularAutomaton.SetTexture(
            kernelInit,
            "Result",
            current
        );

        cellularAutomaton.SetInt(
            "RandomSeed",
            Random.Range(0, int.MaxValue)
        );

        Dispatch(kernelInit);
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
        cellularAutomaton.SetTexture(
            kernelStep,
            "Current",
            current
        );

        cellularAutomaton.SetTexture(
            kernelStep,
            "Result",
            next
        );

        Dispatch(kernelStep);

        // Swap textures.
        RenderTexture temp = current;
        current = next;
        next = temp;
    }


    void Dispatch(int kernel)
    {
        int groupsX = Mathf.CeilToInt(width / 8f);
        int groupsY = Mathf.CeilToInt(height / 8f);

        cellularAutomaton.Dispatch(
            kernel,
            groupsX,
            groupsY,
            1
        );
    }


    void ApplyTextureToPlane()
    {
        if (targetRenderer == null)
        {
            Debug.LogError("CA-REAPER: Target Renderer is not assigned!");
            return;
        }

        targetRenderer.material.mainTexture = current;

        Debug.Log("CA-REAPER: RenderTexture assigned to Quad.");
    }


    void OnDestroy()
    {
        if (current != null)
            current.Release();

        if (next != null)
            next.Release();
    }
}