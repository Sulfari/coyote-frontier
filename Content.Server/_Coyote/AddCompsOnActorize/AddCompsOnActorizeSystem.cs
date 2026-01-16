using Content.Server.Carrying;
using Content.Shared._Coyote.SniffAndSmell;
using Robust.Shared.Player;

namespace Content.Server._Coyote.AddCompsOnActorize;

/// <summary>
/// This is a hacky hardcoded mess designed to apply components when the entity is actorized
/// </summary>
public sealed class AddCompsOnActorizeSystem : EntitySystem
{
    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ActorComponent, ComponentStartup>(OnActorAdded);
    }

    private void OnActorAdded(EntityUid uid, ActorComponent component, ComponentStartup args)
    {
        // smell
        EnsureComp<SmellerComponent>(uid);
        // carriable
        EnsureComp<CarriableComponent>(uid);
    }
}
