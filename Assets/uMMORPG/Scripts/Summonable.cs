// summonable entity types that are bound to a player (pet, mount, ...)
using UnityEngine;
using Mirror;

public abstract class Summonable : Entity
{
    // 'Player' can't be SyncVar so we use [SyncVar] GameObject and wrap it
    [SyncVar] GameObject _owner;
    public Player owner
    {
        get { return _owner != null  ? _owner.GetComponent<Player>() : null; }
        set { _owner = value != null ? value.gameObject : null; }
    }
}
