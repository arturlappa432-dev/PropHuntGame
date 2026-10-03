using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Упрощённый ragdoll охотника при таране (ram-kick.md): 7 физических тел (таз, туловище, голова, 2 руки, 2 ноги),
// без предплечий/кистей/стоп. Точность падения не важна, важно, чтобы выглядело смешно.
// Жизнь: полёт ~3 с по жёсткому таймеру -> проверка позиции -> простой процедурный подъём -> управление возвращается.
public class HunterRagdoll : MonoBehaviour
{
    class Part
    {
        public Transform t; public Rigidbody rb; public Collider col;
        public Vector3 standPos; public Quaternion standRot;   // поза стоя в системе корня охотника
        public Vector3 startPos; public Quaternion startRot;
    }

    public int BodyCount => parts.Count;

    HunterPlayer hunter;
    RamKickAuthority auth;
    readonly List<Part> parts = new List<Part>();
    Part pelvis, torso, head, armR;
    Vector3 preKnockPos;
    Transform gun, gunOldParent; Vector3 gunOldPos; Quaternion gunOldRot;
    Vector3 camLocalPos; Quaternion camLocalRot;

    public static HunterRagdoll Begin(HunterPlayer h, Vector3 launch, Vector3 spin, RamKickAuthority a, Material mat, Transform gun, Pose camPose)
    {
        var go = new GameObject("HunterRagdoll");
        var r = go.AddComponent<HunterRagdoll>();
        r.hunter = h; r.auth = a; r.gun = gun;
        r.preKnockPos = h.transform.position;
        r.Build(mat, camPose);
        foreach (var p in r.parts)
        {
            // Явный импульс каждому телу (с разбросом, чтобы конечности разлетались): охотник не зависит от выталкивания CC.
            p.rb.AddForce(launch + Random.insideUnitSphere * 0.8f, ForceMode.VelocityChange);
            p.rb.AddTorque(spin + Random.insideUnitSphere * 4f, ForceMode.VelocityChange);
        }
        r.StartCoroutine(r.Run());
        return r;
    }

    // Поза камеры, закреплённая на голове: камера кувыркается вместе с ней.
    public bool TryGetCameraPose(out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        if (head == null) return false;
        pos = head.t.TransformPoint(camLocalPos);
        rot = head.t.rotation * camLocalRot;
        return true;
    }

    Part Make(string name, PrimitiveType type, Vector3 localPos, Vector3 size, float mass, Material mat, Part parent, Vector3 anchorOffset)
    {
        var root = hunter.transform;
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(transform, false);
        go.transform.SetPositionAndRotation(root.TransformPoint(localPos), root.rotation);
        go.transform.localScale = size;
        int layer = LayerMask.NameToLayer("Hunter");
        if (layer >= 0) go.layer = layer;
        if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.linearDamping = 0.1f; rb.angularDamping = 0.6f;
        rb.maxAngularVelocity = 20f;
        rb.solverIterations = 12;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        var col = go.GetComponent<Collider>();
        col.material = new PhysicsMaterial { bounciness = 0.15f, dynamicFriction = 0.6f, staticFriction = 0.6f };
        var part = new Part { t = go.transform, rb = rb, col = col, standPos = localPos, standRot = Quaternion.identity };
        if (parent != null)
        {
            var j = go.AddComponent<CharacterJoint>();
            j.connectedBody = parent.rb;
            j.enableProjection = true;
            j.enablePreprocessing = false;
            j.anchor = go.transform.InverseTransformPoint(root.TransformPoint(localPos + anchorOffset));
            j.lowTwistLimit = new SoftJointLimit { limit = -25f };
            j.highTwistLimit = new SoftJointLimit { limit = 25f };
            j.swing1Limit = new SoftJointLimit { limit = 45f };
            j.swing2Limit = new SoftJointLimit { limit = 30f };
        }
        parts.Add(part);
        return part;
    }

    void Build(Material mat, Pose camPose)
    {
        // Силуэт примерно как у капсулы-охотника 1,8 м шириной 0,5. Суставы: шея, плечи, бёдра, поясница.
        pelvis = Make("Pelvis", PrimitiveType.Cube, new Vector3(0, 0.95f, 0), new Vector3(0.36f, 0.2f, 0.24f), 12f, mat, null, Vector3.zero);
        torso = Make("Torso", PrimitiveType.Cube, new Vector3(0, 1.3f, 0), new Vector3(0.42f, 0.5f, 0.24f), 25f, mat, pelvis, new Vector3(0, -0.25f, 0));
        head = Make("Head", PrimitiveType.Sphere, new Vector3(0, 1.65f, 0), new Vector3(0.28f, 0.28f, 0.28f), 5f, mat, torso, new Vector3(0, -0.15f, 0));
        Make("ArmL", PrimitiveType.Capsule, new Vector3(-0.3f, 1.3f, 0), new Vector3(0.12f, 0.3f, 0.12f), 4f, mat, torso, new Vector3(0, 0.28f, 0));
        armR = Make("ArmR", PrimitiveType.Capsule, new Vector3(0.3f, 1.3f, 0), new Vector3(0.12f, 0.3f, 0.12f), 4f, mat, torso, new Vector3(0, 0.28f, 0));
        Make("LegL", PrimitiveType.Capsule, new Vector3(-0.11f, 0.43f, 0), new Vector3(0.18f, 0.43f, 0.18f), 10f, mat, pelvis, new Vector3(0, 0.4f, 0));
        Make("LegR", PrimitiveType.Capsule, new Vector3(0.11f, 0.43f, 0), new Vector3(0.18f, 0.43f, 0.18f), 10f, mat, pelvis, new Vector3(0, 0.4f, 0));

        // Дробовик остаётся в руке: на время ragdoll становится ребёнком правой руки.
        if (gun != null)
        {
            gunOldParent = gun.parent; gunOldPos = gun.localPosition; gunOldRot = gun.localRotation;
            gun.SetParent(armR.t, true);
        }
        camLocalPos = head.t.InverseTransformPoint(camPose.position);
        camLocalRot = Quaternion.Inverse(head.t.rotation) * camPose.rotation;
    }

    IEnumerator Run()
    {
        // Жёсткий таймер: заканчиваем независимо от того, успокоилась ли физика.
        float t = 0f;
        while (t < auth.ragdollDuration)
        {
            t += Time.deltaTime;
            if (pelvis.t.position.y < -5f) break;   // улетел за карту
            yield return null;
        }

        // Проверка позиции перед подъёмом: свободная точка рядом, иначе точка, где охотник стоял до удара.
        Vector3 pel = pelvis.t.position;
        Vector3 target = RamKickAuthority.FindStandPoint(new Vector3(pel.x, Mathf.Max(pel.y, preKnockPos.y), pel.z), 0.25f, 1.8f, preKnockPos);

        // Ориентация: лицом вниз (грудь смотрит в пол) встаём по направлению головы, лицом вверх — в сторону ног.
        bool faceDown = torso.t.forward.y < 0f;
        Vector3 axis = head.t.position - pelvis.t.position; axis.y = 0f;
        if (axis.sqrMagnitude < 0.01f) { axis = torso.t.forward * (faceDown ? 1f : -1f); axis.y = 0f; }   // лежим вертикально: берём разворот груди
        Vector3 facing = faceDown ? axis : -axis;
        if (facing.sqrMagnitude < 1e-4f) facing = hunter.transform.forward;
        Quaternion rootRot = Quaternion.LookRotation(facing.normalized);

        foreach (var p in parts)
        {
            p.rb.linearVelocity = Vector3.zero; p.rb.angularVelocity = Vector3.zero;
            p.rb.isKinematic = true;
            p.col.enabled = false;
            var j = p.t.GetComponent<CharacterJoint>(); if (j != null) Destroy(j);
            p.startPos = p.t.position; p.startRot = p.t.rotation;
        }
        for (float u = 0f; u < auth.getUpDuration; u += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, u / auth.getUpDuration);
            foreach (var p in parts)
            {
                p.t.position = Vector3.Lerp(p.startPos, target + rootRot * p.standPos, k);
                p.t.rotation = Quaternion.Slerp(p.startRot, rootRot * p.standRot, k);
            }
            yield return null;
        }

        if (gun != null)
        {
            gun.SetParent(gunOldParent, false);
            gun.localPosition = gunOldPos; gun.localRotation = gunOldRot;
        }
        hunter.EndKnock(target, rootRot.eulerAngles.y, Time.time + auth.knockImmunity);
        Destroy(gameObject);
    }
}
