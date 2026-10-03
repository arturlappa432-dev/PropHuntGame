using System.Collections.Generic;
using UnityEngine;

public enum PropTier { Tiny = 0, Small = 1, Medium = 2, Large = 3 }

// Вселяемый предмет. Состояние занятости ведёт сервер (PossessionAuthority); клиент только рисует подсветку.
public class Prop : MonoBehaviour
{
    public static readonly List<Prop> All = new List<Prop>();
    // HP по тирам, docs/balance.md
    static readonly int[] TierHp = { 20, 100, 140, 220 };

    public PropTier tier;
    // Идентификатор модели (тип предмета). Пусто -> имя объекта (в сцене имя = имя определения: Can, Box, Bin...).
    // Копируется при смене облика: нужен, чтобы отличить «ту же модель» от «того же тира».
    public string modelId;
    public string ModelId => string.IsNullOrEmpty(modelId) ? name : modelId;
    public float height = 0.3f;       // высота в метрах (для капсулы игрока)
    public float footRadius = 0.15f;  // радиус основания

    public HiderPlayer occupant;
    public bool IsFree => occupant == null;
    public int MaxHp => TierHp[(int)tier];

    Transform homeParent;
    Vector3 homePos;
    Quaternion homeRot;
    Renderer rend;
    Material[] plainMats;
    bool lit;

    public Collider[] Colliders { get; private set; }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Awake()
    {
        homeParent = transform.parent;
        homePos = transform.position;
        homeRot = transform.rotation;
        rend = GetComponentInChildren<Renderer>();
        plainMats = rend.sharedMaterials;
        Colliders = GetComponentsInChildren<Collider>();
    }

    // Подсветка видна только локальному игроку: материал добавляется на локальный рендерер, по сети не передаётся.
    public void SetHighlight(bool on, Material outline)
    {
        if (on == lit || rend == null) return;
        lit = on;
        if (on)
        {
            var litMats = new Material[plainMats.Length + 1];
            plainMats.CopyTo(litMats, 0);
            litMats[plainMats.Length] = outline;
            rend.sharedMaterials = litMats;
        }
        else
        {
            rend.sharedMaterials = plainMats;
        }
    }

    public void AttachTo(Transform body)
    {
        transform.SetParent(body, false);
        transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
        transform.localRotation = Quaternion.identity;
    }

    // Слой для собственного тела игрока: камера от первого лица его не рисует.
    public void SetLayerRecursive(int layer)
    {
        if (layer < 0) return;
        foreach (var t in GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    // Смена облика во время охоты: тело игрока копирует модель, тир и коллайдер образца. Образец не меняется.
    public void CopyAppearanceFrom(Prop src)
    {
        tier = src.tier;
        modelId = src.ModelId;
        height = src.height;
        footRadius = src.footRadius;

        var srcFilter = src.GetComponentInChildren<MeshFilter>();
        var myFilter = GetComponentInChildren<MeshFilter>();
        if (srcFilter != null && myFilter != null) myFilter.sharedMesh = srcFilter.sharedMesh;
        plainMats = src.plainMats;   // подсветка на образце в этот момент уже снята
        rend.sharedMaterials = plainMats;
        lit = false;
        transform.localScale = src.transform.localScale;

        foreach (var c in Colliders) if (c != null) Destroy(c);
        Colliders = CopyColliders(src, gameObject);
        transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
        transform.localRotation = Quaternion.identity;
    }

    static Collider[] CopyColliders(Prop src, GameObject dst)
    {
        var list = new List<Collider>();
        foreach (var c in src.Colliders)
        {
            Collider n = null;
            switch (c)
            {
                case BoxCollider b:
                    var nb = dst.AddComponent<BoxCollider>(); nb.center = b.center; nb.size = b.size; n = nb; break;
                case CapsuleCollider cap:
                    var nc = dst.AddComponent<CapsuleCollider>();
                    nc.center = cap.center; nc.radius = cap.radius; nc.height = cap.height; nc.direction = cap.direction; n = nc; break;
                case SphereCollider s:
                    var ns = dst.AddComponent<SphereCollider>(); ns.center = s.center; ns.radius = s.radius; n = ns; break;
                case MeshCollider m:
                    var nm = dst.AddComponent<MeshCollider>(); nm.sharedMesh = m.sharedMesh; nm.convex = m.convex; n = nm; break;
            }
            if (n != null) list.Add(n);
        }
        return list.ToArray();
    }

    // Освобождённый предмет возвращается на своё место и в пул свободных.
    public void ReleaseToHome()
    {
        SetHighlight(false, null);
        transform.SetParent(homeParent, true);
        transform.SetPositionAndRotation(homePos, homeRot);
        occupant = null;
    }
}
