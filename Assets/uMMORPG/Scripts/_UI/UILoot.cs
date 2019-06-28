// Note: this script has to be on an always-active UI parent, so that we can
// always find it from other code. (GameObject.Find doesn't find inactive ones)
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public partial class UILoot : MonoBehaviour
{
    public static UILoot singleton;
    public GameObject panel;
    public GameObject goldSlot;
    public Text goldText;
    public UILootSlot itemSlotPrefab;
    public Transform content;

    public UILoot()
    {
        // assign singleton only once (to work with DontDestroyOnLoad when
        // using Zones / switching scenes)
        if (singleton == null) singleton = this;
    }

    void Update()
    {
        Player player = Player.localPlayer;

        // use collider point(s) to also work with big entities
        if (player != null &&
            panel.activeSelf &&
            player.target != null &&
            player.target.health == 0 &&
            Utils.ClosestDistance(player.collider, player.target.collider) <= player.interactionRange &&
            player.target is Monster &&
            ((Monster)player.target).HasLoot())
        {
            // gold slot
            if (player.target.gold > 0)
            {
                goldSlot.SetActive(true);
                goldSlot.GetComponentInChildren<Button>().onClick.SetListener(() => {
                    player.CmdTakeLootGold();
                });
                goldText.text = player.target.gold.ToString();
            }
            else goldSlot.SetActive(false);
        }
        else panel.SetActive(false);
    }

    public void Show() { panel.SetActive(true); }
}
