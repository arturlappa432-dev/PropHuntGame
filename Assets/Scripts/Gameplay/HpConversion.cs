using UnityEngine;

// Пересчёт HP при смене облика (docs/balance.md, «Конвертация HP при смене облика»).
public static class HpConversion
{
    public const float BaseMultiplier = 1.15f;
    public const float TierStep = 0.10f;
    public const float MinFraction = 0.10f;

    // Арифметика в decimal: на float значения вида 71,5 / 8,5 приходят как 71,4999 / 8,5000002 и округление «половина вверх» ломается.
    public static int Convert(int hpCurrent, int hpMaxOld, int hpMaxNew, PropTier tierOld, PropTier tierNew)
    {
        decimal r = (decimal)hpCurrent / hpMaxOld;
        decimal w = 1m - r;
        decimal m = 1.15m + 0.10m * System.Math.Max(0, (int)tierNew - (int)tierOld);
        decimal rNew = System.Math.Max(1m - w * m, System.Math.Min(r, 0.10m));
        // round half up (AwayFromZero при неотрицательных значениях), не банковское округление
        int hp = (int)System.Math.Round(rNew * hpMaxNew, System.MidpointRounding.AwayFromZero);
        return Mathf.Clamp(hp, 1, hpMaxNew);
    }
}
