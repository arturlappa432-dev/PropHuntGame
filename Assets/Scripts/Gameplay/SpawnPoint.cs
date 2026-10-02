using UnityEngine;

// Точка-кандидат спавна на поверхности (docs/systems/spawn.md). Тематических пулов пока нет, только лимит по тиру.
public class SpawnPoint : MonoBehaviour
{
    public PropTier maxTier = PropTier.Small;
    void OnDrawGizmosSelected() { Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(transform.position, 0.05f); }
}
