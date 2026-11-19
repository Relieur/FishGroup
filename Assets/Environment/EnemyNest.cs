using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VRTest.Environment
{
	// 小怪巢穴：在巢穴（Boss 巢）区域内随机刷新小怪，同时最多 maxAlive 只
	// 玩家靠近才补怪，进入巢区域后小怪才朝玩家走
	// Boss 开战后小怪一边往外走一边溶解消失
	[AddComponentMenu("VR/Environment/Enemy Nest")]
	public class EnemyNest : MonoBehaviour
	{
		// 所有活跃巢穴，供 BossNest 统一抑制
		static readonly List<EnemyNest> s_All = new List<EnemyNest>();

		// 全局抑制标记：Boss 战斗时置 true
		static bool s_Suppressed;

		[Header("刷新")]
		[Tooltip("小怪预制体候选列表（YG_02/03/04/05），YG_01 是白模不要用")]
		public GameObject[] enemyPrefabs;

		[Tooltip("同时存活的小怪上限")]
		[Min(1)] public int maxAlive = 7;

		[Tooltip("补怪的间隔（秒）")]
		[Min(0f)] public float spawnInterval = 2f;

		[Header("一波制")]
		[Tooltip("勾选后整场只生成 totalCount 只，全部消灭后不再刷新")]
		public bool oneShotWave = false;

		[Tooltip("一波的小怪总数（oneShotWave 时生效）")]
		[Min(1)] public int totalCount = 12;

		[Header("刷新范围（Boss 巢区域）")]
		[Tooltip("巢穴中心周围这个半径内随机生成")]
		public float spawnRadius = 12f;

		[Tooltip("小怪之间的最小生成间距（米）")]
		public float minSpawnSpacing = 2.5f;

		public float groundRayHeight = 12f;
		public LayerMask groundMask = ~0;

		[Header("索敌")]
		[Tooltip("每只小怪的索敌范围（米）：玩家进入后开始追击")]
		public float aggroRadius = 12f;

		[Tooltip("玩家离开索敌范围多久后放弃追击（秒）")]
		[Min(0f)] public float loseTargetDelay = 4f;

		[Header("撤退（Boss 开战）")]
		[Tooltip("往外走的距离（米）")]
		public float fleeDistance = 6f;

		[Tooltip("往外走并溶解消失的时长（秒）")]
		[Min(0.1f)] public float fleeDuration = 2f;

		[Header("模型朝向")]
		[Tooltip("生成初始旋转，必须与场景 YG_01 一致：(270, 270, 0)")]
		public Vector3 spawnEuler = new Vector3(270f, 270f, 0f);

		[Header("调试")]
		public bool drawGizmos = true;

		// 一只小怪在巢穴里的记录
		class Minion
		{
			public LuffyEnemy enemy;
			public Vector3 home;
			public Quaternion baseRot;
			public float yaw;
			public float speed;
			public float loseTimer;   // 玩家离开索敌范围后累计的时间
		}

		readonly List<Minion> m_Minions = new List<Minion>();
		readonly List<GameObject> m_ShufflePool = new List<GameObject>();

		Transform m_Player;
		bool m_Fled;       // Boss 开战后已经清场，不再刷新
		bool m_Retreating;
		float m_SpawnTimer;
		int m_SpawnedTotal;

		// ---- 静态接口 ----

		// Boss 战斗开始时调用：抑制所有巢穴，小怪离开并溶解
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

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void ResetStatics()
		{
			s_Suppressed = false;
		}

		// ---- 生命周期 ----

		void Start()
		{
			ResolvePlayer();

			if (enemyPrefabs == null || enemyPrefabs.Length == 0)
				Debug.LogWarning("[Nest] " + name + " 没有配置 enemyPrefabs", this);

			// 一波制：开局就把整波刷出来，之后不再补充
			if (oneShotWave)
			{
				int count = Mathf.Max(1, totalCount);
				for (int i = 0; i < count; i++) SpawnOne();
			}
		}

		void Update()
		{
			if (s_Suppressed)
			{
				BeginRetreat();
				return;
			}

			ResolvePlayer();
			if (m_Player == null) return;

			// Boss 开战后已经清场，之后不再刷新
			if (m_Fled) return;

			CleanupDead();

			// 每只小怪各自判断索敌范围；离开范围一段时间后才放弃追击
			UpdateAggro();
			ReturnHome();

			// 一波制：开战时已经全部生成，死光就结束
			if (oneShotWave) return;

			// 按间隔把巢补满到上限（与玩家距离无关）
			if (m_Minions.Count >= maxAlive) return;

			m_SpawnTimer += Time.deltaTime;
			if (m_SpawnTimer >= spawnInterval)
			{
				m_SpawnTimer = 0f;
				SpawnOne();
			}
		}

		void ApplySuppression()
		{
			if (s_Suppressed) BeginRetreat();
		}

		// 死亡的小怪等溶解播完再销毁，然后从列表里移除，腾出名额补新的
		void CleanupDead()
		{
			for (int i = m_Minions.Count - 1; i >= 0; i--)
			{
				Minion m = m_Minions[i];
				if (m.enemy == null)
				{
					m_Minions.RemoveAt(i);
					continue;
				}

				if (m.enemy.IsAlive) continue;

				Destroy(m.enemy.gameObject, 2f);
				m_Minions.RemoveAt(i);
			}
		}

		// 每只小怪单独索敌：进范围立刻追；出范围后累计时间，超过 loseTargetDelay 才放弃
		void UpdateAggro()
		{
			for (int i = 0; i < m_Minions.Count; i++)
			{
				Minion m = m_Minions[i];
				LuffyEnemy e = m.enemy;
				if (e == null || !e.IsAlive) continue;

				float distance = HorizontalDistance(e.transform.position, m_Player.position);

				if (distance <= aggroRadius)
				{
					m.loseTimer = 0f;
					e.chasePlayer = true;
					continue;
				}

				if (!e.chasePlayer) continue;

				m.loseTimer += Time.deltaTime;
				if (m.loseTimer >= loseTargetDelay)
				{
					m.loseTimer = 0f;
					e.chasePlayer = false;
				}
			}
		}

		// 放弃追击的小怪走回各自的出生点
		void ReturnHome()
		{
			for (int i = 0; i < m_Minions.Count; i++)
			{
				Minion m = m_Minions[i];
				if (m.enemy == null || !m.enemy.IsAlive) continue;
				if (m.enemy.chasePlayer) continue;

				Vector3 pos = m.enemy.transform.position;
				Vector3 toHome = m.home - pos;
				toHome.y = 0f;

				if (toHome.sqrMagnitude < 0.04f) continue;

				Vector3 dir = toHome.normalized;
				m.enemy.transform.position += dir * m.speed * 1.2f * Time.deltaTime;

				// 面向移动方向：复刻 LuffyEnemy 的旋转公式
				float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + m.enemy.facingYawOffset;
				m.yaw = Mathf.LerpAngle(m.yaw, targetYaw, Mathf.Clamp01(m.enemy.turnSpeed * Time.deltaTime));
				m.enemy.transform.rotation = Quaternion.AngleAxis(m.yaw, Vector3.up) * m.baseRot;
			}
		}

		void SpawnOne()
		{
			GameObject prefab = PickPrefab(m_SpawnedTotal);
			if (prefab == null) return;

			m_SpawnedTotal++;

			Vector3 pos = PickSpawnPosition();
			Quaternion rot = Quaternion.Euler(spawnEuler);

			GameObject go = Instantiate(prefab, pos, rot);
			go.name = prefab.name + "_Nest" + (m_SpawnedTotal);

			// YG 的 FBX 带水平偏移，清掉让它落在生成点上
			if (go.transform.childCount > 0)
			{
				Transform child = go.transform.GetChild(0);
				Vector3 lp = child.localPosition;
				child.localPosition = new Vector3(0f, lp.y, 0f);
			}

			LuffyEnemy e = go.GetComponent<LuffyEnemy>();
			if (e == null) e = go.AddComponent<LuffyEnemy>();

			e.chasePlayer = false;

			m_Minions.Add(new Minion
			{
				enemy = e,
				home = pos,
				baseRot = rot,
				yaw = 0f,
				speed = e.moveSpeed
			});
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

		// 在巢区域内随机取点：尽量和其他小怪保持 minSpawnSpacing 以上的间距
		Vector3 PickSpawnPosition()
		{
			Vector3 best = transform.position;
			float bestSpacing = -1f;

			for (int i = 0; i < 16; i++)
			{
				float angle = Random.Range(0f, Mathf.PI * 2f);
				float radius = spawnRadius * Mathf.Sqrt(Random.value);

				Vector3 pnt = transform.position + new Vector3(
					Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
				pnt = SnapToGround(pnt);

				float spacing = NearestMinionDistance(pnt);
				if (spacing >= minSpawnSpacing) return pnt;

				if (spacing > bestSpacing)
				{
					bestSpacing = spacing;
					best = pnt;
				}
			}

			return best;
		}

		// 候选点到场上已有小怪的最近水平距离
		float NearestMinionDistance(Vector3 point)
		{
			float nearest = float.MaxValue;

			for (int i = 0; i < m_Minions.Count; i++)
			{
				LuffyEnemy e = m_Minions[i].enemy;
				if (e == null) continue;

				float distance = HorizontalDistance(point, e.transform.position);
				if (distance < nearest) nearest = distance;
			}

			return nearest == float.MaxValue ? spawnRadius : nearest;
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

		// Boss 开战：小怪一边朝巢外走，一边溶解消失
		void BeginRetreat()
		{
			if (m_Retreating || m_Fled) return;
			StartCoroutine(RetreatRoutine());
		}

		IEnumerator RetreatRoutine()
		{
			m_Retreating = true;
			m_Fled = true;

			float duration = Mathf.Max(0.1f, fleeDuration);
			float speed = fleeDistance / duration;

			var enemies = new List<LuffyEnemy>();
			var directions = new List<Vector3>();

			for (int i = 0; i < m_Minions.Count; i++)
			{
				LuffyEnemy e = m_Minions[i].enemy;
				if (e == null || !e.IsAlive) continue;

				e.chasePlayer = false;
				e.HideOverheadBar();

				Vector3 dir = e.transform.position - transform.position;
				dir.y = 0f;
				if (dir.sqrMagnitude < 0.0001f)
				{
					dir = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
					if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
				}
				dir.Normalize();

				DissolveEffect effect = e.GetComponent<DissolveEffect>();
				if (effect == null) effect = e.gameObject.AddComponent<DissolveEffect>();
				effect.duration = duration;
				e.StartCoroutine(effect.Play());

				enemies.Add(e);
				directions.Add(dir);
			}

			m_Minions.Clear();

			float elapsed = 0f;
			while (elapsed < duration)
			{
				elapsed += Time.deltaTime;

				for (int i = 0; i < enemies.Count; i++)
				{
					if (enemies[i] == null) continue;
					enemies[i].transform.position += directions[i] * speed * Time.deltaTime;
				}

				yield return null;
			}

			for (int i = 0; i < enemies.Count; i++)
			{
				if (enemies[i] != null) Destroy(enemies[i].gameObject);
			}

			m_Retreating = false;
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
