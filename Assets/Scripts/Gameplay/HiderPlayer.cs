using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

// Локальный прячущийся: стикмен до вселения, потом предмет. Ввод и камера клиентские,
// решение «можно ли вселиться» принимает PossessionAuthority.
[RequireComponent(typeof(CharacterController))]
public class HiderPlayer : MonoBehaviour
{
    public Camera cam;
    public Transform stickman;
    public Material outlineMaterial;
    public Material puffMaterial;
    public float walkSpeed = 4f;      // скорость движения в документах открыта, значение временное
    public float gravity = -20f;
    public float suckDuration = 0.35f;
    public float mouseSensitivity = 0.1f;
    public float propTurnSpeed = 100f;   // °/с, предложено 90-120, калибровать

    public Prop CurrentProp { get; private set; }
    public Prop Target { get; private set; }
    public int Hp { get; private set; }
    public int MaxHp { get; private set; }
    public float NextRepossessTime { get; private set; }
    public string Toast { get; private set; } = "";

    public Vector3 EyePosition => transform.position + Vector3.up * EyeHeight;
    float EyeHeight => CurrentProp == null ? 1.6f : Mathf.Max(0.25f, CurrentProp.height * 0.9f);

    static int OwnBodyLayer => LayerMask.NameToLayer("OwnBody");

    CharacterController cc;
    float yaw, pitch, vy, camDist, toastUntil, propYaw;
    bool thirdPerson, busy;

    void Awake() { cc = GetComponent<CharacterController>(); }

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        yaw = transform.eulerAngles.y;
    }

    void Update()
    {
        if (Toast.Length > 0 && Time.time > toastUntil) Toast = "";
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 d = mouse.delta.ReadValue() * mouseSensitivity;
            yaw += d.x;
            pitch = Mathf.Clamp(pitch - d.y, -80f, 80f);
        }
        if (kb != null && kb.vKey.wasPressedThisFrame) thirdPerson = !thirdPerson;

        if (!busy)
        {
            Move(kb);
            UpdateTarget();
            if (kb != null && kb.eKey.wasPressedThisFrame) PressPossess();
            RotateProp(kb);
        }
    }

    // Q влево, E вправо (E только без подсвеченной цели: у превращения приоритет). Только yaw, отдельно от камеры.
    void RotateProp(Keyboard kb)
    {
        if (CurrentProp == null) return;
        float dir = 0f;
        if (kb != null)
        {
            if (kb.qKey.isPressed) dir -= 1f;
            if (kb.eKey.isPressed && Target == null) dir += 1f;
        }
        propYaw += dir * propTurnSpeed * Time.deltaTime;
        CurrentProp.transform.localRotation = Quaternion.Euler(0f, propYaw, 0f);
    }

    void Move(Keyboard kb)
    {
        // Тело-предмет не крутится за камерой: у него своя ориентация (propYaw), поворачивается только стикмен.
        Quaternion look = Quaternion.Euler(0, yaw, 0);
        if (CurrentProp == null) transform.rotation = look;
        Vector3 input = Vector3.zero;
        if (kb != null)
        {
            if (kb.wKey.isPressed) input.z += 1;
            if (kb.sKey.isPressed) input.z -= 1;
            if (kb.dKey.isPressed) input.x += 1;
            if (kb.aKey.isPressed) input.x -= 1;
        }
        Vector3 move = look * input.normalized * walkSpeed;
        vy = cc.isGrounded ? -1f : vy + gravity * Time.deltaTime;
        move.y = vy;
        cc.Move(move * Time.deltaTime);
    }

    // Прицел по центру экрана: первое непустое попадание луча; подходящий свободный предмет в пределах дистанции подсвечивается.
    void UpdateTarget()
    {
        Prop found = null;
        var auth = PossessionAuthority.Instance;
        var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 30f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (h.collider.GetComponentInParent<HiderPlayer>() == this) continue;
            var p = h.collider.GetComponentInParent<Prop>();
            if (p != null && (CurrentProp != null || p.IsFree) && auth != null &&
                Vector3.Distance(EyePosition, h.point) <= auth.pickDistance) found = p;
            break;   // первое чужое попадание заслоняет всё, что за ним
        }
        if (found != Target)
        {
            if (Target != null) Target.SetHighlight(false, outlineMaterial);
            Target = found;
            if (Target != null) Target.SetHighlight(true, outlineMaterial);
        }
    }

    // То, что делает клавиша E.
    public void PressPossess()
    {
        if (busy) return;
        if (Target == null) { ShowToast("нет подходящего предмета"); return; }
        var prop = Target;
        if (CurrentProp != null)
        {
            // Смена облика: остаёмся на месте и копируем вид образца, образец не резервируем и не трогаем.
            if (!PossessionAuthority.Instance.CheckMorph(this, prop, out var why)) { ShowToast(why); return; }
            prop.SetHighlight(false, outlineMaterial);
            Target = null;
            Morph(prop);
            return;
        }
        if (!PossessionAuthority.Instance.TryRequest(this, prop, out var mode, out var reason))
        {
            ShowToast(reason);
            return;
        }
        prop.SetHighlight(false, outlineMaterial);
        Target = null;
        if (mode == PossessMode.Suck) StartCoroutine(SuckRoutine(prop));
        else Complete(prop, PossessMode.Puff);
    }

    void Morph(Prop sample)
    {
        int oldMax = MaxHp, oldHp = Hp;
        PropTier oldTier = CurrentProp.tier;
        CurrentProp.CopyAppearanceFrom(sample);
        ApplyBodyShape(CurrentProp);
        MaxHp = CurrentProp.MaxHp;
        Hp = HpConversion.Convert(oldHp, oldMax, MaxHp, oldTier, CurrentProp.tier);
        NextRepossessTime = Time.time + PossessionAuthority.Instance.repossessCooldown;
        float size = Mathf.Max(CurrentProp.height, CurrentProp.footRadius * 2f);
        PuffEffect.Spawn(transform.position + Vector3.up * (CurrentProp.height * 0.5f), size, puffMaterial);
        camDist = float.NaN;
    }

    void ApplyBodyShape(Prop prop)
    {
        cc.enabled = false;
        float radius = Mathf.Clamp(prop.footRadius, 0.1f, 0.5f);
        cc.radius = radius;
        cc.height = Mathf.Max(prop.height, radius * 2f);
        cc.center = new Vector3(0, cc.height * 0.5f, 0);
        cc.stepOffset = Mathf.Min(0.3f, cc.height * 0.5f);   // иначе Unity ругается и отключает контроллер у мелких предметов
        cc.enabled = true;
        foreach (var c in prop.Colliders) Physics.IgnoreCollision(cc, c, true);
    }

    // Автопревращение опоздавшего: ближайший свободный предмет, мгновенно, «пуфф», камера без плавности.
    public void AutoPossess()
    {
        var prop = PossessionAuthority.Instance.ReserveNearestFree(this);
        if (prop != null) Complete(prop, PossessMode.Puff);
    }

    IEnumerator SuckRoutine(Prop prop)
    {
        busy = true;
        cc.enabled = false;
        Vector3 from = transform.position, to = FootPos(prop);
        Vector3 scale = stickman.localScale;
        for (float t = 0; t < suckDuration; t += Time.deltaTime)
        {
            float k = t / suckDuration;
            transform.position = Vector3.Lerp(from, to, k * k);
            stickman.localScale = scale * (1f - k);
            yield return null;
        }
        stickman.localScale = scale;
        Complete(prop, PossessMode.Suck);
        busy = false;
    }

    static Vector3 FootPos(Prop p) => p.transform.position - Vector3.up * (p.height * 0.5f);

    // Первое вселение (старт подготовки или автовселение): игрок переходит к зарезервированному экземпляру.
    void Complete(Prop prop, PossessMode mode)
    {
        Vector3 foot = FootPos(prop);
        cc.enabled = false;
        transform.position = foot;
        transform.rotation = Quaternion.identity;
        propYaw = 0f;
        prop.AttachTo(transform);
        prop.SetLayerRecursive(OwnBodyLayer);
        ApplyBodyShape(prop);
        stickman.gameObject.SetActive(false);
        vy = 0f;

        MaxHp = prop.MaxHp;
        Hp = MaxHp;
        CurrentProp = prop;
        NextRepossessTime = Time.time + PossessionAuthority.Instance.repossessCooldown;   // кулдаун первой смены на охоте отсчитывается отсюда

        if (mode == PossessMode.Puff)
        {
            float size = Mathf.Max(prop.height, prop.footRadius * 2f);
            PuffEffect.Spawn(prop.transform.position, size, puffMaterial);
            camDist = float.NaN;   // мгновенный переход камеры, без сглаживания
        }
    }

    void LateUpdate()
    {
        Vector3 eye = EyePosition;
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0);
        float want = thirdPerson ? 2f + (CurrentProp == null ? 1f : CurrentProp.height * 1.5f) : 0f;
        camDist = float.IsNaN(camDist) ? want : Mathf.Lerp(camDist, want, 1f - Mathf.Exp(-14f * Time.deltaTime));
        cam.transform.SetPositionAndRotation(eye - rot * Vector3.forward * camDist, rot);
        // от первого лица собственное тело не рисуется, от третьего (V) рисуется
        int own = OwnBodyLayer;
        if (own >= 0) cam.cullingMask = thirdPerson ? ~0 : ~(1 << own);
    }

    void ShowToast(string s) { Toast = s; toastUntil = Time.time + 1.5f; }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
        var round = RoundState.Instance;
        string phase = round == null ? "" : round.Phase == RoundPhase.Prep ? $"Подготовка: {round.PrepRemaining:0} с" : "Охота";
        float cd = Mathf.Max(0, NextRepossessTime - Time.time);
        string cdText = CurrentProp == null ? "" : cd > 0 ? $"   Смена облика: {cd:0.0} с" : "   Смена облика: готово";
        GUI.Label(new Rect(12, 8, 700, 28), $"{phase}   HP: {Hp}/{MaxHp}{cdText}", style);
        if (Target != null) GUI.Label(new Rect(Screen.width / 2f - 60, Screen.height / 2f + 16, 200, 26), "[E] вселиться", style);
        if (Toast.Length > 0) GUI.Label(new Rect(Screen.width / 2f - 100, Screen.height / 2f + 44, 400, 26), Toast, style);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(Screen.width / 2f - 2, Screen.height / 2f - 2, 4, 4), Texture2D.whiteTexture);
    }
}
