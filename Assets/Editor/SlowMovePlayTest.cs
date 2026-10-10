using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// Проверка Left Alt (точная подстройка скорости) в Play, клавиши событиями Input System. Запуск: Play, затем через MCP
// System.Type.GetType("SlowMovePlayTest, Assembly-CSharp-Editor").GetMethod("Start").Invoke(null, null). Отчёт Temp/slowmove_test.txt.
public static class SlowMovePlayTest
{
    const string Report = "Temp/slowmove_test.txt";
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
        try { if (run == null || !run.MoveNext()) Finish(); }
        catch (System.Exception e) { L("EXCEPTION " + e); Finish(); }
    }

    static void Finish()
    {
        EditorApplication.update -= Tick;
        File.WriteAllText(Report, log.ToString());
        run = null;
    }

    static void Keys(params Key[] k) { InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(k)); }

    // Медиана горизонтальной скорости за окно кадров (только кадры на земле).
    static IEnumerator Measure(HiderPlayer h, float seconds, System.Action<float, float> done, params Key[] keys)
    {
        var samples = new List<float>();
        float t0 = Time.time;
        Vector3 prev = h.transform.position; float prevT = Time.time;
        yield return null;
        while (Time.time - t0 < seconds)
        {
            Keys(keys);
            yield return null;
            Vector3 p = h.transform.position; float dt = Time.time - prevT;
            if (dt > 0f) { Vector3 d = p - prev; d.y = 0f; samples.Add(d.magnitude / dt); }
            prev = p; prevT = Time.time;
        }
        Keys();
        samples.Sort();
        float med = samples.Count > 0 ? samples[samples.Count / 2] : -1f;
        done(med, samples.Count);
    }

    static IEnumerator Main()
    {
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        var h = HiderPlayer.All.Find(x => x.name == "Player");
        foreach (var o in HiderPlayer.All) if (o != h) o.SetControlled(false);
        if (RoundState.Instance.Phase == RoundPhase.Prep) { h.AutoPossess(); }
        for (float w = Time.time; Time.time - w < 0.5f;) yield return null;
        L($"Prop={(h.CurrentProp != null ? h.CurrentProp.name : "null")} pos={h.transform.position} walkSpeed={h.walkSpeed} slowMult={h.slowMoveMult}");

        Vector3 start = new Vector3(0f, 0.08f, -5.5f);
        // Вынесем на открытое место пола в зале (проход), смотрим по +Z.
        var cc = h.GetComponent<CharacterController>();
        cc.enabled = false; h.transform.position = start; cc.enabled = true;
        for (float w = Time.time; Time.time - w < 0.4f;) yield return null;

        float v = 0f, n = 0f;
        for (var e = Measure(h, 0.8f, (m, c) => { v = m; n = c; }, Key.W); e.MoveNext();) yield return null;
        L($"W: {v:F2} м/с (кадров {n}), ожидаем {h.walkSpeed:F2}, pos={h.transform.position}, grounded={h.Grounded}");
        float normal = v;

        cc.enabled = false; h.transform.position = start; cc.enabled = true;
        for (float w = Time.time; Time.time - w < 0.3f;) yield return null;
        Vector3 p0 = h.transform.position;
        for (var e = Measure(h, 0.8f, (m, c) => { v = m; n = c; }, Key.W, Key.LeftAlt); e.MoveNext();) yield return null;
        L($"W+LeftAlt: путь {(h.transform.position - p0).magnitude:F2} м за 0,8 с; {v:F2} м/с (кадров {n}), SlowMoving={h.SlowMoving}, ожидаем {h.walkSpeed * h.slowMoveMult:F2}, отношение {(normal > 0 ? v / normal : 0):F2}");

        // Стикмен/охотник: у HunterPlayer этой клавиши нет, в коде у него нет обработки Alt (проверка кодом: grep).
        // Alt при ускорении: Z должен перебить.
        h.transform.position = start;
        Physics.SyncTransforms();
        h.PressBoost();
        L($"Boost после PressBoost: {h.Boost}");
        for (var e = Measure(h, 0.8f, (m, c) => { v = m; n = c; }, Key.W, Key.LeftAlt); e.MoveNext();) yield return null;
        L($"Boost+W+LeftAlt: {v:F2} м/с, SlowMoving={h.SlowMoving}, ожидаем {h.walkSpeed * h.boostSpeedMult * h.slowMoveMult:F2} (множители перемножаются)");

        // Дождаться конца ускорения
        for (float w = Time.time; Time.time - w < 3.5f;) { Keys(); yield return null; }
        L($"После 3,5 с: Boost={h.Boost}");

        // Поворот Q при зажатом Alt
        float yaw0 = h.PropYaw;
        for (float w = Time.time; Time.time - w < 0.5f;) { Keys(Key.Q, Key.LeftAlt); yield return null; }
        Keys();
        float dyaw = Mathf.DeltaAngle(yaw0, h.PropYaw);
        L($"Q+Alt 0,5 с: поворот {dyaw:F1}° (ожидаем ~-{h.propTurnSpeed * 0.5f:F0}°)");

        // Прыжок без Alt (для сравнения)
        for (float w = Time.time; Time.time - w < 1.5f;) { Keys(); yield return null; }
        {
            float yb = h.transform.position.y, mb = yb, tb = Time.time;
            Keys(Key.Space); yield return null;
            while (Time.time - tb < 1.5f) { Keys(); mb = Mathf.Max(mb, h.transform.position.y); yield return null; }
            L($"Space без Alt: высота прыжка {mb - yb:F2} м");
        }
        for (float w = Time.time; Time.time - w < 1.0f;) { Keys(); yield return null; }
        // Прыжок при Alt
        yield return null;
        float y0 = h.transform.position.y, maxY = y0;
        float tj = Time.time;
        Keys(Key.Space, Key.LeftAlt);
        yield return null;
        while (Time.time - tj < 1.5f) { Keys(Key.LeftAlt); maxY = Mathf.Max(maxY, h.transform.position.y); yield return null; }
        Keys();
        L($"Space+Alt: высота прыжка {maxY - y0:F2} м (обычная {h.JumpHeight:F2}, ожидаем {h.JumpHeight * h.slowJumpMult:F2})");

        // Точность: шаг на 0.5 с с нажатия-отпускания (проскальзывание после отпускания: у CC нет инерции)
        h.transform.position = start;
        Physics.SyncTransforms();
        yield return null;
        Vector3 a = h.transform.position;
        for (int i = 0; i < 3; i++) { Keys(Key.W, Key.LeftAlt); yield return null; }
        Keys(); yield return null; yield return null;
        L($"Три кадра Alt+W и стоп: сдвиг {(h.transform.position - a).magnitude * 1000f:F0} мм, после отпускания не едет");
    }
}
