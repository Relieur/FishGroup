using System.Collections.Generic;
using UnityEngine;

namespace VRTest.Environment
{
	// 怪物巢穴：固定位置刷新 3~5 只怪，模型随机不重复
	// 玩家靠近吸引仇恨；玩家离开范围后整窝复活
	//
	// 刻意不修改 LuffyEnemy，只通过它已有的公开字段（chasePlayer / moveSpeed）
	// 控制警戒分级，朝向逻辑完全交给原版处理。
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

		[Tooltip("警戒状态下的移动速度倍率（相对仇恨时的全速）")]
		[Range(0.1f, 1f)] public float alertSpeedScale = 0.45f;

		[Header("模型朝向")]
		[Tooltip("生成初始旋转，必须与场景 YG_01 一致：(270, 270, 0)。不要随机化，否则会破坏 LuffyEnemy 的姿态基准")]
		public Vector3 spawnEuler = new Vector3(270f, 270f, 0f);

		[Header("调试")]
		public bool drawGizmos = true;

		public enum NestState { Idle, Alert, Combat }
		public NestState State => m_State;

		// 运行时
		readonly List<LuffyEnemy> m_Enemies = new List<LuffyEnemy>();
		readonly List<GameObject> m_ShufflePool = new List<GameObject>();
		readonly List<float> m_BaseSpeed = new List<float>();

		NestState m_State = NestState.Idle;
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

			// 根据距离切换状态：仇恨 > 警戒 > 游荡
			if (dist <= aggroRadius) SetState(NestState.Combat);
			else if (dist <= alertRadius) SetState(NestState.Alert);
			else SetState(NestState.Idle);

			// 玩家走远后计时复活
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

		// 通过 LuffyEnemy 已有的公开字段实现警戒分级，不改它的代码
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
			// 初始为游荡状态
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

			// 统一用 YG_01 的朝向，不随机旋转
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
				if (fresh != null)
				{
					m_Enemies[i] = fresh;
					// 复活后按当前状态立即设置
					fresh.chasePlayer = (m_State != NestState.Idle);
					if (m_State == NestState.Alert) SetSpeed(i, fresh, alertSpeedScale);
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