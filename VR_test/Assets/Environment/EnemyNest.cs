using System.Collections.Generic;
using UnityEngine;

namespace VRTest.Environment
{
	// 怪物巢穴：固定位置刷新 3~5 只怪，模型随机不重复；玩家离开后整窝复活
	// 刻意不修改 LuffyEnemy，只负责生成/统计/复活
	[AddComponentMenu("VR/Environment/Enemy Nest")]
	public class EnemyNest : MonoBehaviour
	{
		[Header("刷新")]
		[Tooltip("怪物预制体候选列表（YG_02/03/04/05），YG_01 是白模不要用")]
		public GameObject[] enemyPrefabs;

		[Min(1)] public int minCount = 3;
		[Min(1)] public int maxCount = 5;

		[Tooltip("怪全部死亡后，玩家离开多久整窝复活（秒）")]
		[Min(0f)] public float respawnDelay = 5f;

		[Header("范围")]
		public float spawnRadius = 3.5f;
		public float leaveRadius = 14f;
		public float groundRayHeight = 12f;
		public LayerMask groundMask = ~0;

		[Header("模型朝向")]
		[Tooltip("生成初始旋转，必须与场景 YG_01 一致：(270, 270, 0)")]
		public Vector3 spawnEuler = new Vector3(270f, 270f, 0f);

		[Header("调试")]
		public bool drawGizmos = true;

		readonly List<LuffyEnemy> m_Enemies = new List<LuffyEnemy>();
		readonly List<GameObject> m_ShufflePool = new List<GameObject>();
		float m_LeaveTimer;
		int m_TargetCount;
		Transform m_Player;

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
			ResolvePlayer();
			if (m_Player == null) return;

			float dist = HorizontalDistance(m_Player.position, transform.position);

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
			if (prefab == null) return null;

			Vector3 pos = PickSpawnPosition(index, m_TargetCount);

			// 统一用 YG_01 的朝向，不随机旋转：LuffyEnemy 会把初始姿态当基准
			Quaternion rot = Quaternion.Euler(spawnEuler);

			var go = Instantiate(prefab, pos, rot);
			go.name = prefab.name + "_Nest" + (index + 1);

			// YG 的 FBX 带水平偏移，清掉让怪落在巢穴位置
			if (go.transform.childCount > 0)
			{
				var child = go.transform.GetChild(0);
				Vector3 lp = child.localPosition;
				child.localPosition = new Vector3(0f, lp.y, 0f);
			}

			var e = go.GetComponent<LuffyEnemy>();
			if (e == null) e = go.AddComponent<LuffyEnemy>();
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
				if (fresh != null) m_Enemies[i] = fresh;
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