using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

// Проверка зазора «предмет-тело игрока <-> сосед» в Play. Запуск: Play, затем через MCP
// System.Type.GetType("GapPlayTest, Assembly-CSharp-Editor").GetMethod("Start").Invoke(null, null). Отчёт Temp/gap_test.txt.
public static class GapPlayTest
{
    const string Report = "Temp/gap_test.txt";
    static IEnumerator run;
    static StringBuilder log = new StringBuilder();
    static void L(string s) { log.AppendLine(s); }

    public static void Start()
    {
        log.Clear();
        run = Main();
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        try { if (run == null || !Step(run)) Finish(); }
        catch (System.Exception e) { L("EXCEPTION " + e); Finish(); }
    }

    static bool Step(IEnumerator it)
    {
        if (it.Current is IEnumerator inner) { if (Step(inner)) return true; }
        return it.MoveNext();
    }

    static void Finish()
    {
        EditorApplication.update -= Tick;
        File.WriteAllText(Report, log.ToString());
        run = null;
    }

    static IEnumerator Secs(float t) { float t0 = Time.time; while (Time.time - t0 < t) yield return null; }
    static IEnumerator Frames(int n) { int f0 = Time.frameCount; while (Time.frameCount - f0 < n) yield return null; }

    static Prop Sample(string name) => Prop.All.First(p => p.name == name && p.IsFree);

    static void Morph(HiderPlayer h, Prop s) { typeof(HiderPlayer).GetMethod("Morph", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(h, new object[] { s }); }
    static void SetYaw(HiderPlayer h, float y)
    {
        typeof(HiderPlayer).GetField("propYaw", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(h, y);
        h.CurrentProp.transform.localRotation = Quaternion.Euler(0, y, 0);
    }

    static void Teleport(HiderPlayer h, Vector3 p)
    {
        var cc = h.GetComponent<CharacterController>();
        cc.enabled = false; h.transform.position = p; cc.enabled = true;
        Physics.SyncTransforms();
    }

    // Минимальное расстояние от вершин меша тела до коллайдера соседа (для выпуклых равно зазору между поверхностями).
    static float Gap(HiderPlayer h, Collider other)
    {
        // симметрично: вершины моего меша -> коллайдер соседа и вершины соседа -> мои коллайдеры
        var mf = h.CurrentProp.GetComponentInChildren<MeshFilter>();
        float best = float.MaxValue;
        foreach (var v in mf.sharedMesh.vertices)
        {
            Vector3 w = mf.transform.TransformPoint(v);
            best = Mathf.Min(best, Vector3.Distance(w, other.ClosestPoint(w)));
        }
        var omf = other.GetComponent<MeshFilter>();
        if (omf != null)
            foreach (var v in omf.sharedMesh.vertices)
            {
                Vector3 w = omf.transform.TransformPoint(v);
                foreach (var c in h.CurrentProp.Colliders) best = Mathf.Min(best, Vector3.Distance(w, c.ClosestPoint(w)));
            }
        return best;
    }

    // > 0: тела пересекаются на столько метров (ComputePenetration по настоящим коллайдерам)
    static float Overlap(HiderPlayer h, Collider other)
    {
        float worst = 0f;
        foreach (var c in h.CurrentProp.Colliders)
            if (Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, other, other.transform.position, other.transform.rotation, out _, out float d)) worst = Mathf.Max(worst, d);
        return worst;
    }

    static bool FreeSpot(Vector3 p)
    {
        foreach (var c in Physics.OverlapBox(p + Vector3.up * 1f, new Vector3(2.2f, 1f, 2.2f), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            if (c.bounds.max.y > 0.05f && c.GetComponentInParent<HiderPlayer>() == null) return false;
        return Physics.Raycast(p + Vector3.up, Vector3.down, out var hit, 2f) && Mathf.Abs(hit.point.y) < 0.02f;
    }

    static IEnumerator Main()
    {
        foreach (var b in Object.FindObjectsByType<HiderBot>(FindObjectsSortMode.None)) b.enabled = false;
        foreach (var h0 in HiderPlayer.All) if (h0.botInput != null) h0.botInput.move = Vector2.zero;
        var me = HiderPlayer.All.Find(h => !h.controlled && h.botInput == null);
        if (me == null) { L("нет манекена"); yield break; }
        me.botInput = new BotInput();
        var rs = RoundState.Instance; rs.EndPrep();
        yield return Frames(5);

        Vector3 spot = default; bool found = false;
        for (float x = -9; x <= 9 && !found; x += 0.5f)
            for (float z = -6; z <= 6 && !found; z += 0.5f)
                if (FreeSpot(new Vector3(x, 0f, z))) { spot = new Vector3(x, 0f, z); found = true; }
        L($"свободное место: {spot} найдено={found}");
        if (!found) yield break;

        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = "GapBlock"; block.transform.localScale = new Vector3(0.4f, 1.2f, 0.4f);
        block.transform.position = spot + new Vector3(1.6f, 0.6f, 0f);
        var blockCol = block.GetComponent<Collider>();
        // стена-плоскость для проверки контакта с геометрией уровня
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "GapWall"; wall.transform.localScale = new Vector3(4f, 2f, 0.2f);
        wall.transform.position = spot + new Vector3(0f, 1f, -1.9f);
        var wallCol = wall.GetComponent<Collider>();
        // свободный предмет-сосед (Box) на полу
        var neighbor = Sample("Box");
        neighbor.transform.position = spot + new Vector3(-1.6f, 0.1f, 1.2f);
        neighbor.transform.rotation = Quaternion.identity;
        var nCol = neighbor.Colliders[0];

        foreach (var model in new[] { "Snack", "Can", "Box", "Basket", "Bin", "Machine", "Table" })
        {
            Prop s; try { s = Sample(model); } catch { L($"{model}: нет образца"); continue; }
            foreach (float yawDeg in new[] { 0f, 37f, 90f })
            {
                Morph(me, s);
                SetYaw(me, yawDeg);
                me.botInput.yaw = 0f;
                var p = me.CurrentProp;
                string head = $"{model,-8} tier={p.tier} yaw={yawDeg,3} ccR={me.GetComponent<CharacterController>().radius:F3} innerR={p.InnerRadius:F3} footR={p.footRadius:F3}";

                // 1) к блоку (статика) вдоль +x
                Teleport(me, spot + new Vector3(0f, 0f, 0f)); me.botInput.move = new Vector2(1, 0);
                yield return Secs(1.2f);
                me.botInput.move = Vector2.zero; yield return Frames(3);
                float g1 = Gap(me, blockCol), o1 = Overlap(me, blockCol);
                Vector3 pos1 = me.transform.position; yield return Frames(30);
                float jit1 = (me.transform.position - pos1).magnitude;

                // 2) к стене-плоскости вдоль -z
                Teleport(me, spot); me.botInput.move = new Vector2(0, -1);
                yield return Secs(1.2f);
                me.botInput.move = Vector2.zero; yield return Frames(3);
                float g2 = Gap(me, wallCol), o2 = Overlap(me, wallCol);

                // 3) к свободному предмету-соседу (Box) вдоль -x, вход со смещением по z
                Teleport(me, spot + new Vector3(0f, 0f, 1.2f)); me.botInput.move = new Vector2(-1, 0);
                yield return Secs(1.2f);
                me.botInput.move = Vector2.zero; yield return Frames(3);
                float g3 = Gap(me, nCol), o3 = Overlap(me, nCol);

                // 4) повернуть предмет вплотную к блоку: пересечения быть не должно
                Teleport(me, spot); me.botInput.move = new Vector2(1, 0); yield return Secs(1.2f);
                me.botInput.move = Vector2.zero; me.botInput.rotate = 1f; yield return Frames(70); me.botInput.rotate = 0f; yield return Frames(3);
                float o4 = Overlap(me, blockCol);

                // 5) держим клавишу в сторону блока: положение по кадрам не должно дрожать
                Teleport(me, spot); me.botInput.move = new Vector2(1, 0); yield return Secs(1.2f);
                { var near = Physics.OverlapBox(me.CurrentProp.Colliders[0].bounds.center, me.CurrentProp.Colliders[0].bounds.extents + Vector3.one * 0.02f, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                  L($"   [{model} yaw {yawDeg}] после 1.2 с: корень {me.transform.position:F3} propYaw-ось {me.CurrentProp.transform.eulerAngles.y:F1} рядом: " + string.Join(", ", near.Select(c => c.name + "(" + c.gameObject.layer + ")"))); }
                var xs = new System.Collections.Generic.List<float>();
                for (int k = 0; k < 60; k++) { xs.Add(me.transform.position.x * 1000f); yield return null; }
                me.botInput.move = Vector2.zero;
                float mn = xs.Skip(10).Min(), mx = xs.Skip(10).Max(); int flips = 0;
                for (int k = 11; k < xs.Count - 1; k++) if ((xs[k] - xs[k - 1]) * (xs[k + 1] - xs[k]) < -1e-6f) flips++;
                float hold = Gap(me, blockCol);
                string hj = $"держим: размах {mx - mn:F2} мм, смен направления {flips}, зазор {hold * 1000:F1} мм";
                float y = me.transform.position.y;
                L($"{head} | блок: зазор {g1 * 1000:F1} мм пересеч {o1 * 1000:F1} дрожь {jit1 * 1000:F2} мм | стена: {g2 * 1000:F1}/{o2 * 1000:F1} | предмет: {g3 * 1000:F1}/{o3 * 1000:F1} | после Q/E пересеч {o4 * 1000:F1} мм | {hj} | y={y:F3}");
            }
        }
        // тело к телу: второй вселённый (бот с выключенным ИИ) стоит, первый упирается в него
        var other0 = HiderPlayer.All.Find(h => h != me && h.CurrentProp != null);
        if (other0 != null)
        {
            block.SetActive(false); Physics.SyncTransforms();
            if (other0.botInput != null) other0.botInput.move = Vector2.zero;
            foreach (var m2 in new[] { "Snack", "Box", "Basket", "Machine" })
            {
                Morph(me, Sample(m2)); SetYaw(me, 0f);
                Teleport(other0, spot + new Vector3(1.6f, 0f, 0f));
                Vector3 oPos = other0.transform.position;
                Teleport(me, spot); me.botInput.move = new Vector2(1, 0);
                { var tr = new System.Text.StringBuilder(); for (int k = 0; k < 40; k++) { tr.Append($"({me.transform.position.x:F2},{me.transform.position.z:F2}) "); yield return null; } L("   трасса " + tr); }
                yield return Secs(1.0f);
                me.botInput.move = Vector2.zero; yield return Frames(3);
                var oc = other0.CurrentProp.Colliders[0];
                L($"тело-в-тело: я={m2} другой={other0.CurrentProp.ModelId} зазор {Gap(me, oc) * 1000:F1} мм пересеч {Overlap(me, oc) * 1000:F1} мм, другой сдвинут на {(other0.transform.position - oPos).magnitude * 1000:F1} мм; я={me.transform.position:F3} другой={other0.transform.position:F3} rotY другого={other0.CurrentProp.transform.eulerAngles.y:F0}");
            }
        }
        // второй вселённый: два тела друг к другу
        var other = HiderPlayer.All.Find(h => h != me && h.CurrentProp != null);
        L(other != null ? "есть второй игрок (ботов отключил, не двигал)" : "второго игрока нет");
    }
}
