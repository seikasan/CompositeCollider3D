using UnityEngine;

namespace CompositeCollider3D
{
    /// <summary>
    /// One ordered input for a CompositeCollider3D.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class CompositeColliderSource3D : MonoBehaviour
    {
        [SerializeField, HideInInspector] private Collider _collider;
        [SerializeField] private CompositeCollider3D.BooleanOperation _operation;

        public Collider SourceCollider =>
            _collider != null
                ? _collider
                : GetComponent<Collider>();
        public CompositeCollider3D.BooleanOperation Operation => _operation;

        private void Reset()
        {
            _collider = GetComponent<Collider>();
        }
    }
}
