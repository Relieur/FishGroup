using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;

// 按下左摇杆在初速与加速之间切换
[AddComponentMenu("XR/Locomotion/Move Speed Toggle")]
public class MoveSpeedToggle : MonoBehaviour
{
	[Tooltip("按下摇杆后的移速倍数，再按一次恢复初速")]
	[SerializeField] float m_BoostMultiplier = 2f;

	InputAction m_ToggleAction;           // 移动手的摇杆按下（左手）
	ContinuousMoveProvider m_MoveProvider;
	float m_BaseMoveSpeed;
	bool m_Boosted;

	void Awake()
	{
		m_ToggleAction = new InputAction("SpeedToggle", InputActionType.Button, "<XRController>{LeftHand}/{Primary2DAxisClick}");
		m_ToggleAction.Enable();
	}

	void Start()
	{
		// 等连续移动组件初始化完再记录初速
		m_MoveProvider = FindFirstObjectByType<ContinuousMoveProvider>();
		if (m_MoveProvider != null)
			m_BaseMoveSpeed = m_MoveProvider.moveSpeed;
	}

	void Update()
	{
		if (m_MoveProvider == null || !m_ToggleAction.WasPressedThisFrame())
			return;

		m_Boosted = !m_Boosted;
		m_MoveProvider.moveSpeed = m_Boosted ? m_BaseMoveSpeed * m_BoostMultiplier : m_BaseMoveSpeed;
	}

	void OnDestroy()
	{
		if (m_ToggleAction == null) return;
		m_ToggleAction.Disable();
		m_ToggleAction.Dispose();
		m_ToggleAction = null;
	}
}
