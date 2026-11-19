using System.Collections.Generic;
using UnityEngine;

namespace VRTest.Environment
{
	// Boss 巢穴：在固定位置生成一只放大的 Boss
	// Boss 不再接近玩家，原地朝玩家发射泡泡；被攻击时抑制所有小怪的仇恨
	// Boss 死亡后永久消失（不复活）
	[AddComponentMenu("VR/Environment/Boss Nest")]
	public class BossNest : MonoBehaviour
	{
		[Header("Boss 外观")]
		[Tooltip("Boss 使用的模型（建议 YG_02~05）")]
		public GameObject bossPrefab;

		[Tooltip("整体缩放倍率。1 = 和小怪一样大，4 = 四倍")]
		[Min(1f)] public float bossScale = 4f;

		[Header("Boss 属性")]
		[Tooltip("Boss 血量。小怪固定 20，Boss 100")]
		[Min(1f)] public float bossHealth = 100f;

		[Tooltip("转身平滑速度")]
		public float bossTurnSpeed = 1.5f;

		[Header("泡泡攻击")]
		[Tooltip("泡泡模板；留空则运行时会按名字 BossBubble 在场景里自动查找")]
		public GameObject bubbleTemplate;

		[Tooltip("发射间隔（秒）")]
		public float bubbleInterval = 2.5f;

		[Tooltip("泡泡飞行速度（米/秒）")]
		public float bubbleSpeed = 8f;

		[Tooltip("泡泡相对模板的缩放倍数")]
		public float bubbleScale = 3f;

		[Tooltip("泡泡命中玩家的伤害")]
		public float bubbleDamage = 10f;

		[Header("巢区域")]
		[Tooltip("Boss 巢的范围半径（米），只用于范围显示")]
		public float activateRadius = 27f;

		[Header("索敌")]
		[Tooltip("Boss 的索敌范围（米）：玩家进入后开始发射泡泡")]
		public float aggroRadius = 22f;

		[Tooltip("玩家离开索敌范围多久后 Boss 停火（秒）")]
		[Min(0f)] public float loseTargetDelay = 5f;

		[Header("贴地")]
		public float groundRayHeight = 12f;
		public LayerMask groundMask = ~0;

		[Header("模型朝向")]
		[Tooltip("生成初始旋转，必须与场景 YG_01 一致：(270, 270, 0)")]
		public Vector3 spawnEuler = new Vector3(270f, 270f, 0f);

		[Header("调试")]
		public bool drawGizmos = true;

		// 运行时
		LuffyEnemy m_Boss;
		Transform m_Player;
		Vector3 m_HomePosition;
		float m_LastHealth = 1f;
		bool m_Dead;
		bool m_Suppressing;
		bool m_Engaged;     // 是否处于索敌状态
		float m_LoseTimer;  // 玩家离开索敌范围后累计的时间

		public bool IsBossAlive => m_Boss != null && m_Boss.IsAlive;
		public bool IsDefeated => m_Dead;

		// Boss 死亡时通知（胜利弹窗等）
		public static event System.Action BossDefeated;

		void Start()
		{
			ResolvePlayer();
			m_HomePosition = transform.position;
			SpawnBoss();
		}

		void OnDisable()
		{
			// 场景卸载/禁用时恢复小怪仇恨，避免状态卡死
			if (m_Suppressing) EnemyNest.SetAllSuppressed(false);
		}

		void SpawnBoss()
		{
			if (bossPrefab == null)
			{
				Debug.LogWarning("[Boss] " + name + " 没有指定 bossPrefab", this);
				return;
			}

			Vector3 pos = SnapToGround(m_HomePosition);
			Quaternion rot = Quaternion.Euler(spawnEuler);

			var go = Instantiate(bossPrefab, pos, rot);
			go.name = "Boss_" + bossPrefab.name;

			// 放大
			go.transform.localScale = Vector3.one * bossScale;

			// YG 的 FBX 带水平偏移，清掉让 Boss 落在巢穴位置
			if (go.transform.childCount > 0)
			{
				var child = go.transform.GetChild(0);
				Vector3 lp = child.localPosition;
				child.localPosition = new Vector3(0f, lp.y, 0f);
			}

			m_Boss = go.GetComponent<LuffyEnemy>();
			if (m_Boss == null) m_Boss = go.AddComponent<LuffyEnemy>();

			// 血量固定 100，体型 4 倍；不再接近玩家
			m_Boss.maxHealth = Mathf.Max(1f, bossHealth);
			m_Boss.turnSpeed = bossTurnSpeed;
			m_Boss.chasePlayer = false;

			// 攻击方式：原地发射泡泡
			m_Boss.shootBubbles = false;   // 玩家进入激活范围后由 UpdateAttack 打开
			m_Boss.bubbleTemplate = bubbleTemplate != null ? bubbleTemplate : FindBubbleTemplate();
			m_Boss.bubbleInterval = bubbleInterval;
			m_Boss.bubbleSpeed = bubbleSpeed;
			m_Boss.bubbleScale = bubbleScale;
			m_Boss.bubbleDamage = bubbleDamage;
			m_Boss.bubbleRange = aggroRadius;

			// Boss 血条挂在 Boss 头顶：体型放大后血条跟着放大，避免离得远时看不见
			m_Boss.barWidth = 0.9f * bossScale;
			m_Boss.barHeight = 0.14f * bossScale;
			m_Boss.barHeadOffset = 0.3f * bossScale;

			m_LastHealth = 1f;

			Debug.Log(string.Format("[Boss] 已生成 {0}，血量 {1}，缩放 {2}x",
				go.name, m_Boss.maxHealth, bossScale));
		}

		void Update()
		{
			if (m_Boss == null) return;

			// Boss 已死：解除抑制，标记为已击败
			if (m_Dead || !m_Boss.IsAlive)
			{
				if (!m_Dead)
				{
					m_Dead = true;
					ReleaseSuppress();
					BossDefeated?.Invoke();
					Debug.Log("[Boss] Boss 已被击败");
				}
				return;
			}

			ResolvePlayer();
			if (m_Player == null) return;

			// 检测是否被攻击（血量下降）
			float hp = m_Boss.HealthFraction;
			if (hp < m_LastHealth - 0.0001f)
			{
				OnBossDamaged();
			}
			m_LastHealth = hp;

			UpdateAttack();
		}

		// Boss 被打了：抑制所有小怪的仇恨
		void OnBossDamaged()
		{
			if (!m_Suppressing)
			{
				m_Suppressing = true;
				EnemyNest.SetAllSuppressed(true);
				Debug.Log("[Boss] 玩家开始攻击 Boss，已抑制所有小怪仇恨");
			}
		}

		void ReleaseSuppress()
		{
			if (!m_Suppressing) return;
			m_Suppressing = false;
			EnemyNest.SetAllSuppressed(false);
		}

		// Boss 原地不动：玩家进入索敌范围就朝玩家发射泡泡，离开一段时间后停火
		void UpdateAttack()
		{
			if (bubbleTemplate != null) m_Boss.bubbleTemplate = bubbleTemplate;
			else if (m_Boss.bubbleTemplate == null) m_Boss.bubbleTemplate = FindBubbleTemplate();

			float distToPlayer = HorizontalDistance(m_Player.position, m_Boss.transform.position);

			// 进入索敌范围立刻开火；离开后要过一段时间才停火
			if (distToPlayer <= aggroRadius)
			{
				m_Engaged = true;
				m_LoseTimer = 0f;
			}
			else if (m_Engaged)
			{
				m_LoseTimer += Time.deltaTime;
				if (m_LoseTimer >= loseTargetDelay)
				{
					m_Engaged = false;
					m_LoseTimer = 0f;
				}
			}

			m_Boss.shootBubbles = m_Engaged;
		}

		// 按名字在场景里找泡泡模板（包含未激活的隐藏对象）
		static GameObject FindBubbleTemplate()
		{
			GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
			for (int i = 0; i < all.Length; i++)
			{
				if (all[i] != null && all[i].name == "BossBubble" && all[i].scene.IsValid())
					return all[i];
			}
			return null;
		}

		Vector3 SnapToGround(Vector3 point)
		{
			Vector3 origin = new Vector3(point.x, point.y + groundRayHeight, point.z);
			if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
					groundRayHeight * 3f, groundMask, QueryTriggerInteraction.Ignore))
			{
				return new Vector3(point.x, hit.point.y + 0.15f, point.z);
			}
			return point;
		}

		void ResolvePlayer()
		{
			if (m_Player != null) return;
			Camera cam = Camera.main;
			if (cam == null) cam = FindFirstObjectByType<Camera>();
			if (cam != null) m_Player = cam.transform;
		}

		static float HorizontalDistance(Vector3 a, Vector3 b)
		{
			float dx = a.x - b.x;
			float dz = a.z - b.z;
			return Mathf.Sqrt(dx * dx + dz * dz);
		}

		void OnDrawGizmosSelected()
		{
			if (!drawGizmos) return;

			Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.6f);
			DrawCircle(transform.position, activateRadius);
		}

		static void DrawCircle(Vector3 center, float radius)
		{
			const int segments = 48;
			Vector3 prev = center + new Vector3(radius, 0f, 0f);
			for (int i = 1; i <= segments; i++)
			{
				float a = i * Mathf.PI * 2f / segments;
				Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
				Gizmos.DrawLine(prev, next);
				prev = next;
			}
		}
	}
}