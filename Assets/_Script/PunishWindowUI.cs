using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows the current punish window as a draining fill bar — purely a tuning/debug aid so
/// you can actually see the window's timing instead of guessing from animation alone.
/// Polls every frame rather than event-driven, since this is a continuous countdown display,
/// not a discrete on-change value like the health/energy bars.
/// </summary>
public class PunishWindowUI : MonoBehaviour
{
    [SerializeField] private ComboController combo;
    [SerializeField] private Image fillImage;
    [Tooltip("Parent object toggled on/off based on whether the window is currently open. Leave empty to just always show the (empty) bar.")]
    [SerializeField] private GameObject container;

    private void Update()
    {
        if (combo == null) return;

        bool show = combo.InPunishWindow;
        if (container != null) container.SetActive(show);
        if (fillImage != null) fillImage.fillAmount = combo.PunishWindowFraction;
    }
}
