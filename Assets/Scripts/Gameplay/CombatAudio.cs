using UnityEngine;

// Процедурные звуки боя (без внешних ассетов, ИИ-ассетов нет): выстрел, попадание, телл промаха, свист near-miss, «пуфф».
public static class CombatAudio
{
    const int Rate = 22050;
    static AudioClip shot, hit, miss, whistle, puff;

    public static AudioClip Shot => shot != null ? shot : shot = Make("shot", 0.4f, (t, r) =>
        (Noise(r) * Mathf.Exp(-t * 16f) + Mathf.Sin(2f * Mathf.PI * 70f * t) * Mathf.Exp(-t * 9f)) * 0.9f);

    public static AudioClip Hit => hit != null ? hit : hit = Make("hit", 0.18f, (t, r) =>
        (Noise(r) * 0.4f + Mathf.Sin(2f * Mathf.PI * (260f - 500f * t) * t)) * Mathf.Exp(-t * 22f));

    // Телл промаха: комичное нисходящее «бвуп» + глухой шлепок. Громкое, чтобы слышали прячущиеся поблизости.
    public static AudioClip Miss => miss != null ? miss : miss = Make("miss_stumble", 0.6f, (t, r) =>
    {
        float slide = Mathf.Sin(2f * Mathf.PI * (520f * t - 450f * t * t) * 1f);
        float thump = Mathf.Sin(2f * Mathf.PI * 80f * t) * Mathf.Exp(-t * 30f);
        return (slide * Mathf.Exp(-t * 3.5f) * 0.6f + thump) * Mathf.Min(1f, t * 80f);
    });

    // Свист near-miss: короткий «вжух», частота взлетает и падает.
    public static AudioClip Whistle => whistle != null ? whistle : whistle = Make("near_miss_whoosh", 0.35f, (t, r) =>
    {
        float k = t / 0.35f;
        float f = 700f + 1500f * Mathf.Sin(Mathf.PI * k);
        return (Mathf.Sin(2f * Mathf.PI * f * t) * 0.5f + Noise(r) * 0.25f) * Mathf.Sin(Mathf.PI * k);
    });

    public static AudioClip Puff => puff != null ? puff : puff = Make("puff", 0.3f, (t, r) =>
        Noise(r) * Mathf.Exp(-t * 12f) * Mathf.Min(1f, t * 200f) * 0.7f);

    // Громкость свиста: у границ слабее, максимум на средней дистанции, вплотную (<2 м) тихо.
    public static float WhistleVolume(float distance)
    {
        if (distance < 2f) return 0f;
        if (distance < 7f) return Mathf.Lerp(0.2f, 1f, (distance - 2f) / 5f);
        return Mathf.Lerp(1f, 0.3f, Mathf.Clamp01((distance - 7f) / 7f));
    }

    // Трёхмерный звук в точке: слышен всем рядом, глохнет с расстоянием.
    // local = звук исходит от самого слушателя (свой выстрел, своё попадание): точка источника совпадает с AudioListener,
    // и 3D-панорама зависит от того, куда камера сместилась за кадр (при движении A/D стороны инвертировались), поэтому играем 2D.
    public static void PlayAt(AudioClip clip, Vector3 pos, float volume, bool local = false)
    {
        var go = new GameObject("sfx_" + clip.name);
        go.transform.position = pos;
        var s = go.AddComponent<AudioSource>();
        s.clip = clip; s.volume = volume; s.spatialBlend = local ? 0f : 1f;
        s.minDistance = 2f; s.maxDistance = 30f; s.rolloffMode = AudioRolloffMode.Linear;
        s.Play();
        Object.Destroy(go, clip.length + 0.1f);
    }

    static float Noise(System.Random r) => (float)(r.NextDouble() * 2 - 1);

    static AudioClip Make(string name, float len, System.Func<float, System.Random, float> f)
    {
        int n = (int)(Rate * len);
        var d = new float[n];
        var rng = new System.Random(name.GetHashCode());
        for (int i = 0; i < n; i++) d[i] = Mathf.Clamp(f(i / (float)Rate, rng), -1f, 1f);
        var c = AudioClip.Create(name, n, 1, Rate, false);
        c.SetData(d, 0);
        return c;
    }
}
