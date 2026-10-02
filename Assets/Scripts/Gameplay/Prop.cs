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

    // Освобождённый предмет возвращается на своё место и в пул свободных.
    public void ReleaseToHome()
    {
        SetHighlight(false, null);
        transform.SetParent(homeParent, true);
        transform.SetPositionAndRotation(homePos, homeRot);
        occupant = null;
    }
}
