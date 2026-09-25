using UnityEngine;

namespace CompositeCollider3D
{
    /// <summary>
    /// Selects the Boolean operation for a Collider on this GameObject.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class CompositeColliderSource3D : MonoBehaviour
    {
        [SerializeField] private CompositeCollider3D.BooleanOperation _operation;

        public CompositeCollider3D.BooleanOperation Operation => _operation;
    }
}
