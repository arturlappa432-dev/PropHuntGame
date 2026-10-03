using UnityEngine;

// HP-бар над прячущимся при попадании (hunter-combat.md): зелёное = текущее HP, красное = оставшееся до максимума.
// Мировые спрайты (стены его закрывают), поворот к активной камере; через пару секунд после последнего попадания гаснет.
public class HpBar : MonoBehaviour
{
    public float showDuration = 2.5f;   // предложено ~2-3 с, калибровать
    public float fadeDuration = 0.5f;
    public float width = 0.6f, height = 0.07f, lift = 0.25f;

    HiderPlayer owner;
    SpriteRenderer border, red, green;
    float visibleUntil = -1f;
    static Sprite pixel, pixelLeft;

    public static HpBar Attach(HiderPlayer owner)
    {
        var go = new GameObject("HpBar");
        var bar = go.AddComponent<HpBar>();
        bar.owner = owner;
        bar.border = Make(go.transform, "Border", Color.black, 0, false);
        bar.red = Make(go.transform, "Red", new Color(0.85f, 0.12f, 0.1f), 1, true);
        bar.green = Make(go.transform, "Green", new Color(0.2f, 0.85f, 0.2f), 2, true);
        bar.SetAlpha(0f);
        return bar;
    }

    static SpriteRenderer Make(Transform parent, string name, Color c, int order, bool leftPivot)
    {
        if (pixel == null)
        {
            var tex = new Texture2D(1, 1) { filterMode = FilterMode.Point };
            tex.SetPixel(0, 0, Color.white); tex.Apply();
            pixel = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            pixelLeft = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0f, 0.5f), 1f);
        }
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = leftPivot ? pixelLeft : pixel;
        sr.color = c;
        sr.sortingOrder = order;
        return sr;
    }

    public float Fraction { get; private set; }
    public bool Visible => Time.time < visibleUntil;

    public void Show(int hp, int maxHp)
    {
        Fraction = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
        visibleUntil = Time.time + showDuration;   // каждое попадание продлевает таймер
    }

    void SetAlpha(float a)
    {
        foreach (var sr in new[] { border, red, green })
        {
            var c = sr.color; c.a = a; sr.color = c;
        }
    }

    void LateUpdate()
    {
        if (owner == null) { Destroy(gameObject); return; }
        float remain = visibleUntil - Time.time;
        // себе бар не показываем: своё HP видно в HUD
        float a = owner.controlled || owner.Caught ? 0f : Mathf.Clamp01(remain / fadeDuration);
        SetAlpha(a);
        if (a <= 0f) return;

        transform.position = owner.BodyCenter + Vector3.up * (owner.BodyHeight * 0.5f + lift);
        var cam = Camera.main;
        if (cam != null) transform.rotation = cam.transform.rotation;

        border.transform.localScale = new Vector3(width + 0.03f, height + 0.03f, 1f);
        red.transform.localPosition = new Vector3(-width * 0.5f, 0f, -0.001f);
        red.transform.localScale = new Vector3(width, height, 1f);
        green.transform.localPosition = new Vector3(-width * 0.5f, 0f, -0.002f);
        green.transform.localScale = new Vector3(width * Fraction, height, 1f);
    }
}
