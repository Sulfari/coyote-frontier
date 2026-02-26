using Robust.Shared.GameStates;

namespace Content.Shared.Item.Orbiter;

[RegisterComponent, NetworkedComponent]
public sealed partial class HeldItemOrbiterComponent : Component
{
    /// <summary>
    /// The prototype ID of the entity to spawn as the orbiting sprite.
    /// </summary>
    [DataField(required: true)]
    public string SpritePrototype = default!;

    /// <summary>
    /// Reference to the spawned orbiting entity, for cleanup.
    /// </summary>
    public EntityUid? OrbitingSprite;
}
