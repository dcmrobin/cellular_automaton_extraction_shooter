using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GunController : MonoBehaviour
{
    public CAController caController;
    public string rules = ""; // format: "B/S/D" where B = birth, S = survival, D = decay
    public Vector3 automatonID = new Vector3(1f, 0f, 0f);
    public int fireRate = 1;
    public int CALifespan = 100;
    public int AOE = 1;
    public int Decay { get; private set; }

    void Start() {
        GenerateGunValues();
        Debug.Log("Gun rules: " + rules);
        SetComputeShaderForGun();
    }

    void Update() {
        Vector3 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition);

        Vector3 dir = mousePos - transform.position;
        float angle = Mathf.Atan2(dir.y,dir.x) * Mathf.Rad2Deg;
        transform.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
    }

    public void FireGun() {
        // somehow fire a laser in the direction the gun is pointing, and apply the CA effect to the world at the point of impact
    }

    public void GenerateGunValues() {
        // generate randon rules
        if (rules == "")
        {
            int birthNum = UnityEngine.Random.Range(0, 88888888);
            string birthStr = birthNum.ToString();
            var birthList = new List<char>();
            foreach (char ch in birthStr)
            {
                if (ch == '9') continue;
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

        // set stats
        automatonID = new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value);
        if (automatonID.x == 0 && automatonID.y == 0 && automatonID.z == 1) {
            automatonID = new Vector3(1f, 1f, 1f);
        }
        fireRate = UnityEngine.Random.Range(1, 50);
        CALifespan = UnityEngine.Random.Range(10, 200);
        AOE = UnityEngine.Random.Range(1, 20);
    }

    public void SetComputeShaderForGun() { // called when the gun is picked up
        string[] parts = rules.Split('/');
        int birthMask = caController.ParseRuleMask(parts[0]);
        int survivalMask = caController.ParseRuleMask(parts[1]);
        caController.cellularAutomaton.SetInt("CurrentGunBirthMask", birthMask);
        caController.cellularAutomaton.SetInt("CurrentGunSurvivalMask", survivalMask);

        Decay = int.Parse(parts[2]);
        caController.cellularAutomaton.SetInt("CurrentGunDecay", Decay);

        caController.cellularAutomaton.SetInt("GunCALifespan", CALifespan);

        caController.cellularAutomaton.SetVector("CurrentGunAutomatonID", automatonID);
    }
}