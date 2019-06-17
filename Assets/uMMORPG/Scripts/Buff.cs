// Buffs are like Skills, but for the Buffs list.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Mirror;

[Serializable]
public partial struct Buff
{
    // hashcode used to reference the real ScriptableSkill (can't link to data
    // directly because synclist only supports simple types). and syncing a
    // string's hashcode instead of the string takes WAY less bandwidth.
    public int hash;

    // dynamic stats (cooldowns etc.)
    public int level;
    public double buffTimeEnd; // server time. double for long term precision.

    // constructors
    public Buff(BuffSkill data, int level)
    {
        hash = data.name.GetStableHashCode();
        this.level = level;
        buffTimeEnd = NetworkTime.time + data.buffTime; // start buff immediately
    }

    // wrappers for easier access
    public BuffSkill data
    {
        get
        {
            // show a useful error message if the key can't be found
            // note: ScriptableSkill.OnValidate 'is in resource folder' check
            //       causes Unity SendMessage warnings and false positives.
            //       this solution is a lot better.
            if (!ScriptableSkill.dict.ContainsKey(hash))
                throw new KeyNotFoundException("There is no ScriptableSkill with hash=" + hash + ". Make sure that all ScriptableSkills are in the Resources folder so they are loaded properly.");
            return (BuffSkill)ScriptableSkill.dict[hash];
        }
    }
    public string name => data.name;
    public Sprite image => data.image;
    public float buffTime => data.buffTime;
    public int bonusHealthMax => data.bonusHealthMax;
    public int bonusManaMax => data.bonusManaMax;
    public int bonusDamage => data.bonusDamage;
    public int bonusDefense => data.bonusDefense;
    public float bonusBlockChance => data.bonusBlockChance;
    public float bonusCriticalChance => data.bonusCriticalChance;
    public float bonusHealthPercentPerSecond => data.bonusHealthPercentPerSecond;
    public float bonusManaPercentPerSecond => data.bonusManaPercentPerSecond;
    public float bonusSpeed => data.bonusSpeed;
    public int maxLevel => data.maxLevel;

    // tooltip - runtime part
    public string ToolTip()
    {
        // we use a StringBuilder so that addons can modify tooltips later too
        // ('string' itself can't be passed as a mutable object)
        StringBuilder tip = new StringBuilder(data.ToolTip(level));

        // addon system hooks
        Utils.InvokeMany(typeof(Buff), this, "ToolTip_", tip);

        return tip.ToString();
    }

    public float BuffTimeRemaining()
    {
        // how much time remaining until the buff ends? (using server time)
        return NetworkTime.time >= buffTimeEnd ? 0 : (float)(buffTimeEnd - NetworkTime.time);
    }
}

public class SyncListBuff : SyncList<Buff> {}
