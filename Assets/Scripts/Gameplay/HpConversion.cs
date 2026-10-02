using UnityEngine;

// Пересчёт HP при смене облика (docs/balance.md, «Конвертация HP при смене облика»).
public static class HpConversion
{
    public const float BaseMultiplier = 1.15f;
    public const float TierStep = 0.10f;
    public const float MinFraction = 0.10f;

    public static int Convert(int hpCurrent, int hpMaxOld, int hpMaxNew, PropTier tierOld, PropTier tierNew)
    {
        float r = (float)hpCurrent / hpMaxOld;
        float w = 1f - r;
        float m = BaseMultiplier + TierStep * Mathf.Max(0, (int)tierNew - (int)tierOld);
        float rNew = Mathf.Max(1f - w * m, Mathf.Min(r, MinFraction));
        // округление «половина вверх» (как в таблице balance.md), а не банковское Mathf.RoundToInt
        int hp = (int)System.Math.Round((double)rNew * hpMaxNew, System.MidpointRounding.AwayFromZero);
        return Mathf.Clamp(hp, 1, hpMaxNew);
    }
}
