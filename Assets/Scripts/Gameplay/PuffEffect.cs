using UnityEngine;

// «Пуфф» + быстро тающее облачко пыли; размер зависит от размера предмета.
public static class PuffEffect
{
    public static void Spawn(Vector3 pos, float size, Material mat)
    {
        var go = new GameObject("Puff");
        go.transform.position = pos;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.duration = 0.5f; main.loop = false; main.playOnAwake = false;
        main.startLifetime = 0.6f;
        main.startSpeed = Mathf.Lerp(0.3f, 1.2f, Mathf.Clamp01(size));
        main.startSize = new ParticleSystem.MinMaxCurve(0.6f * size + 0.1f, 1.2f * size + 0.2f);
        main.startColor = new Color(0.92f, 0.92f, 0.92f, 0.9f);
        var em = ps.emission; em.rateOverTime = 0;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 12) });
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = Mathf.Max(0.05f, size * 0.3f);
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                  new[] { new GradientAlphaKey(0.9f, 0), new GradientAlphaKey(0f, 1) });
        col.color = g;
        var sz = ps.sizeOverLifetime; sz.enabled = true;
        sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0, 0.5f, 1, 1.4f));
        go.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
        ps.Play();
        Object.Destroy(go, 1.5f);
    }
}
