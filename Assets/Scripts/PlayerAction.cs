/// <summary>Things the player does that other systems (sound, behaviour recorder) want to react to.</summary>
public enum PlayerAction
{
    Jump,
    Land,          // value = fall speed just before touching down
    Dash,
    Slash,
    Overhead,
    AirAttack,
    Teleport,
    Damage,
    Death,
    HitBoss,
    HitDecoration
}
