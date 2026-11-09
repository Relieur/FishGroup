using UnityEngine;

// 单面素材（草、灌木等）绕 Y 轴始终正面朝向相机
[DisallowMultipleComponent]
public class BillboardY : MonoBehaviour
{
	[Tooltip("朝向修正角度（度）；素材背面朝相机时填 180")]
	public float yawOffset = 0f;

	Transform m_Camera;

	void LateUpdate()
	{
		if (m_Camera == null)
		{
			Camera cam = Camera.main;
			if (cam == null) return;
			m_Camera = cam.transform;
		}

		Vector3 toCamera = m_Camera.position - transform.position;
		toCamera.y = 0f;
		if (toCamera.sqrMagnitude < 0.0001f) return;

		// 单面素材的可见面一般是 -Z，所以让 +Z 背对相机再按 yawOffset 修正
		Quaternion look = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
		transform.rotation = look * Quaternion.Euler(0f, yawOffset, 0f);
	}
}
