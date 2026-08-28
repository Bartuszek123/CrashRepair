using Game.Prefabs;
using Unity.Entities;

namespace CrashRepair
{
    /// <summary>
    /// The single detection rule for a broken prefab reference. A reference is
    /// "missing" when the target entity is gone entirely, or is an obsolete
    /// placeholder created by the game's ResolvePrefabsSystem for prefabs
    /// absent from the current session (PrefabData disabled, negative index,
    /// no managed PrefabBase behind it).
    /// </summary>
    internal static class MissingPrefabDetector
    {
        /// <summary>True for a live entity that carries prefab data (real or placeholder).</summary>
        public static bool IsPrefabEntity(EntityManager entityManager, Entity entity)
        {
            return entity != Entity.Null && entityManager.HasComponent<PrefabData>(entity);
        }

        public static bool IsMissing(EntityManager entityManager, PrefabSystem prefabSystem, Entity prefab)
        {
            if (!IsPrefabEntity(entityManager, prefab))
                return true;
            if (entityManager.IsComponentEnabled<PrefabData>(prefab))
                return false;
            if (entityManager.GetComponentData<PrefabData>(prefab).m_Index >= 0)
                return false;
            // Cross-check with the managed prefab registry: a real prefab always
            // has a PrefabBase; obsolete placeholders never do.
            return !prefabSystem.TryGetPrefab(prefab, out PrefabBase _);
        }
    }
}
