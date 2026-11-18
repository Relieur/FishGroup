using System.Collections.Generic;
using UnityEngine;

namespace VRTest.Environment
{
	// 怪物巢穴：固定位置刷新 3~5 只小怪，模型随机不重复
	// 玩家靠近吸引仇恨；玩家离开范围后整窝复活
	// Boss 战斗时可以全局抑制所有巢穴的仇恨
	[AddComponentMenu("VR/Environment/Enemy Nest")]
	public class EnemyNest : MonoBehaviour
	{
		// 所有活跃巢穴，供 BossNest 统一抑制仇恨
		static readonly List<EnemyNest> s_All = new List<EnemyNest>();

		// 全局抑制标记：Boss 战斗时置 true
		static bool s_Suppressed;

		[Header("刷新")]
		[Tooltip("小怪预制体候选列表（YG_02/03/04/05），YG_01 是白模不要用")]
		public GameObject[] enemyPrefabs;

		[Min(1)] public int minCount = 3;
		[Min(1)] public int maxCount = 5;

		[Tooltip("怪全部死亡后，玩家离开多久整窝复活（秒）")]
		[Min(0f)] public float respawnDelay = 5f;

		[Header("刷新范围")]
		public float spawnRadius = 3.5f;
		public float groundRayHeight = 12f;
		public LayerMask groundMask = ~0;

		[Header("警戒与仇恨")]
		[Tooltip("玩家进入这个半径：怪开始靠近（慢速警戒）")]
		public float alertRadius = 16f;

		[Tooltip("玩家进入这个半径：怪全速冲过来（仇恨）")]
		public float aggroRadius = 9f;

		[Tooltip("玩家离开这个半径才开始计算复活计时")]
		public float leaveRadius = 20f;

		[Tooltip("警戒状态下的移动速度倍率")]
		[Range(0.1f, 1f)] public float alertSpeedScale = 0.45f;

		[Header("模型朝向")]
		[Tooltip("生成初始旋转，必须与场景 YG_01 一致：(270, 270, 0)")]
		public Vector3 spawnEuler = new Vector3(270f, 270f, 0f);

		[Header("调试")]
		public bool drawGizmos = true;

		public enum NestState { Idle, Alert, Combat }
		public NestState State => m_State;

		readonly List<LuffyEnemy> m_Enemies = new List<LuffyEnemy>();
		readonly List<GameObject> m_ShufflePool = new List<GameObject>();
		readonly List<float> m_BaseSpeed = new List<float>();
		// 每只怪的出生点，用于"回到原位"
		readonly List<Vector3> m_HomeSpots = new List<Vector3>();

		NestState m_State = NestState.Idle;
		float m_LeaveTimer;
		int m_TargetCount;
		Transform m_Player;

		// ---- 静态接口 ----

		// Boss 战斗开始/结束时调用，抑制或恢复所有巢穴的仇恨
		public static void SetAllSuppressed(bool suppressed)
		{
			s_Suppressed = suppressed;
			for (int i = 0; i < s_All.Count; i++)
			{
				if (s_All[i] != null) s_All[i].ApplySuppression();
			}
		}

		public static int ActiveNestCount => s_All.Count;

		void OnEnable()
		{
			if (!s_All.Contains(this)) s_All.Add(this);
		}

		void OnDisable()
		{
			s_All.Remove(this);
		}

		// ---- 生命周期 ----

		void Start()
		{
			ResolvePlayer();

			int modelCount = (enemyPrefabs != null) ? enemyPrefabs.Length : 0;
			if (modelCount == 0)
			{
				Debug.LogWarning("[Nest] " + name + " 没有配置 enemyPrefabs", this);
				return;
			}

			int lo = Mathf.Max(1, minCount);
			int hi = Mathf.Max(lo, maxCount);
			if (modelCount < hi) { hi = modelCount; if (lo > hi) lo = hi; }

			m_TargetCount = Random.Range(lo, hi + 1);
			SpawnAll();
		}

		void Update()
		{
			// Boss 战斗中：所有小怪停止追击，并走回出生点
			if (s_Suppressed)
			{
				SetState(NestState.Idle);
				ReturnHome();
				return;
			}

			ResolvePlayer();
			if (m_Player == null) return;

			float dist = HorizontalDistance(m_Player.position, transform.position);

			if (dist <= aggroRadius) SetState(NestState.Combat);
			else if (dist <= alertRadius) SetState(NestState.Alert);
			else SetState(NestState.Idle);

			if (dist > leaveRadius)
			{
				m_LeaveTimer += Time.deltaTime;
				if (m_LeaveTimer >= respawnDelay)
				{
					m_LeaveTimer = 0f;
					ReviveDead();
				}
			}
			else m_LeaveTimer = 0f;
		}

		// 全局抑制状态变化时立即应用
		void ApplySuppression()
		{
			if (s_Suppressed) SetState(NestState.Idle);
		}

		// 让所有存活的小怪走回各自的出生点
		void ReturnHome()
		{
			for (int i = 0; i < m_Enemies.Count; i++)
			{
				var e = m_Enemies[i];
				if (e == null || !e.IsAlive) continue;
				if (i >= m_HomeSpots.Count) continue;

				Vector3 home = m_HomeSpots[i];
				Vector3 pos = e.transform.position;

				Vector3 toHome = home - pos;
				toHome.y = 0f;

				// 已经到位置了就不再动
				if (toHome.sqrMagnitude < 0.04f) continue;

				Vector3 dir = toHome.normalized;
				float speed = m_BaseSpeed.Count > i && m_BaseSpeed[i] > 0f
					? m_BaseSpeed[i] : 0.6f;

				// 回家时速度稍快，避免磨蹭
				e.transform.position += dir * speed * 1.2f * Time.deltaTime;
			}
		}

		void SetState(NestState next)
		{
			if (m_State == next) return;
			m_State = next;

			for (int i = 0; i < m_Enemies.Count; i++)
			{
				var e = m_Enemies[i];
				if (e == null) continue;

				switch (next)
				{
					case NestState.Idle:
						e.chasePlayer = false;
						RestoreSpeed(i, e);
						break;

					case NestState.Alert:
						e.chasePlayer = true;
						SetSpeed(i, e, alertSpeedScale);
						break;

					case NestState.Combat:
						e.chasePlayer = true;
						RestoreSpeed(i, e);
						break;
				}
			}
		}

		void SetSpeed(int index, LuffyEnemy e, float scale)
		{
			EnsureBaseSpeed(index, e);
			e.moveSpeed = m_BaseSpeed[index] * scale;
		}

		void RestoreSpeed(int index, LuffyEnemy e)
		{
			EnsureBaseSpeed(index, e);
			e.moveSpeed = m_BaseSpeed[index];
		}

		void EnsureBaseSpeed(int index, LuffyEnemy e)
		{
			while (m_BaseSpeed.Count <= index) m_BaseSpeed.Add(-1f);
			if (m_BaseSpeed[index] < 0f) m_BaseSpeed[index] = e.moveSpeed;
		}

		void SpawnAll()
		{
			for (int i = 0; i < m_TargetCount; i++)
			{
				var e = CreateEnemy(i);
				if (e != null) m_Enemies.Add(e);
			}
			SetStateImmediate(NestState.Idle);
		}

		void SetStateImmediate(NestState s)
		{
			m_State = s;
			for (int i = 0; i < m_Enemies.Count; i++)
			{
				var e = m_Enemies[i];
				if (e != null) e.chasePlayer = (s != NestState.Idle);
			}
		}

		LuffyEnemy CreateEnemy(int index)
		{
			GameObject prefab = PickPrefab(index);
			if (prefab == null) return null;

			Vector3 pos = PickSpawnPosition(index, m_TargetCount);
			Quaternion rot = Quaternion.Euler(spawnEuler);

			var go = Instantiate(prefab, pos, rot);
			go.name = prefab.name + "_Nest" + (index + 1);

			if (go.transform.childCount > 0)
			{
				var child = go.transform.GetChild(0);
				Vector3 lp = child.localPosition;
				child.localPosition = new Vector3(0f, lp.y, 0f);
			}

			var e = go.GetComponent<LuffyEnemy>();
			if (e == null) e = go.AddComponent<LuffyEnemy>();

			// 关键：防止 Start() 把怪搬到玩家视野内
			e.spawnInView = false;
			// 复活由巢穴统一管理
			e.respawnDelay = 99999f;

			// 记录出生点
			while (m_HomeSpots.Count <= index) m_HomeSpots.Add(Vector3.zero);
			m_HomeSpots[index] = pos;

			return e;
		}

		GameObject PickPrefab(int index)
		{
			if (enemyPrefabs == null || enemyPrefabs.Length == 0) return null;

			if (m_ShufflePool.Count == 0)
			{
				m_ShufflePool.AddRange(enemyPrefabs);
				for (int i = m_ShufflePool.Count - 1; i > 0; i--)
				{
					int j = Random.Range(0, i + 1);
					var tmp = m_ShufflePool[i];
					m_ShufflePool[i] = m_ShufflePool[j];
					m_ShufflePool[j] = tmp;
				}
			}

			return m_ShufflePool[index % m_ShufflePool.Count];
		}

		Vector3 PickSpawnPosition(int index, int total)
		{
			float baseAngle = 360f / Mathf.Max(1, total) * index;
			float jitter = Random.Range(-18f, 18f);
			float rad = (baseAngle + jitter) * Mathf.Deg2Rad;

			float r = spawnRadius * (index % 2 == 0 ? 0.55f : 1f);
			r *= Random.Range(0.85f, 1.15f);

			Vector3 pnt = transform.position + new Vector3(
				Mathf.Sin(rad) * r, 0f, Mathf.Cos(rad) * r);

			return SnapToGround(pnt);
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

		void ReviveDead()
		{
			for (int i = 0; i < m_Enemies.Count; i++)
			{
				var e = m_Enemies[i];
				if (e == null) continue;
				if (e.IsAlive) continue;

				Destroy(e.gameObject);
				var fresh = CreateEnemy(i);
				if (fresh != null)
				{
					m_Enemies[i] = fresh;
					fresh.chasePlayer = (m_State != NestState.Idle && !s_Suppressed);
				}
			}
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

			Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.9f);
			DrawCircle(transform.position, spawnRadius);

			Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.7f);
			DrawCircle(transform.position, alertRadius);

			Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.9f);
			DrawCircle(transform.position, aggroRadius);

			Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.5f);
			DrawCircle(transform.position, leaveRadius);
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