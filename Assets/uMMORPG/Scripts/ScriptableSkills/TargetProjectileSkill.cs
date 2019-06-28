using UnityEngine;
using Mirror;

[CreateAssetMenu(menuName="uMMORPG Skill/Target Projectile", order=999)]
public class TargetProjectileSkill : DamageSkill
{
    [Header("Projectile")]
    public ProjectileSkillEffect projectile; // Arrows, Bullets, Fireballs, ...

    public override bool CheckSelf(Entity caster, int skillLevel)
    {
        // check base and ammo
        return base.CheckSelf(caster, skillLevel);
    }

    public override bool CheckTarget(Entity caster)
    {
        // target exists, alive, not self, oktype?
        return caster.target != null && caster.CanAttack(caster.target);
    }

    public override bool CheckDistance(Entity caster, int skillLevel, out Vector3 destination)
    {
        // target still around?
        if (caster.target != null)
        {
            destination = caster.target.collider.ClosestPoint(caster.transform.position);
            return Utils.ClosestDistance(caster.collider, caster.target.collider) <= castRange;
        }
        destination = caster.transform.position;
        return false;
    }

    public override void Apply(Entity caster, int skillLevel)
    {

        // spawn the skill effect. this can be used for anything ranging from
        // blood splatter to arrows to chain lightning.
        // -> we need to call an RPC anyway, it doesn't make much of a diff-
        //    erence if we use NetworkServer.Spawn for everything.
        // -> we try to spawn it at the weapon's projectile mount
        if (projectile != null)
        {
            GameObject go = Instantiate(projectile.gameObject, caster.effectMount.position, caster.effectMount.rotation);
            ProjectileSkillEffect effect = go.GetComponent<ProjectileSkillEffect>();
            effect.target = caster.target;
            effect.caster = caster;
            effect.damage = damage;
            effect.stunChance = stunChance;
            effect.stunTime = stunTime;
            NetworkServer.Spawn(go);
        }
        else Debug.LogWarning(name + ": missing projectile");
    }
}
