using System.Collections.Generic;
using UnityEngine;

namespace VRTest.Environment
{
	// Boss 巢穴：在固定位置生成一只放大的 Boss
	// Boss 追击玩家但受 leash 半径限制；被攻击时抑制所有小怪的仇恨
	// Boss 死亡后永久消失（不复活）
	[AddComponentMenu("VR/Environment/Boss Nest")]
	public class BossNest : MonoBehaviour
	{
		[Header("Boss 外观")]
		[Tooltip("Boss 使用的模型（建议 YG_02~05）")]
		public GameObject bossPrefab;

		[Tooltip("整体缩放倍率。1 = 和小怪一样大，2 = 两倍")]
		[Min(1f)] public float bossScale = 2f;

		[Header("Boss 属性")]
		[Tooltip("被泡泡命中多少次死亡。小怪是 3，三倍就是 9")]
		[Min(1)] public int bossMaxHits = 9;

		[Tooltip("移动速度（米/秒）。比小怪慢一点，有压迫感")]
		public float bossMoveSpeed = 0.35f;

		[Tooltip("停在离玩家多远的位置（米）。体型大所以要远一点，避免糊脸")]
		public float bossStopDistance = 2.8f;

		[Tooltip("转身平滑速度")]
		public float bossTurnSpeed = 1.5f;

		[Header("追击限制")]
		[Tooltip("Boss 离巢穴最大距离（米）。超过就往回拉，不能追太远")]
		public float leashRadius = 12f;

		[Tooltip("玩家进入这个半径，Boss 才开始追（米）")]
		public float activateRadius = 18f;

		[Tooltip("拉回巢穴时的速度倍率（相对正常移动）")]
		[Range(0.5f, 3f)] public float returnSpeedScale = 1.8f;

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
		// 是否已被玩家攻击过（激怒）。未激怒时 Boss 只转向不追击
		bool m_Enraged;

		// 基准姿态与当前偏航，用于回家时面向移动方向
		Quaternion m_BaseRot = Quaternion.identity;
		float m_CurYaw;

		public bool IsBossAlive => m_Boss != null && m_Boss.IsAlive;
		public bool IsDefeated => m_Dead;

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

			// 关键：防止 Start() 把 Boss 搬到玩家视野内
			m_Boss.spawnInView = false;
			// Boss 不自动复活，死了就永久消失
			m_Boss.respawnDelay = 99999f;

			// 三倍血量 + 调参
			m_Boss.maxHits = Mathf.Max(1, bossMaxHits);
			m_Boss.moveSpeed = bossMoveSpeed;
			m_Boss.stopDistance = bossStopDistance;
			m_Boss.turnSpeed = bossTurnSpeed;
			m_Boss.chasePlayer = false;   // 初始不追，等玩家靠近

			m_LastHealth = 1f;

			// 记录基准姿态（等价 LuffyEnemy 的 m_BaseRotation），初始 yaw 为 0
			m_BaseRot = rot;
			m_CurYaw = 0f;

			Debug.Log(string.Format("[Boss] 已生成 {0}，血量 {1}，缩放 {2}x",
				go.name, m_Boss.maxHits, bossScale));
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

			EnforceLeash();
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

			// 首次被攻击：进入激怒状态，之后才会追击玩家
			m_Enraged = true;
			m_Boss.chasePlayer = true;
		}

		void ReleaseSuppress()
		{
			if (!m_Suppressing) return;
			m_Suppressing = false;
			EnemyNest.SetAllSuppressed(false);
		}

		// 限制 Boss 的追击范围：离巢穴太远就拉回来
		void EnforceLeash()
		{
			float distFromHome = HorizontalDistance(m_Boss.transform.position, m_HomePosition);

			if (distFromHome > leashRadius)
			{
				// 追出去太远了：强制往回走
				m_Boss.chasePlayer = false;
				ReturnHome();
				return;   // 回程中不处理追击
			}

			// 未被攻击过：只转向，不追击
			if (!m_Enraged)
			{
				m_Boss.chasePlayer = false;
				return;
			}

			// 激怒后：玩家在激活范围内就追，离开则返回原位
			float distToPlayer = HorizontalDistance(m_Player.position, m_Boss.transform.position);

			if (distToPlayer > activateRadius)
			{
				// 玩家跑远了：停止追击，游回巢穴
				m_Boss.chasePlayer = false;
				ReturnHome();
			}
			else
			{
				m_Boss.chasePlayer = true;
			}
		}

		// Boss 游回巢穴，并面向移动方向
		void ReturnHome()
		{
			Vector3 toHome = m_HomePosition - m_Boss.transform.position;
			toHome.y = 0f;

			// 到位置了就停下
			if (toHome.sqrMagnitude < 0.04f) return;

			Vector3 dir = toHome.normalized;
			float speed = bossMoveSpeed * returnSpeedScale;

			m_Boss.transform.position += dir * speed * Time.deltaTime;

			// 面向移动方向，复刻 LuffyEnemy 的旋转公式
			float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + m_Boss.facingYawOffset;
			m_CurYaw = Mathf.LerpAngle(m_CurYaw, targetYaw,
				Mathf.Clamp01(m_Boss.turnSpeed * Time.deltaTime));
			m_Boss.transform.rotation = Quaternion.AngleAxis(m_CurYaw, Vector3.up) * m_BaseRot;
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

			Gizmos.color = new Color(1f, 0.2f, 0.8f, 0.9f);
			DrawCircle(transform.position, leashRadius);

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