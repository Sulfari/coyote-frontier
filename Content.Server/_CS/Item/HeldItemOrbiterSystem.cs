using Content.Shared.Follower;
using Content.Shared.Hands;
using Content.Shared.Item.Orbiter;

namespace Content.Server.Item.Orbiter;

//There's 100% a much better and more elegant way to do this but I cannot find it.
public sealed class HeldItemOrbiterSystem : EntitySystem
{
    [Dependency] private readonly FollowerSystem _follower = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HeldItemOrbiterComponent, GotEquippedHandEvent>(OnEquipped);
        SubscribeLocalEvent<HeldItemOrbiterComponent, GotUnequippedHandEvent>(OnUnequipped);
    }

    private void OnEquipped(Entity<HeldItemOrbiterComponent> ent, ref GotEquippedHandEvent args)
    {
        if (!args.User.IsValid()) //This errored once, perhaps due to the fact I deleted the user. So here we are.
            return;
        var sprite = _entMan.SpawnEntity(ent.Comp.SpritePrototype, Transform(ent).Coordinates);
        ent.Comp.OrbitingSprite = sprite;
        _follower.StartFollowingEntity(sprite, args.User);
    }

    private void OnUnequipped(Entity<HeldItemOrbiterComponent> ent, ref GotUnequippedHandEvent args)
    {
        if (ent.Comp.OrbitingSprite.HasValue)
        {
            var sprite = ent.Comp.OrbitingSprite.Value;
            _entMan.DeleteEntity(sprite);
            ent.Comp.OrbitingSprite = null;
        }
    }
}
