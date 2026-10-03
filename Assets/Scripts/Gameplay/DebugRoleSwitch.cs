using UnityEngine;
using UnityEngine.InputSystem;

// Отладка без сети: F2 передаёт управление между прячущимся и охотником, F3 завершает подготовку.
// Уйдёт, когда появятся лобби и сеть (в игре одна роль на игрока).
public class DebugRoleSwitch : MonoBehaviour
{
    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.f3Key.wasPressedThisFrame && RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) RoundState.Instance.EndPrep();
        if (kb.f2Key.wasPressedThisFrame) Swap();
    }

    public static void Swap()
    {
        HiderPlayer hider = HiderPlayer.All.Find(h => h.IsAlive && h.cam != null);   // локальный слот: у манекена камеры нет
        HunterPlayer hunter = HunterPlayer.All.Count > 0 ? HunterPlayer.All[0] : null;
        if (hider == null || hunter == null) return;
        bool toHunter = hider.controlled;
        hider.SetControlled(!toHunter);
        hunter.SetControlled(toHunter);
    }
}
