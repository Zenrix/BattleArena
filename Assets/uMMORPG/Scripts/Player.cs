// All player logic was put into this class. We could also split it into several
// smaller components, but this would result in many GetComponent calls and a
// more complex syntax.
//
// The default Player class takes care of the basic player logic like the state
// machine and some properties like damage.
//
// The class also takes care of selection handling, which detects 3D world
// clicks and then targets/navigates somewhere/interacts with someone.
//
// Animations are not handled by the NetworkAnimator because it's still very
// buggy and because it can't really react to movement stops fast enough, which
// results in moonwalking. Not synchronizing animations over the network will
// also save us bandwidth
using UnityEngine;
using Mirror;
using System;
using System.Linq;
using System.Collections.Generic;
using TMPro;

[Serializable]
public partial struct SkillbarEntry
{
    public string reference;
    public KeyCode hotKey;
}

[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(PlayerChat))]
[RequireComponent(typeof(NetworkName))]
public partial class Player : Entity
{
    [Header("Components")]
    public PlayerChat chat;
    public Camera avatarCamera;
    public NetworkNavMeshAgentRubberbanding rubberbanding;

    [Header("Text Meshes")]
    public TextMeshPro nameOverlay;
    public Color nameOverlayDefaultColor = Color.white;
    public Color nameOverlayFriendlyTeam = Color.blue;
    public Color nameOverlayEnemyTeam = Color.red;

    [Header("Icons")]
    public Sprite classIcon; // for character selection
    public Sprite portraitIcon; // for top left portrait

    // some meta info
    [HideInInspector] public string account = "";
    [HideInInspector] public string className = "";

    // localPlayer singleton for easier access from UI scripts etc.
    public static Player localPlayer;

    // health
    public override int healthMax
    {
        get
        {
            return base.healthMax;
        }
    }

    // mana
    public override int manaMax
    {
        get
        {
            return base.manaMax;
        }
    }

    // damage
    public override int damage
    {
        get
        {
            return base.damage;
        }
    }

    // speed
    public override float speed
    {
        get
        {
            // mount speed if mounted, regular speed otherwise
            return activeMount != null && activeMount.health > 0 ? activeMount.speed : base.speed;
        }
    }

    [Header("Indicator")]
    public GameObject indicatorPrefab;
    [HideInInspector] public GameObject indicator;

    [Header("Inventory")]
    public int inventorySize = 30;
    public KeyCode[] inventorySplitKeys = {KeyCode.LeftShift, KeyCode.RightShift};

    [Header("Skillbar")]
    public SkillbarEntry[] skillbar = {
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha1},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha2},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha3},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha4},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha5},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha6},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha7},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha8},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha9},
        new SkillbarEntry{reference="", hotKey=KeyCode.Alpha0},
    };

    [Header("Interaction")]
    public float interactionRange = 4;
    public KeyCode targetNearestKey = KeyCode.Tab;
    public bool localPlayerClickThrough = true; // click selection goes through localplayer. feels best.
    public KeyCode cancelActionKey = KeyCode.Escape;

    [Header("PvP")]
    public BuffSkill offenderBuff;
    public BuffSkill murdererBuff;

    // 'Pet' can't be SyncVar so we use [SyncVar] GameObject and wrap it
    [Header("Pet")]
    [SyncVar] GameObject _activePet;
    public Pet activePet
    {
        get { return _activePet != null  ? _activePet.GetComponent<Pet>() : null; }
        set { _activePet = value != null ? value.gameObject : null; }
    }

    // pet's destination should always be right next to player, not inside him
    // -> we use a helper property so we don't have to recalculate it each time
    // -> we offset the position by exactly 1 x bounds to the left because dogs
    //    are usually trained to walk on the left of the owner. looks natural.
    public Vector3 petDestination
    {
        get
        {
            Bounds bounds = collider.bounds;
            return transform.position - transform.right * bounds.size.x;
        }
    }

    [Header("Mount")]
    public Transform meshToOffsetWhenMounted;
    public float seatOffsetY = -1;

    // 'Mount' can't be SyncVar so we use [SyncVar] GameObject and wrap it
    [SyncVar] GameObject _activeMount;
    public Mount activeMount
    {
        get { return _activeMount != null  ? _activeMount.GetComponent<Mount>() : null; }
        set { _activeMount = value != null ? value.gameObject : null; }
    }

    // when moving into attack range of a target, we always want to move a
    // little bit closer than necessary to tolerate for latency and other
    // situations where the target might have moved away a little bit already.
    [Header("Movement")]
    [Range(0.1f, 1)] public float attackToMoveRangeRatio = 0.8f;

    [Header("Death")]
    public float deathExperienceLossPercent = 0.05f;

    // some commands should have delays to avoid DDOS, too much database usage
    // or brute forcing coupons etc. we use one riskyAction timer for all.
    [SyncVar, HideInInspector] public double nextRiskyActionTime = 0; // double for long term precision

    // the next target to be set if we try to set it while casting
    // 'Entity' can't be SyncVar and NetworkIdentity causes errors when null,
    // so we use [SyncVar] GameObject and wrap it for simplicity
    [SyncVar] GameObject _nextTarget;
    public Entity nextTarget
    {
        get { return _nextTarget != null  ? _nextTarget.GetComponent<Entity>() : null; }
        set { _nextTarget = value != null ? value.gameObject : null; }
    }

    // cache players to save lots of computations
    // (otherwise we'd have to iterate NetworkServer.objects all the time)
    // => on server: all online players
    // => on client: all observed players
    public static Dictionary<string, Player> onlinePlayers = new Dictionary<string, Player>();

    // first allowed logout time after combat
    public double allowedLogoutTime => lastCombatTime + ((NetworkManagerMMO)NetworkManager.singleton).combatLogoutDelay;
    public double remainingLogoutTime => NetworkTime.time < allowedLogoutTime ? (allowedLogoutTime - NetworkTime.time) : 0;

    // helper variable to remember which skill to use when we walked close enough
    int useSkillWhenCloser = -1;

    // cached SkinnedMeshRenderer bones without equipment, by name
    Dictionary<string, Transform> skinBones = new Dictionary<string, Transform>();

    // networkbehaviour ////////////////////////////////////////////////////////
    protected override void Awake()
    {
        // cache base components
        base.Awake();

        // cache all default SkinnedMeshRenderer bones without equipment
        // (we might have multiple SkinnedMeshRenderers e.g. on feet, legs, etc.
        //  so we need GetComponentsInChildren)
        foreach (SkinnedMeshRenderer skin in GetComponentsInChildren<SkinnedMeshRenderer>())
            foreach (Transform bone in skin.bones)
                skinBones[bone.name] = bone;

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "Awake_");
    }

    public override void OnStartLocalPlayer()
    {
        // set singleton
        localPlayer = this;

        // setup camera targets
        Camera.main.GetComponent<CameraMMO>().target = transform;
        GameObject.FindWithTag("MinimapCamera").GetComponent<CopyPosition>().target = transform;
        if (avatarCamera) avatarCamera.enabled = true; // avatar camera for local player

        // load skillbar after player data was loaded
        LoadSkillbar();

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnStartLocalPlayer_");
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
    }

    public override void OnStartServer()
    {
        base.OnStartServer();

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnStartServer_");
    }

    protected override void Start()
    {
        // do nothing if not spawned (=for character selection previews)
        if (!isServer && !isClient) return;

        base.Start();
        onlinePlayers[name] = this;

        // spawn effects for any buffs that might still be active after loading
        // (OnStartServer is too early)
        // note: no need to do that in Entity.Start because we don't load them
        //       with previously casted skills
        if (isServer)
            for (int i = 0; i < buffs.Count; ++i)
                if (buffs[i].BuffTimeRemaining() > 0)
                    buffs[i].data.SpawnEffect(this, this);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "Start_");
    }

    void LateUpdate()
    {
        // pass parameters to animation state machine
        // => passing the states directly is the most reliable way to avoid all
        //    kinds of glitches like movement sliding, attack twitching, etc.
        // => make sure to import all looping animations like idle/run/attack
        //    with 'loop time' enabled, otherwise the client might only play it
        //    once
        // => MOVING state is set to local IsMovement result directly. otherwise
        //    we would see animation latencies for rubberband movement if we
        //    have to wait for MOVING state to be received from the server
        // => MOVING checks if !CASTING because there is a case in UpdateMOVING
        //    -> SkillRequest where we still slide to the final position (which
        //    is good), but we should show the casting animation then.
        // => skill names are assumed to be boolean parameters in animator
        //    so we don't need to worry about an animation number etc.
        if (isClient) // no need for animations on the server
        {
            // now pass parameters after any possible rebinds
            foreach (Animator anim in GetComponentsInChildren<Animator>())
            {
                anim.SetBool("MOVING", IsMoving() && state != "CASTING" && !IsMounted());
                anim.SetBool("CASTING", state == "CASTING");
                anim.SetBool("STUNNED", state == "STUNNED");
                anim.SetBool("MOUNTED", IsMounted()); // for seated animation
                anim.SetBool("DEAD", state == "DEAD");
                foreach (Skill skill in skills)
                    if (skill.level > 0 && !(skill.data is PassiveSkill))
                        anim.SetBool(skill.name, skill.CastTimeRemaining() > 0);
            }
        }

        // follow mount's seat position if mounted
        // (on server too, for correct collider position and calculations)
        ApplyMountSeatOffset();

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "LateUpdate_");
    }

    void OnDestroy()
    {
        // do nothing if not spawned (=for character selection previews)
        if (!isServer && !isClient) return;

        if (isLocalPlayer) // requires at least Unity 5.5.1 bugfix to work
        {
            Destroy(indicator);
            SaveSkillbar();
            localPlayer = null;
        }

        onlinePlayers.Remove(name);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnDestroy_");
    }

    // finite state machine events /////////////////////////////////////////////
    bool EventDied()
    {
        return health == 0;
    }

    bool EventTargetDisappeared()
    {
        return target == null;
    }

    bool EventTargetDied()
    {
        return target != null && target.health == 0;
    }

    bool EventSkillRequest()
    {
        return 0 <= currentSkill && currentSkill < skills.Count;
    }

    bool EventSkillFinished()
    {
        return 0 <= currentSkill && currentSkill < skills.Count &&
               skills[currentSkill].CastTimeRemaining() == 0;
    }

    bool EventMoveStart()
    {
        return state != "MOVING" && IsMoving(); // only fire when started moving
    }

    bool EventMoveEnd()
    {
        return state == "MOVING" && !IsMoving(); // only fire when stopped moving
    }

    bool EventStunned()
    {
        return NetworkTime.time <= stunTimeEnd;
    }

    [Command]
    public void CmdRespawn() { respawnRequested = true; }
    bool respawnRequested;
    bool EventRespawn()
    {
        bool result = respawnRequested;
        respawnRequested = false; // reset
        return result;
    }

    [Command]
    public void CmdCancelAction() { cancelActionRequested = true; }
    bool cancelActionRequested;
    bool EventCancelAction()
    {
        bool result = cancelActionRequested;
        cancelActionRequested = false; // reset
        return result;
    }

    // finite state machine - server ///////////////////////////////////////////
    [Server]
    string UpdateServer_IDLE()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died.
            OnDeath();
            return "DEAD";
        }
        if (EventStunned())
        {
            rubberbanding.ResetMovement();
            return "STUNNED";
        }
        if (EventCancelAction())
        {
            // the only thing that we can cancel is the target
            target = null;
            return "IDLE";
        }
        if (EventMoveStart())
        {
            // cancel casting (if any)
            currentSkill = -1;
            return "MOVING";
        }
        if (EventSkillRequest())
        {
            // don't cast while mounted
            // (no MOUNTED state because we'd need MOUNTED_STUNNED, etc. too)
            if (!IsMounted())
            {
                // user wants to cast a skill.
                // check self (alive, mana, weapon etc.) and target and distance
                Skill skill = skills[currentSkill];
                nextTarget = target; // return to this one after any corrections by CastCheckTarget
                Vector3 destination;
                if (CastCheckSelf(skill) && CastCheckTarget(skill) && CastCheckDistance(skill, out destination))
                {
                    // start casting and cancel movement in any case
                    // (player might move into attack range * 0.8 but as soon as we
                    //  are close enough to cast, we fully commit to the cast.)
                    rubberbanding.ResetMovement();
                    StartCastSkill(skill);
                    return "CASTING";
                }
                else
                {
                    // checks failed. stop trying to cast.
                    currentSkill = -1;
                    nextTarget = null; // nevermind, clear again (otherwise it's shown in UITarget)
                    return "IDLE";
                }
            }
        }
        if (EventSkillFinished()) {} // don't care
        if (EventMoveEnd()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care

        return "IDLE"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_MOVING()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died.
            OnDeath();
            return "DEAD";
        }
        if (EventStunned())
        {
            rubberbanding.ResetMovement();
            return "STUNNED";
        }
        if (EventMoveEnd())
        {
            // finished moving. do whatever we did before.
            return "IDLE";
        }
        if (EventCancelAction())
        {
            // cancel casting (if any) and stop moving
            currentSkill = -1;
            //rubberbanding.ResetMovement(); <- done locally. doing it here would reset localplayer to the slightly behind server position otherwise
            return "IDLE";
        }
        // SPECIAL CASE: Skill Request while doing rubberband movement
        // -> we don't really need to react to it
        // -> we could just wait for move to end, then react to request in IDLE
        // -> BUT player position on server always lags behind in rubberband movement
        // -> SO there would be a noticeable delay before we start to cast
        //
        // SOLUTION:
        // -> start casting as soon as we are in range
        // -> BUT don't ResetMovement. instead let it slide to the final position
        //    while already starting to cast
        // -> NavMeshAgentRubberbanding won't accept new positions while casting
        //    anyway, so this is fine
        if (EventSkillRequest())
        {
            // don't cast while mounted
            // (no MOUNTED state because we'd need MOUNTED_STUNNED, etc. too)
            if (!IsMounted())
            {
                Vector3 destination;
                Skill skill = skills[currentSkill];
                if (CastCheckSelf(skill) && CastCheckTarget(skill) && CastCheckDistance(skill, out destination))
                {
                    //Debug.Log("MOVING->EventSkillRequest: early cast started while sliding to destination...");
                    // rubberbanding.ResetMovement(); <- DO NOT DO THIS.
                    StartCastSkill(skill);
                    return "CASTING";
                }
            }
        }
        if (EventMoveStart()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care

        return "MOVING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_CASTING()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died.
            OnDeath();
            return "DEAD";
        }
        if (EventStunned())
        {
            currentSkill = -1;
            rubberbanding.ResetMovement();
            return "STUNNED";
        }
        if (EventMoveStart())
        {
            // we do NOT cancel the cast if the player moved, and here is why:
            // * local player might move into cast range and then try to cast.
            // * server then receives the Cmd, goes to CASTING state, then
            //   receives one of the last movement updates from the local player
            //   which would cause EventMoveStart and cancel the cast.
            // * this is the price for rubberband movement.
            // => if the player wants to cast and got close enough, then we have
            //    to fully commit to it. there is no more way out except via
            //    cancel action. any movement in here is to be rejected.
            //    (many popular MMOs have the same behaviour too)
            //
            // we do NOT reset movement either. allow sliding to final position.
            // (NavMeshAgentRubberbanding doesn't accept new ones while CASTING)
            //rubberbanding.ResetMovement(); <- DO NOT DO THIS

            // Not sure what's going on here, but since we likely have no targeted
            // skills, the previous comment is moot. Will be cancelling cast for now.
            // Keeping previous comment temporarily.

            currentSkill = -1;

            return "CASTING";
        }
        if (EventCancelAction())
        {
            // cancel casting
            currentSkill = -1;
            return "IDLE";
        }
        if (EventTargetDisappeared())
        {
            // cancel if the target matters for this skill
            if (skills[currentSkill].cancelCastIfTargetDied)
            {
                currentSkill = -1;
                return "IDLE";
            }
        }
        if (EventTargetDied())
        {
            // cancel if the target matters for this skill
            if (skills[currentSkill].cancelCastIfTargetDied)
            {
                currentSkill = -1;
                return "IDLE";
            }
        }
        if (EventSkillFinished())
        {
            // apply the skill after casting is finished
            // note: we don't check the distance again. it's more fun if players
            //       still cast the skill if the target ran a few steps away
            Skill skill = skills[currentSkill];

            // apply the skill on the target
            FinishCastSkill(skill);

            // clear current skill for now
            currentSkill = -1;

            // target-based skill and no more valid target? then clear
            // (otherwise IDLE will get an unnecessary skill request and mess
            //  with targeting)
            bool validTarget = target != null && target.health > 0;
            if (currentSkill != -1 && skills[currentSkill].cancelCastIfTargetDied && !validTarget)
                currentSkill = -1;

            // go back to IDLE
            return "IDLE";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventRespawn()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "CASTING"; // nothing interesting happened
    }

    [Server]
    string UpdateServer_STUNNED()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventDied())
        {
            // we died.
            OnDeath();
            return "DEAD";
        }
        if (EventStunned())
        {
            return "STUNNED";
        }

        // go back to idle if we aren't stunned anymore and process all new
        // events there too
        return "IDLE";
    }

    [Server]
    string UpdateServer_DEAD()
    {
        // events sorted by priority (e.g. target doesn't matter if we died)
        if (EventRespawn())
        {
            // revive to closest spawn, with 50% health, then go to idle
            Transform start = NetworkManagerMMO.GetNearestStartPosition(transform.position);
            agent.Warp(start.position); // recommended over transform.position
            Revive(0.5f);
            return "IDLE";
        }
        if (EventMoveStart())
        {
            // this should never happen, rubberband should prevent from moving
            // while dead.
            Debug.LogWarning("Player " + name + " moved while dead. This should not happen.");
            return "DEAD";
        }
        if (EventMoveEnd()) {} // don't care
        if (EventSkillFinished()) {} // don't care
        if (EventDied()) {} // don't care
        if (EventCancelAction()) {} // don't care
        if (EventTargetDisappeared()) {} // don't care
        if (EventTargetDied()) {} // don't care
        if (EventSkillRequest()) {} // don't care

        return "DEAD"; // nothing interesting happened
    }

    [Server]
    protected override string UpdateServer()
    {
        if (state == "IDLE")     return UpdateServer_IDLE();
        if (state == "MOVING")   return UpdateServer_MOVING();
        if (state == "CASTING")  return UpdateServer_CASTING();
        if (state == "STUNNED")  return UpdateServer_STUNNED();
        if (state == "DEAD")     return UpdateServer_DEAD();
        Debug.LogError("invalid state:" + state);
        return "IDLE";
    }

    // finite state machine - client ///////////////////////////////////////////
    [Client]
    protected override void UpdateClient()
    {
        if (state == "IDLE" || state == "MOVING")
        {
            if (isLocalPlayer)
            {
                // simply accept input
                SelectionHandling();
                WASDHandling();
                TargetNearest();

                // cancel action if escape key was pressed
                if (Input.GetKeyDown(cancelActionKey))
                {
                    agent.ResetPath(); // reset locally because we use rubberband movement
                    CmdCancelAction();
                }

                // trying to cast a skill on a monster that wasn't in range?
                // then check if we walked into attack range by now
                if (useSkillWhenCloser != -1)
                {
                    // can we still attack the target? maybe it was switched.
                    if (CanAttack(target))
                    {
                        // in range already?
                        // -> we don't use CastCheckDistance because we want to
                        // move a bit closer (attackToMoveRangeRatio)
                        float range = skills[useSkillWhenCloser].castRange * attackToMoveRangeRatio;
                        if (Utils.ClosestDistance(collider, target.collider) <= range)
                        {
                            // then stop moving and start attacking
                            CmdUseSkill(useSkillWhenCloser);

                            // reset
                            useSkillWhenCloser = -1;
                        }
                        // otherwise keep walking there. the target might move
                        // around or run away, so we need to keep adjusting the
                        // destination all the time
                        else
                        {
                            //Debug.Log("walking closer to target...");
                            agent.stoppingDistance = range;
                            agent.destination = target.collider.ClosestPoint(transform.position);
                        }
                    }
                    // otherwise reset
                    else useSkillWhenCloser = -1;
                }
            }
        }
        else if (state == "CASTING")
        {
            // keep looking at the target for server & clients (only Y rotation)
            if (target) LookAtY(target.transform.position);

            if (isLocalPlayer)
            {
                // simply accept input and reset any client sided movement
                SelectionHandling();
                WASDHandling(); // still call this to set pendingVelocity for after cast
                TargetNearest();
                agent.ResetMovement();

                // cancel action if escape key was pressed
                if (Input.GetKeyDown(cancelActionKey)) CmdCancelAction();
            }
        }
        else if (state == "STUNNED")
        {
            if (isLocalPlayer)
            {
                // simply accept input and reset any client sided movement
                SelectionHandling();
                TargetNearest();
                agent.ResetMovement();

                // cancel action if escape key was pressed
                if (Input.GetKeyDown(cancelActionKey)) CmdCancelAction();
            }
        }
        else if (state == "DEAD") {}
        else Debug.LogError("invalid state:" + state);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "UpdateClient_");
    }

    // overlays ////////////////////////////////////////////////////////////////
    protected override void UpdateOverlays()
    {
        base.UpdateOverlays();

        if (nameOverlay != null)
        {
            // only players need to copy names to name overlay. it never changes
            // for monsters / npcs.
            nameOverlay.text = name;

            // find local player (null while in character selection)
            if (localPlayer != null)
            {
                // TODO: Set player team color
            }
        }
    }

    // skill finished event & pending actions //////////////////////////////////
    // pending actions while casting. to be applied after cast.
    int pendingSkill = -1;
    Vector3 pendingDestination;
    bool pendingDestinationValid;
    Vector3 pendingVelocity;
    bool pendingVelocityValid;

    // client event when skill cast finished on server
    // -> useful for follow up attacks etc.
    //    (doing those on server won't really work because the target might have
    //     moved, in which case we need to follow, which we need to do on the
    //     client)
    [Client]
    void OnSkillCastFinished(Skill skill)
    {
        if (!isLocalPlayer) return;

        // tried to click move somewhere?
        if (pendingDestinationValid)
        {
            agent.stoppingDistance = 0;
            agent.destination = pendingDestination;
        }
        // tried to wasd move somewhere?
        else if (pendingVelocityValid)
        {
            agent.velocity = pendingVelocity;
        }
        // user pressed another skill button?
        else if (pendingSkill != -1)
        {
            TryUseSkill(pendingSkill, true);
        }
        // otherwise do follow up attack if no interruptions happened
        else if (skill.followupDefaultAttack)
        {
            TryUseSkill(0, true);
        }

        // clear pending actions in any case
        pendingSkill = -1;
        pendingDestinationValid = false;
        pendingVelocityValid = false;
    }

    [Server]
    public void OnDamageDealtToMonster(Monster monster)
    {

    }

    [Server]
    public void OnDamageDealtToPlayer(Player player)
    {
        // was he innocent?
        if (!player.IsOffender() && !player.IsMurderer())
        {
            // did we kill him? then start/reset murder status
            // did we just attack him? then start/reset offender status
            // (unless we are already a murderer)
            if (player.health == 0) StartMurderer();
            else if (!IsMurderer()) StartOffender();
        }
    }

    [Server]
    public void OnDamageDealtToPet(Pet pet)
    {
        // was he innocent?
        if (!pet.owner.IsOffender() && !pet.owner.IsMurderer())
        {
            // did we kill him? then start/reset murder status
            // did we just attack him? then start/reset offender status
            // (unless we are already a murderer)
            if (pet.health == 0) StartMurderer();
            else if (!IsMurderer()) StartOffender();
        }
    }

    // custom DealDamageAt function that also rewards experience if we killed
    // the monster
    [Server]
    public override void DealDamageAt(Entity entity, int amount, float stunChance=0, float stunTime=0)
    {
        // deal damage with the default function
        base.DealDamageAt(entity, amount, stunChance, stunTime);

        // a monster?
        if (entity is Monster)
        {
            OnDamageDealtToMonster((Monster)entity);
        }
        // a player?
        // (see murder code section comments to understand the system)
        else if (entity is Player)
        {
            OnDamageDealtToPlayer((Player)entity);
        }
        // a pet?
        // (see murder code section comments to understand the system)
        else if (entity is Pet)
        {
            OnDamageDealtToPet((Pet)entity);
        }

        // let pet know that we attacked something
        if (activePet != null && activePet.autoAttack)
            activePet.OnAggro(entity);

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "DealDamageAt_", entity, amount);
    }

    // aggro ///////////////////////////////////////////////////////////////////
    // this function is called by entities that attack us
    [ServerCallback]
    public override void OnAggro(Entity entity)
    {
        // forward to pet if it's supposed to defend us
        if (activePet != null && activePet.defendOwner)
            activePet.OnAggro(entity);
    }

    // death ///////////////////////////////////////////////////////////////////
    [Server]
    protected override void OnDeath()
    {
        // take care of entity stuff
        base.OnDeath();

        // rubberbanding needs a custom reset
        rubberbanding.ResetMovement();

        // addon system hooks
        Utils.InvokeMany(typeof(Player), this, "OnDeath_");
    }

    // loot ////////////////////////////////////////////////////////////////////
    [Command]
    public void CmdTakeLootGold()
    {
        // validate: dead monster and close enough?
        // use collider point(s) to also work with big entities
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            target != null && target is Monster && target.health == 0 &&
            Utils.ClosestDistance(collider, target.collider) <= interactionRange)
        {

            gold += target.gold;

            // reset target gold
            target.gold = 0;
        }
    }

    // inventory ///////////////////////////////////////////////////////////////
    // are inventory operations like swap, merge, split allowed at the moment?
    bool InventoryOperationsAllowed()
    {
        return state == "IDLE" ||
               state == "MOVING" ||
               state == "CASTING";
    }

    bool CanReplaceAllBones(SkinnedMeshRenderer equipmentSkin)
    {
        // are all equipment SkinnedMeshRenderer bones in the player bones?
        return equipmentSkin.bones.All(bone => skinBones.ContainsKey(bone.name));
    }

    // replace all equipment SkinnedMeshRenderer bones with the original player
    // bones so that the equipment animation works with IK too
    // (make sure to check CanReplaceAllBones before)
    void ReplaceAllBones(SkinnedMeshRenderer equipmentSkin)
    {
        // get equipment bones
        Transform[] bones = equipmentSkin.bones;

        // replace each one
        for (int i = 0; i < bones.Length; ++i)
        {
            string boneName = bones[i].name;
            if (!skinBones.TryGetValue(boneName, out bones[i]))
                Debug.LogWarning(equipmentSkin.name + " bone " + boneName + " not found in original player bones. Make sure to check CanReplaceAllBones before.");
        }

        // reassign bones
        equipmentSkin.bones = bones;
    }

    void RebindAnimators()
    {
        foreach (Animator anim in GetComponentsInChildren<Animator>())
            anim.Rebind();
    }

    // skills //////////////////////////////////////////////////////////////////
    // CanAttack check
    // we use 'is' instead of 'GetType' so that it works for inherited types too
    public override bool CanAttack(Entity entity)
    {
        return base.CanAttack(entity) &&
               (entity is Monster ||
                entity is Player ||
                (entity is Pet && entity != activePet) ||
                (entity is Mount && entity != activeMount));
    }

    [Command]
    public void CmdUseSkill(int skillIndex)
    {
        // validate
        if ((state == "IDLE" || state == "MOVING" || state == "CASTING") &&
            0 <= skillIndex && skillIndex < skills.Count)
        {
            // skill learned and can be casted?
            if (skills[skillIndex].level > 0 && skills[skillIndex].IsReady())
            {
                currentSkill = skillIndex;
            }
        }
    }

    // helper function: try to use a skill and walk into range if necessary
    [Client]
    public void TryUseSkill(int skillIndex, bool ignoreState=false)
    {
        // only if not casting already
        // (might need to ignore that when coming from pending skill where
        //  CASTING is still true)
        if (state != "CASTING" || ignoreState)
        {
            Skill skill = skills[skillIndex];
            if (CastCheckSelf(skill) && CastCheckTarget(skill))
            {
                // check distance between self and target
                Vector3 destination;
                if (CastCheckDistance(skill, out destination))
                {
                    // cast
                    CmdUseSkill(skillIndex);
                }
                else
                {
                    // move to the target first
                    // (use collider point(s) to also work with big entities)
                    agent.stoppingDistance = skill.castRange * attackToMoveRangeRatio;
                    agent.destination = destination;

                    // use skill when there
                    useSkillWhenCloser = skillIndex;
                }
            }
        }
        else
        {
            pendingSkill = skillIndex;
        }
    }

    public bool HasLearnedSkill(string skillName)
    {
        return skills.Any(skill => skill.name == skillName && skill.level > 0);
    }

    public bool HasLearnedSkillWithLevel(string skillName, int skillLevel)
    {
        return skills.Any(skill => skill.name == skillName && skill.level >= skillLevel);
    }

    // skillbar ////////////////////////////////////////////////////////////////
    //[Client] <- disabled while UNET OnDestroy isLocalPlayer bug exists
    void SaveSkillbar()
    {
        // save skillbar to player prefs (based on player name, so that
        // each character can have a different skillbar)
        for (int i = 0; i < skillbar.Length; ++i)
            PlayerPrefs.SetString(name + "_skillbar_" + i, skillbar[i].reference);

        // force saving playerprefs, otherwise they aren't saved for some reason
        PlayerPrefs.Save();
    }

    [Client]
    void LoadSkillbar()
    {
        print("loading skillbar for " + name);
        List<Skill> learned = skills.Where(skill => skill.level > 0).ToList();
        for (int i = 0; i < skillbar.Length; ++i)
        {
            // try loading an existing entry
            if (PlayerPrefs.HasKey(name + "_skillbar_" + i))
            {
                string entry = PlayerPrefs.GetString(name + "_skillbar_" + i, "");

                // is this a valid item/equipment/learned skill?
                // (might be an old character's playerprefs)
                // => only allow learned skills (in case it's an old character's
                //    skill that we also have, but haven't learned yet)
                if (HasLearnedSkill(entry))
                {
                    skillbar[i].reference = entry;
                }
            }
            // otherwise fill with default skills for a better first impression
            else if (i < learned.Count)
            {
                skillbar[i].reference = learned[i].name;
            }
        }
    }

    // pvp murder system ///////////////////////////////////////////////////////
    // attacking someone innocent results in Offender status
    //   (can be attacked without penalty for a short time)
    // killing someone innocent results in Murderer status
    //   (can be attacked without penalty for a long time + negative buffs)
    // attacking/killing a Offender/Murderer has no penalty
    //
    // we use buffs for the offender/status because buffs have all the features
    // that we need here.
    public bool IsOffender()
    {
        return offenderBuff != null && buffs.Any(buff => buff.name == offenderBuff.name);
    }

    public bool IsMurderer()
    {
        return murdererBuff != null && buffs.Any(buff => buff.name == murdererBuff.name);
    }

    public void StartOffender()
    {
        if (offenderBuff != null) AddOrRefreshBuff(new Buff(offenderBuff, 1));
    }

    public void StartMurderer()
    {
        if (murdererBuff != null) AddOrRefreshBuff(new Buff(murdererBuff, 1));
    }

    // pet /////////////////////////////////////////////////////////////////////
    [Command]
    public void CmdPetSetAutoAttack(bool value)
    {
        // validate
        if (activePet != null)
            activePet.autoAttack = value;
    }

    [Command]
    public void CmdPetSetDefendOwner(bool value)
    {
        // validate
        if (activePet != null)
            activePet.defendOwner = value;
    }

    // helper function for command and UI
    public bool CanUnsummonPet()
    {
        // only while pet and owner aren't fighting
        return activePet != null &&
               (          state == "IDLE" ||           state == "MOVING") &&
               (activePet.state == "IDLE" || activePet.state == "MOVING");
    }

    [Command]
    public void CmdPetUnsummon()
    {
        // validate
        if (CanUnsummonPet())
        {
            // destroy from world. item.summoned and activePet will be null.
            NetworkServer.Destroy(activePet.gameObject);
        }
    }

    // mounts //////////////////////////////////////////////////////////////////
    public bool IsMounted()
    {
        return activeMount != null && activeMount.health > 0;
    }

    void ApplyMountSeatOffset()
    {
        if (meshToOffsetWhenMounted != null)
        {
            // apply seat offset if on mount (not a dead one), reset otherwise
            if (activeMount != null && activeMount.health > 0)
                meshToOffsetWhenMounted.transform.position = activeMount.seat.position + Vector3.up * seatOffsetY;
            else
                meshToOffsetWhenMounted.transform.localPosition = Vector3.zero;
        }
    }

    // selection handling //////////////////////////////////////////////////////
    public void SetIndicatorViaParent(Transform parent)
    {
        if (!indicator) indicator = Instantiate(indicatorPrefab);
        indicator.transform.SetParent(parent, true);
        indicator.transform.position = parent.position;
    }

    public void SetIndicatorViaPosition(Vector3 position)
    {
        if (!indicator) indicator = Instantiate(indicatorPrefab);
        indicator.transform.parent = null;
        indicator.transform.position = position;
    }

    [Command]
    public void CmdSetTarget(NetworkIdentity ni)
    {
        // validate
        if (ni != null)
        {
            // can directly change it, or change it after casting?
            if (state == "IDLE" || state == "MOVING" || state == "STUNNED")
                target = ni.GetComponent<Entity>();
            else if (state == "CASTING")
                nextTarget = ni.GetComponent<Entity>();
        }
    }

    [Client]
    void SelectionHandling()
    {
        // click raycasting if not over a UI element & not pinching on mobile
        // note: this only works if the UI's CanvasGroup blocks Raycasts
        if (Input.GetMouseButtonDown(0) && !Utils.IsCursorOverUserInterface() && Input.touchCount <= 1)
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            // raycast with local player ignore option
            RaycastHit hit;
            bool cast = localPlayerClickThrough ? Utils.RaycastWithout(ray, out hit, gameObject) : Physics.Raycast(ray, out hit);
            if (cast)
            {
                // clear requested skill in any case because if we clicked
                // somewhere else then we don't care about it anymore
                useSkillWhenCloser = -1;

                // valid target?
                Entity entity = hit.transform.GetComponent<Entity>();
                if (entity)
                {
                    // set indicator
                    SetIndicatorViaParent(hit.transform);

                    // clicked last target again? and is not self or pet?
                    if (entity == target && entity != this && entity != activePet)
                    {
                        // attackable and has skills? => attack
                        if (CanAttack(entity) && skills.Count > 0)
                        {
                            // then try to use that one
                            TryUseSkill(0);
                        }
                        // monster, dead, has loot, close enough? => loot
                        // use collider point(s) to also work with big entities
                        else if (entity is Monster && entity.health == 0 &&
                                 Utils.ClosestDistance(collider, entity.collider) <= interactionRange &&
                                 ((Monster)entity).HasLoot())
                        {
                            UILoot.singleton.Show();
                        }
                        // not attackable, lootable, talkable, etc., but it's
                        // still an entity and double clicking it without doing
                        // anything would be strange.
                        // (e.g. if we are in a safe zone and click on a
                        //  monster. it's not attackable, but we should at least
                        //  move there, otherwise double click feels broken)
                        else
                        {
                            // use collider point(s) to also work with big entities
                            agent.stoppingDistance = interactionRange;
                            agent.destination = entity.collider.ClosestPoint(transform.position);
                        }

                        // addon system hooks
                        Utils.InvokeMany(typeof(Player), this, "OnSelect_", entity);
                    }
                    // clicked a new target
                    else
                    {
                        // target it
                        CmdSetTarget(entity.netIdentity);
                    }
                }
                // otherwise it's a movement target
                else
                {
                    // set indicator and navigate to the nearest walkable
                    // destination. this prevents twitching when destination is
                    // accidentally in a room without a door etc.
                    Vector3 bestDestination = agent.NearestValidDestination(hit.point);
                    SetIndicatorViaPosition(bestDestination);

                    // casting? then set pending destination
                    if (state == "CASTING")
                    {
                        pendingDestination = bestDestination;
                        pendingDestinationValid = true;
                    }
                    else
                    {
                        agent.stoppingDistance = 0;
                        agent.destination = bestDestination;
                    }
                }
            }
        }
    }

    [Client]
    void WASDHandling()
    {
        // don't move if currently typing in an input
        // we check this after checking h and v to save computations
        if (!UIUtils.AnyInputActive())
        {
            // get horizontal and vertical input
            // note: no != 0 check because it's 0 when we stop moving rapidly
            float horizontal = Input.GetAxis("Horizontal");
            float vertical = Input.GetAxis("Vertical");

            if (horizontal != 0 || vertical != 0)
            {
                // create input vector, normalize in case of diagonal movement
                Vector3 input = new Vector3(horizontal, 0, vertical);
                if (input.magnitude > 1) input = input.normalized;

                // get camera rotation without up/down angle, only left/right
                Vector3 angles = Camera.main.transform.rotation.eulerAngles;
                angles.x = 0;
                Quaternion rotation = Quaternion.Euler(angles); // back to quaternion

                // calculate input direction relative to camera rotation
                Vector3 direction = rotation * input;

                // draw direction for debugging
                Debug.DrawLine(transform.position, transform.position + direction, Color.green, 0, false);

                // clear indicator if there is one, and if it's not on a target
                // (simply looks better)
                if (direction != Vector3.zero && indicator != null && indicator.transform.parent == null)
                    Destroy(indicator);

                // cancel path if we are already doing click movement, otherwise
                // we will slide
                agent.ResetMovement();

                // casting? then set pending velocity
                if (state == "CASTING")
                {
                    pendingVelocity = direction * speed;
                    pendingVelocityValid = true;
                }
                else
                {
                    // set velocity
                    agent.velocity = direction * speed;

                    // moving with velocity doesn't look at the direction, do it manually
                    LookAtY(transform.position + direction);
                }

                // clear requested skill in any case because if we clicked
                // somewhere else then we don't care about it anymore
                useSkillWhenCloser = -1;
            }
        }
    }

    // simple tab targeting
    [Client]
    void TargetNearest()
    {
        if (Input.GetKeyDown(targetNearestKey))
        {
            // find all monsters that are alive, sort by distance
            GameObject[] objects = GameObject.FindGameObjectsWithTag("Monster");
            List<Monster> monsters = objects.Select(go => go.GetComponent<Monster>()).Where(m => m.health > 0).ToList();
            List<Monster> sorted = monsters.OrderBy(m => Vector3.Distance(transform.position, m.transform.position)).ToList();

            // target nearest one
            if (sorted.Count > 0)
            {
                SetIndicatorViaParent(sorted[0].transform);
                CmdSetTarget(sorted[0].netIdentity);
            }
        }
    }

    // ontrigger ///////////////////////////////////////////////////////////////
    protected override void OnTriggerEnter(Collider col)
    {
        // call base function too
        base.OnTriggerEnter(col);
    }

    void OnDragAndDrop_SkillsSlot_SkillbarSlot(int[] slotIndices)
    {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        skillbar[slotIndices[1]].reference = skills[slotIndices[0]].name; // just save it clientsided
    }

    void OnDragAndDrop_SkillbarSlot_SkillbarSlot(int[] slotIndices)
    {
        // slotIndices[0] = slotFrom; slotIndices[1] = slotTo
        // just swap them clientsided
        string temp = skillbar[slotIndices[0]].reference;
        skillbar[slotIndices[0]].reference = skillbar[slotIndices[1]].reference;
        skillbar[slotIndices[1]].reference = temp;
    }

    void OnDragAndClear_SkillbarSlot(int slotIndex)
    {
        skillbar[slotIndex].reference = "";
    }

    // validation //////////////////////////////////////////////////////////////
    void OnValidate()
    {
        // make sure that the NetworkNavMeshAgentRubberbanding component is
        // ABOVE the player component, so that it gets updated before Player.cs.
        // -> otherwise it overwrites player's WASD velocity for local player
        //    hosts
        // -> there might be away around it, but a warning is good for now
        Component[] components = GetComponents<Component>();
        if (Array.IndexOf(components, GetComponent<NetworkNavMeshAgentRubberbanding>()) >
            Array.IndexOf(components, this))
            Debug.LogWarning(name + "'s NetworkNavMeshAgentRubberbanding component is below the Player component. Please drag it above the Player component in the Inspector, otherwise there might be WASD movement issues due to the Update order.");
    }
}
