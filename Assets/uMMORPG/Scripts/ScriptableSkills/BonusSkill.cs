// Base type for bonus skill templates.
// => can be used for passive skills, buffs, etc.
using System.Text;
using UnityEngine;
using Mirror;

public abstract class BonusSkill : ScriptableSkill
{
    public int bonusHealthMax;
    public int bonusManaMax;
    public int bonusDamage;
    public int bonusDefense;
    public float bonusBlockChance; // range [0,1]
    public float bonusCriticalChance; // range [0,1]
    public float bonusHealthPercentPerSecond; // 0.1=10%; can be negative too
    public float bonusManaPercentPerSecond; // 0.1=10%; can be negative too
    public float bonusSpeed; // can be negative too

    // tooltip
    public override string ToolTip(int skillLevel, bool showRequirements = false)
    {
        StringBuilder tip = new StringBuilder(base.ToolTip(skillLevel, showRequirements));
        tip.Replace("{BONUSHEALTHMAX}", bonusHealthMax.ToString());
        tip.Replace("{BONUSMANAMAX}", bonusManaMax.ToString());
        tip.Replace("{BONUSDAMAGE}", bonusDamage.ToString());
        tip.Replace("{BONUSDEFENSE}", bonusDefense.ToString());
        tip.Replace("{BONUSBLOCKCHANCE}", Mathf.RoundToInt(bonusBlockChance * 100).ToString());
        tip.Replace("{BONUSCRITICALCHANCE}", Mathf.RoundToInt(bonusCriticalChance * 100).ToString());
        tip.Replace("{BONUSHEALTHPERCENTPERSECOND}", Mathf.RoundToInt(bonusHealthPercentPerSecond * 100).ToString());
        tip.Replace("{BONUSMANAPERCENTPERSECOND}", Mathf.RoundToInt(bonusManaPercentPerSecond * 100).ToString());
        tip.Replace("{BONUSSPEED}", bonusSpeed.ToString("F2"));
        return tip.ToString();
    }
}
