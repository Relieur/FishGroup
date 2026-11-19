using UnityEngine;
using JSukoAnimals;

// Boss 发射的泡泡：直线飞向玩家
// 玩家举着盾牌时 100% 被挡下并扣盾牌耐久；没有盾就命中玩家扣血
[AddComponentMenu("VR/Boss Bubble")]
[DisallowMultipleComponent]
public class BossBubble : MonoBehaviour
{
	[Header("飞行")]
	[Tooltip("飞行速度（米/秒），Boss 发射时会覆盖")]
	public float speed = 8f;
	[Tooltip("存活时间（秒），到时间自动销毁")]
	public float lifetime = 8f;

	[Header("命中")]
	[Tooltip("命中玩家的伤害，Boss 发射时会覆盖")]
	public float damage = 10f;
	[Tooltip("离玩家头显多近算命中（米）")]
	public float hitRadius = 0.55f;
	[Tooltip("离盾牌多近算撞上（米）")]
	public float shieldPadding = 0.18f;

	Transform m_Player;
	Vector3 m_Velocity;
	float m_DieTime;

	// 由 LuffyEnemy 发射时调用：锁定玩家并给初速度
	public void Launch(Transform player, float moveSpeed, float hitDamage)
	{
		m_Player = player;
		speed = Mathf.Max(0.1f, moveSpeed);
		damage = Mathf.Max(0f, hitDamage);
		m_DieTime = Time.time + Mathf.Max(0.5f, lifetime);

		Vector3 target = player != null ? player.position : transform.position + Vector3.forward;
		Vector3 dir = target - transform.position;
		if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;

		m_Velocity = dir.normalized * speed;
	}

	void Update()
	{
		if (Time.time >= m_DieTime)
		{
			Destroy(gameObject);
			return;
		}

		transform.position += m_Velocity * Time.deltaTime;

		// 盾牌优先：举着盾时一定挡住
		if (TryBlockByShield()) return;

		// 没盾：飞到玩家头显附近就扣血
		if (m_Player == null) return;

		Vector3 delta = transform.position - m_Player.position;
		if (delta.sqrMagnitude > hitRadius * hitRadius) return;

		PlayerHealthBar health = PlayerHealthBar.Instance;
		if (health != null) health.TakeDamage(damage);
		Destroy(gameObject);
	}

	bool TryBlockByShield()
	{
		if (!SardineUserController.IsShieldRaised) return false;

		GameObject shield = SardineUserController.ShieldObject;
		if (shield == null) return false;

		bool blocked = false;

		// 盾面是个很薄的包围盒，用 ClosestPoint 判断泡泡是否已经贴到盾上
		Renderer shieldRenderer = shield.GetComponentInChildren<Renderer>();
		if (shieldRenderer != null)
		{
			Bounds bounds = shieldRenderer.bounds;
			Vector3 closest = bounds.ClosestPoint(transform.position);
			blocked = (closest - transform.position).sqrMagnitude <= shieldPadding * shieldPadding;
		}

		// 兜底：只要举着盾，泡泡已经飞到玩家身边（本来就要命中）就一定被挡下
		if (!blocked && m_Player != null)
		{
			float reach = hitRadius + 0.9f;
			blocked = (transform.position - m_Player.position).sqrMagnitude <= reach * reach;
		}

		if (!blocked) return false;

		// 挡下：扣盾牌耐久（默认和发射一发泡泡消耗相同），泡泡消失
		SardineUserController.TryBlockBossBubble();
		Destroy(gameObject);
		return true;
	}
}
