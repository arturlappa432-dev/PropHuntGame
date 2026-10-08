using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

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

    // Вписанный радиус основания: наименьшая полуось меша по X/Z. Круглая капсула CharacterController не может повторить
    // прямоугольный предмет, поэтому берётся вписанная, а остальной контакт считает HiderPlayer.ResolveShapeContacts по настоящему коллайдеру.
    public float InnerRadius
    {
        get
        {
            var mf = GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return footRadius;
            var e = mf.sharedMesh.bounds.extents;
            var s = mf.transform.localScale;
            return Mathf.Min(e.x * Mathf.Abs(s.x), e.z * Mathf.Abs(s.z));
        }
    }

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
        if (OnShelf) MakeShelfObstacle();
    }

    // Предмет стоит на полке (низ выше пола)?
    public bool OnShelf => transform.position.y - height * 0.5f > 0.15f;

    // Свободный предмет на полке вырезает себя из навмеша полок (HiderSmall, docs/systems/bots.md): полка 0,275 м глубиной,
    // мимо предмета не пройти — путь и место прыжка на уровень должны его обходить. Вселились — вырез снимается.
    NavMeshObstacle obstacle;
    void MakeShelfObstacle()
    {
        if (Colliders.Length == 0) return;
        var b = Colliders[0].bounds;
        obstacle = gameObject.AddComponent<NavMeshObstacle>();
        obstacle.shape = NavMeshObstacleShape.Box;
        var ls = transform.lossyScale;
        obstacle.center = transform.InverseTransformPoint(b.center);
        obstacle.size = new Vector3(b.size.x / Mathf.Abs(ls.x), b.size.y / Mathf.Abs(ls.y), b.size.z / Mathf.Abs(ls.z));
        obstacle.carving = true;
        obstacle.carveOnlyStationary = true;
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
        if (obstacle != null) obstacle.enabled = false;
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
        if (obstacle != null) obstacle.enabled = true;
    }
}
