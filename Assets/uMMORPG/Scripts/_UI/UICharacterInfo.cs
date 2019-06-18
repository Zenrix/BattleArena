// Note: this script has to be on an always-active UI parent, so that we can
// always react to the hotkey.
using UnityEngine;
using UnityEngine.UI;

public partial class UICharacterInfo : MonoBehaviour
{
    public KeyCode hotKey = KeyCode.T;
    public GameObject panel;
    public Text damageText;
    public Text healthText;
    public Text manaText;
    public Text speedText;
    public Text levelText;

    void Update()
    {
        Player player = Player.localPlayer;
        if (player)
        {
            // hotkey (not while typing in chat, etc.)
            if (Input.GetKeyDown(hotKey) && !UIUtils.AnyInputActive())
                panel.SetActive(!panel.activeSelf);

            // only refresh the panel while it's active
            if (panel.activeSelf)
            {
                damageText.text = player.damage.ToString();
                healthText.text = player.healthMax.ToString();
                manaText.text = player.manaMax.ToString();
                speedText.text = player.speed.ToString();
                levelText.text = player.level.ToString();
            }
        }
        else panel.SetActive(false);
    }
}
