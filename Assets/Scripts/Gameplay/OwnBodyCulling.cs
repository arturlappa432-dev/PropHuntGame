using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Скрывает собственное тело игрока только от его камеры (movement-and-camera.md): перед рендером конкретной камеры
// у рендереров владельца Renderer.enabled = false, после рендера возвращается. Все остальные камеры и чужие тела не затронуты
// (общий слой с Culling Mask скрывал бы ВСЕ тела на слое от ВСЕХ камер). Слой OwnBody остаётся только для матрицы физики.
public interface IOwnBodyViewer
{
    Camera ViewCamera { get; }     // камера владельца (null, если не управляется)
    bool HideOwnBody { get; }      // true, если сейчас своё тело надо скрыть (первое лицо)
    void CollectOwnRenderers(List<Renderer> buffer);
}

public static class OwnBodyCulling
{
    static readonly List<IOwnBodyViewer> viewers = new List<IOwnBodyViewer>();
    static readonly List<Renderer> scratch = new List<Renderer>();
    static readonly List<Renderer> disabled = new List<Renderer>();
    static bool subscribed;

    public static void Register(IOwnBodyViewer v)
    {
        if (!viewers.Contains(v)) viewers.Add(v);
        if (subscribed) return;
        subscribed = true;
        RenderPipelineManager.beginCameraRendering += OnBegin;
        RenderPipelineManager.endCameraRendering += OnEnd;
    }

    public static void Unregister(IOwnBodyViewer v) { viewers.Remove(v); }

    static void OnBegin(ScriptableRenderContext ctx, Camera cam)
    {
        disabled.Clear();
        foreach (var v in viewers)
        {
            if (v.ViewCamera != cam || !v.HideOwnBody) continue;
            scratch.Clear();
            v.CollectOwnRenderers(scratch);
            foreach (var r in scratch)
                if (r != null && r.enabled) { r.enabled = false; disabled.Add(r); }
        }
    }

    static void OnEnd(ScriptableRenderContext ctx, Camera cam)
    {
        foreach (var r in disabled) if (r != null) r.enabled = true;
        disabled.Clear();
    }
}
