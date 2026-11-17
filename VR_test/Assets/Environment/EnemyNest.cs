using System.Collections.Generic;
using UnityEngine;

namespace VRTest.Environment
{
	// 怪物巢穴：在固定位置刷新 3~5 只怪，成群游荡
	// 玩家靠近吸引仇恨；玩家离开范围 5 秒后整窝复活
	[AddComponentMenu("VR/Environment/Enemy Nest")]
	public class EnemyNest : MonoBehaviour
	{
		[Header("刷新")]
		[Tooltip("怪物预制体候选列表（YG_02/03/04/05）。注意 YG_01 是白模，不要用。每只怪从列表里随机挑一个")]
		public GameObject[] enemyPrefabs;

		[Tooltip("兼容旧配置：列表为空时回退用这个（建议留空）")]
		public GameObject enemyPrefab;

		[Tooltip("这一窝最少几只")]
		[Min(1)] public int minCount = 3;

		[Tooltip("这一窝最多几只")]
		[Min(1)] public int maxCount = 5;

		[Tooltip("所有怪死亡后，玩家离开多久整窝复活（秒）")]
		[Min(0f)] public float respawnDelay = 5f;

		[Header("范围")]
		[Tooltip("刷新散布半径（米），怪会在这个圈里出生")]
		public float spawnRadius = 3.5f;

		[Tooltip("玩家进入这个半径就吸引仇恨（米）")]
		public float aggroRadius = 9f;

		[Tooltip("玩家离开这个半径，开始计时复活（米）")]
		public float leaveRadius = 14f;

		[Tooltip("贴地射线起点高度（米）")]
		public float groundRayHeight = 12f;

		[Tooltip("地面所在的层")]
		public LayerMask groundMask = ~0;

		[Header("调试")]
		[Tooltip("在 Scene 视图画出各范围圈")]
		public bool drawGizmos = true;

		[Header("模型朝向")]
		[Tooltip("模型的基础欧拉角，与场景里的 YG_01 保持一致：实测 eulerAngles = (270, 270, 0)")]
		public Vector3 baseEuler = new Vector3(270f, 270f, 0f);

		[Tooltip("勾选后，生成时把模型内部偏移清掉，让怪精确落在巢穴位置")]
		public bool correctModelOffset = true;

		public enum NestState { Idle, Combat, Cooldown }

		// 运行时
		readonly List<LuffyEnemy> m_Enemies = new List<LuffyEnemy>();
		NestState m_State = NestState.Idle;
		float m_LeaveTimer;
		int m_TargetCount;
		Transform m_Player;

		public NestState State => m_State;
		public int AliveCount
		{
			get
			{
				int n = 0;
				for (int i = 0; i < m_Enemies.Count; i++)
					if (m_Enemies[i] != null && m_Enemies[i].IsAlive) n++;
				return n;
			}
		}

		void Start()
		{
			ResolvePlayer();

			bool hasList = enemyPrefabs != null && enemyPrefabs.Length > 0;
			if (!hasList && enemyPrefab == null)
			{
				Debug.LogWarning("[Nest] " + name + " 没有指定 enemyPrefabs", this);
				return;
			}

			m_TargetCount = Random.Range(
				Mathf.Max(1, minCount),
				Mathf.Max(Mathf.Max(1, minCount), maxCount) + 1);

			SpawnAll();
		}

		void Update()
		{
			ResolvePlayer();
			if (m_Player == null) return;

			float dist = HorizontalDistance(m_Player.position, transform.position);

			switch (m_State)
			{
				case NestState.Idle:
				case NestState.Cooldown:
					if (dist <= aggroRadius) EnterCombat();
					break;

				case NestState.Combat:
					if (dist > leaveRadius)
					{
						// 玩家走远，开始计时
						m_LeaveTimer += Time.deltaTime;
						if (m_LeaveTimer >= respawnDelay)
						{
							m_LeaveTimer = 0f;
							ReviveAll();
							m_State = NestState.Idle;
							SetAggro(false);
						}
					}
					else
					{
						m_LeaveTimer = 0f;
					}
					break;
			}
		}

		void EnterCombat()
		{
			if (m_State == NestState.Combat) return;
			m_State = NestState.Combat;
			m_LeaveTimer = 0f;
			SetAggro(true);
		}

		void SetAggro(bool value)
		{
			for (int i = 0; i < m_Enemies.Count; i++)
			{
				if (m_Enemies[i] != null) m_Enemies[i].Aggro = value;
			}
		}

		// 一次性生成整窝怪
		void SpawnAll()
		{
			for (int i = 0; i < m_TargetCount; i++)
			{
				var e = CreateEnemy(i);
				if (e != null) m_Enemies.Add(e);
			}
		}

		LuffyEnemy CreateEnemy(int index)
		{
			GameObject prefab = PickPrefab(index);
			if (prefab == null)
			{
				Debug.LogWarning("[Nest] 没有配置怪物预制体", this);
				return null;
			}

			Vector3 pos = PickSpawnPosition(index, m_TargetCount);

			// 用 baseEuler 保证模型立正；只在 Y 上叠随机朝向
			Quaternion rot = Quaternion.Euler(
				baseEuler.x,
				baseEuler.y + Random.Range(0f, 360f),
				baseEuler.z);

			var go = Instantiate(prefab, pos, rot);
			go.name = prefab.name + "_Nest" + (index + 1);

			// YG 的 FBX 在预制体里带了 z 偏移，直接实例化会偏离巢穴位置
			if (correctModelOffset && go.transform.childCount > 0)
			{
				var child = go.transform.GetChild(0);
				Vector3 p = child.localPosition;
				// 只清掉水平偏移，保留高度（模型可能靠 Y 贴地）
				child.localPosition = new Vector3(0f, p.y, 0f);
			}

			// YG_02~05 这些模型没有挂 LuffyEnemy，运行时补上
			var e = go.GetComponent<LuffyEnemy>();
			if (e == null) e = go.AddComponent<LuffyEnemy>();

			// 交给巢穴托管
			e.managedByNest = true;
			e.useNestSpawnPoint = true;
			e.HomePosition = transform.position;
			e.Aggro = m_State == NestState.Combat;

			return e;
		}

		// 从候选列表里挑一个模型；同一窝尽量不重复
		GameObject PickPrefab(int index)
		{
			if (enemyPrefabs != null && enemyPrefabs.Length > 0)
			{
				// 先按顺序取，超出后再随机，保证多样性
				if (index < enemyPrefabs.Length) return enemyPrefabs[index];
				return enemyPrefabs[Random.Range(0, enemyPrefabs.Length)];
			}
			return enemyPrefab;
		}

		// 在巢穴周围均匀撒点，避免所有怪挤在同一个位置
		Vector3 PickSpawnPosition(int index, int total)
		{
			float baseAngle = 360f / Mathf.Max(1, total) * index;
			float jitter = Random.Range(-18f, 18f);
			float rad = (baseAngle + jitter) * Mathf.Deg2Rad;

			// 内外两圈，避免全部落在同一半径上
			float r = spawnRadius * (index % 2 == 0 ? 0.55f : 1f);
			r *= Random.Range(0.85f, 1.15f);

			Vector3 p = transform.position + new Vector3(
				Mathf.Sin(rad) * r, 0f, Mathf.Cos(rad) * r);

			return SnapToGround(p);
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

		// 玩家离开足够久后，整窝恢复到满员
		void ReviveAll()
		{
			for (int i = 0; i < m_Enemies.Count; i++)
			{
				var e = m_Enemies[i];
				if (e == null) continue;
				if (!e.IsAlive)
				{
					e.TeleportTo(PickSpawnPosition(i, m_Enemies.Count));
					e.HomePosition = transform.position;
					e.Revive();
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

			Gizmos.color = new Color(1f, 0.35f, 0.25f, 0.9f);
			DrawCircle(transform.position, aggroRadius);

			Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.6f);
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
