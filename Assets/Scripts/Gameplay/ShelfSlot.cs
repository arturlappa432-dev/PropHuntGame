using System.Collections.Generic;
using UnityEngine;

// Одна сторона одного уровня стеллажа (основание или полка), куда может забраться мелкий предмет.
// Создаётся Editor-скриптом ShelfNavBuilder вместе с рядом NavMeshLink (тип перехода Jump) между полом прохода и этим уровнем.
// Боты берут отсюда кандидатов укрытия на полках; сам путь и переход строит NavMesh (агент HiderSmall).
public class ShelfSlot : MonoBehaviour
{
    public static readonly List<ShelfSlot> All = new List<ShelfSlot>();

    public Vector3 a, b;          // концы средней линии уровня (мир, на верхней поверхности полки)
    public Vector3 normal;        // горизонталь наружу, в проход
    public float top;             // высота поверхности
    public float clearance;       // свободная высота над поверхностью (до полки выше; у верхней полки большая)
    public float depth;           // глубина стороны (от задней панели до кромки)
    public int level;             // 0 = основание, 1..4 = полки

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public Vector3 Point(float u) => Vector3.Lerp(a, b, u);

    // Параметр u ближайшей точки средней линии к p (по горизонтали).
    public float Project(Vector3 p)
    {
        Vector3 ab = b - a; ab.y = 0f;
        Vector3 ap = p - a; ap.y = 0f;
        return ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(ap, ab) / ab.sqrMagnitude);
    }

    // Лежит ли точка (низ предмета) на этом уровне этой стороны.
    public bool Contains(Vector3 foot, float tol = 0.06f)
    {
        if (Mathf.Abs(foot.y - top) > tol) return false;
        Vector3 c = Point(Project(foot));
        Vector3 d = foot - c; d.y = 0f;
        return d.magnitude <= depth * 0.5f + 0.02f;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(a, b);
        Gizmos.DrawLine((a + b) * 0.5f, (a + b) * 0.5f + normal * 0.4f);
    }
}
