using UnityEngine;

public enum RoundPhase { Prep, Hunt }

// Минимальный раунд для теста вселения: подготовка -> охота. Лобби и охотник придут позже.
public class RoundState : MonoBehaviour
{
    public static RoundState Instance { get; private set; }
    public float prepDuration = 20f;   // GDD: по умолчанию 20 сек
    public RoundPhase Phase { get; private set; } = RoundPhase.Prep;
    float phaseStart;

    public float PrepRemaining => Mathf.Max(0f, prepDuration - (Time.time - phaseStart));

    void Awake() { Instance = this; phaseStart = Time.time; }

    void Update()
    {
        if (Phase == RoundPhase.Prep && PrepRemaining <= 0f) EndPrep();
    }

    public void EndPrep()
    {
        Phase = RoundPhase.Hunt;
        foreach (var p in FindObjectsByType<HiderPlayer>(FindObjectsSortMode.None))
            if (p.CurrentProp == null) p.AutoPossess();
    }
}
