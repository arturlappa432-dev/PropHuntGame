using UnityEngine;

// Вешается на предмет на время полёта после пинка: помнит, когда тело последний раз опиралось на поверхность под собой.
// Нужен для настоящей проверки «осел» (низкая скорость И контакт с опорой), а не только по скорости/таймауту.
public class KickContactProbe : MonoBehaviour
{
    public float LastSupportTime { get; private set; } = float.NegativeInfinity;
    Rigidbody rb;

    void Awake() { rb = GetComponent<Rigidbody>(); }

    void OnCollisionEnter(Collision c) { Check(c); }
    void OnCollisionStay(Collision c) { Check(c); }

    void Check(Collision c)
    {
        float centerY = rb != null ? rb.worldCenterOfMass.y : transform.position.y;
        for (int i = 0; i < c.contactCount; i++)
        {
            var cp = c.GetContact(i);
            if (Mathf.Abs(cp.normal.y) > 0.5f && cp.point.y < centerY) { LastSupportTime = Time.time; return; }
        }
    }

    public bool Supported(float window = 0.15f) => Time.time - LastSupportTime <= window;
}
