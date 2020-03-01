using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Entitas.Utils;

namespace Entitas {

	/// A context manages the lifecycle of entities and groups.
	/// You can create and destroy entities and get groups of entities.
	/// The prefered way to create a context is to use the generated methods
	/// from the code generator, e.g. var context = new GameContext();
	public partial class Context : IContext
	{
		/// Occurs when an entity gets created.
		public event ContextEntityChanged OnEntityCreated;

		/// Occurs when an entity will be destroyed.
		public event ContextEntityChanged OnEntityWillBeDestroyed;

		/// Occurs when an entity got destroyed.
		public event ContextEntityChanged OnEntityDestroyed;

		/// Occurs when a group gets created for the first time.
		public event ContextGroupChanged OnGroupCreated;

		/// The total amount of components an entity can possibly have.
		/// This value is generated by the code generator,
		/// e.g ComponentLookup.TotalComponents.
		public int totalComponents { get { return _totalComponents; } }

		/// Returns all componentPools. componentPools is used to reuse
		/// removed components.
		/// Removed components will be pushed to the componentPool.
		/// Use entity.CreateComponent(index, type) to get a new or reusable
		/// component from the componentPool.
		//public Stack<IComponent>[] componentPools { get { return _componentPools; } }

		/// The contextInfo contains information about the context.
		/// It's used to provide better error messages.
		public ContextInfo contextInfo { get { return _contextInfo; } }

		/// Returns the number of entities in the context.
		public int count { get { return _entities.Count; } }

		/// Returns the number of entities in the internal ObjectPool
		/// for entities which can be reused.
		public int reusableEntitiesCount { get { return _reusableEntities.Count; } }

		/// Returns the number of entities that are currently retained by
		/// other objects (e.g. Group, Collector, ReactiveSystem).
		public int retainedEntitiesCount { get { return _retainedEntities.Count; } }

		readonly int _totalComponents;

		readonly IComponentPool[] _componentPools;
		readonly ContextInfo _contextInfo;
		readonly Func<IEntity, IAERC> _aercFactory;

		readonly HashSet<Entity> _entities = new HashSet<Entity>(EntityEqualityComparer.comparer);
		readonly Stack<Entity> _reusableEntities = new Stack<Entity>();
		readonly HashSet<Entity> _retainedEntities = new HashSet<Entity>(EntityEqualityComparer.comparer);

		readonly Dictionary<IMatcher, IGroup> _groups = new Dictionary<IMatcher, IGroup>();
		readonly List<IGroup>[] _groupsForIndex;
		readonly IGroup[] _groupForSingle;
		readonly ObjectPool<List<GroupChanged>> _groupChangedListPool;
		readonly Dictionary<int, Entity> _entitiesLookup = new Dictionary<int, Entity>();

		int _creationIndex;
		Entity[] _entitiesCache;

		// Cache delegates to avoid gc allocations
		EntityComponentChanged _cachedEntityChanged;
		EntityComponentReplaced _cachedComponentReplaced;
		EntityEvent _cachedEntityReleased;
		EntityEvent _cachedDestroyEntity;

		public Context(ContextInfo contextInfo, Func<IEntity, IAERC> aercFactory)
		{
			int totalComponents = contextInfo.GetComponentCount();

			_totalComponents = totalComponents;
			_creationIndex = 0;

			if (contextInfo != null)
			{
				_contextInfo = contextInfo;
				if (contextInfo.componentNames.Length != totalComponents)
				{
					throw new ContextInfoException(this, contextInfo);
				}
			}
			else
			{
				_contextInfo = createDefaultContextInfo();
			}

			_aercFactory = aercFactory == null
				? (entity) => new SafeAERC(entity)
				: aercFactory;

			_groupsForIndex = new List<IGroup>[totalComponents];
			_groupForSingle = new IGroup[totalComponents];
			_componentPools = new IComponentPool[totalComponents];

			_groupChangedListPool = new ObjectPool<List<GroupChanged>>(
										() => new List<GroupChanged>(),
										list => list.Clear()

									);

			// Cache delegates to avoid gc allocations
			_cachedEntityChanged = updateGroupsComponentAddedOrRemoved;
			_cachedComponentReplaced = updateGroupsComponentReplaced;
			_cachedEntityReleased = onEntityReleased;
			_cachedDestroyEntity = onDestroyEntity;

			// Add listener for updating lookup
			OnEntityCreated += (c, entity) => _entitiesLookup.Add(entity.creationIndex, (Entity)entity);
			OnEntityDestroyed += (c, entity) => _entitiesLookup.Remove(entity.creationIndex);
		}

		ContextInfo createDefaultContextInfo()
		{
			var componentNames = new string[_totalComponents];
			const string prefix = "Index ";
			for (int i = 0; i < componentNames.Length; i++)
			{
				componentNames[i] = prefix + i;
			}

			return new ContextInfo("Unnamed Context", componentNames, null);
		}

		/// Creates a new entity or gets a reusable entity from the
		/// internal ObjectPool for entities.
		public Entity CreateEntity()
		{
			Entity entity;

			Interlocked.Increment(ref _creationIndex);

			if (_reusableEntities.Count > 0)
			{
				entity = _reusableEntities.Pop();
				entity.Reactivate(_creationIndex);
			}
			else
			{
				entity = (Entity)Activator.CreateInstance(typeof(Entity));
				entity.Initialize(_creationIndex, _componentPools, _contextInfo, _aercFactory(entity));
			}

			_entities.Add(entity);
			entity.Retain(this);
			_entitiesCache = null;
			entity.OnComponentAdded += _cachedEntityChanged;
			entity.OnComponentRemoved += _cachedEntityChanged;
			entity.OnComponentReplaced += _cachedComponentReplaced;
			entity.OnEntityReleased += _cachedEntityReleased;
			entity.OnDestroyEntity += _cachedDestroyEntity;

			if (OnEntityCreated != null) {
				OnEntityCreated(this, entity);
			}

			return entity;
		}

		public Entity CreateEntity(string name)
		{
			Entity entity = CreateEntity();
			entity.name = name;
			return entity;
		}

		/// Destroys the entity, removes all its components and pushs it back
		/// to the internal ObjectPool for entities.
		private void DestroyEntity(Entity entity)
		{
			var removed = _entities.Remove(entity);
			if (!removed)
			{
				throw new ContextDoesNotContainEntityException(
					"'" + this + "' cannot destroy " + entity + "!",
					"This cannot happen!?!"
				);
			}
			_entitiesCache = null;

			if (OnEntityWillBeDestroyed != null)
			{
				OnEntityWillBeDestroyed(this, entity);
			}

			entity.InternalDestroy();

			if (OnEntityDestroyed != null)
			{
				OnEntityDestroyed(this, entity);
			}

			if (entity.retainCount == 1)
			{
				// Can be released immediately without
				// adding to _retainedEntities
				entity.OnEntityReleased -= _cachedEntityReleased;
				_reusableEntities.Push(entity);
				entity.Release(this);
				entity.RemoveAllOnEntityReleasedHandlers();
			}
			else
			{
				_retainedEntities.Add(entity);
				entity.Release(this);
			}
		}

		/// Destroys all entities in the context.
		/// Throws an exception if there are still retained entities.
		public void DestroyAllEntities()
		{
			var entities = GetEntities();
			for (int i = 0; i < entities.Length; i++)
			{
				entities[i].Destroy();
			}

			_entities.Clear();
			_entitiesLookup.Clear();

			if (_retainedEntities.Count != 0)
			{
				throw new ContextStillHasRetainedEntitiesException(this, _retainedEntities.ToArray());
			}
		}

		/// Determines whether the context has the specified entity.
		public bool HasEntity(Entity entity)
		{
			return _entities.Contains(entity);
		}

		/// Returns all entities which are currently in the context.
		public Entity[] GetEntities()
		{
			if (_entitiesCache == null)
			{
				_entitiesCache = new Entity[_entities.Count];
				_entities.CopyTo(_entitiesCache);
			}

			return _entitiesCache;
		}

		/// Returns a group for the specified matcher.
		/// Calling context.GetGroup(matcher) with the same matcher will always
		/// return the same instance of the group.
		public IGroup GetGroup(IMatcher matcher)
		{
			IGroup group;
			if (!_groups.TryGetValue(matcher, out group))
			{
				group = new Group(matcher);
				var entities = GetEntities();
				for (int i = 0; i < entities.Length; i++)
				{
					group.HandleEntitySilently(entities[i]);
				}
				_groups.Add(matcher, group);

				for (int i = 0; i < matcher.indices.Length; i++)
				{
					var index = matcher.indices[i];
					if (_groupsForIndex[index] == null)
					{
						_groupsForIndex[index] = new List<IGroup>();
					}
					_groupsForIndex[index].Add(group);
				}

				if (OnGroupCreated != null)
				{
					OnGroupCreated(this, group);
				}
			}

			return group;
		}

		/// Resets the creationIndex back to 0.
		internal void ResetCreationIndex()
		{
			_creationIndex = 0;
		}

		/*
		/// Clears the componentPool at the specified index.
		internal void ClearComponentPool(int index)
		{
			var componentPool = _componentPools[index];
			if (componentPool != null)
			{
				componentPool.Clear();
			}
		}

		/// Clears all componentPools.
		internal void ClearComponentPools()
		{
			for (int i = 0; i < _componentPools.Length; i++)
			{
				ClearComponentPool(i);
			}
		}
		*/

		/// Resets the context (destroys all entities and
		/// resets creationIndex back to 0).
		public void Reset()
		{
			DestroyAllEntities();
			ResetCreationIndex();

			OnEntityCreated = null;
			OnEntityWillBeDestroyed = null;
			OnEntityDestroyed = null;
			OnGroupCreated = null;
		}

		public override string ToString()
		{
			return _contextInfo.name;
		}

		void updateGroupsComponentAddedOrRemoved(IEntity entity, int index, IComponent component)
		{
			var groups = _groupsForIndex[index];
			if (groups != null)
			{
				var events = _groupChangedListPool.Get();

				var tEntity = (Entity)entity;

				for (int i = 0; i < groups.Count; i++)
				{
					events.Add(groups[i].HandleEntity(tEntity));
				}

				for (int i = 0; i < events.Count; i++)
				{
					var groupChangedEvent = events[i];
					if (groupChangedEvent != null)
					{
						groupChangedEvent(
							groups[i], tEntity, index, component
						);
					}
				}

				_groupChangedListPool.Push(events);
			}
		}

		void updateGroupsComponentReplaced(IEntity entity, int index, IComponent previousComponent, IComponent newComponent)
		{
			var groups = _groupsForIndex[index];
			if (groups != null)
			{

				var tEntity = (Entity)entity;

				for (int i = 0; i < groups.Count; i++)
				{
					groups[i].UpdateEntity(
						tEntity, index, previousComponent, newComponent
					);
				}
			}
		}

		void onEntityReleased(IEntity entity)
		{
			if (entity.isEnabled)
			{
				throw new EntityIsNotDestroyedException(
					"Cannot release " + entity + "!"
				);
			}
			var tEntity = (Entity)entity;
			entity.RemoveAllOnEntityReleasedHandlers();
			_retainedEntities.Remove(tEntity);
			_reusableEntities.Push(tEntity);
		}

		void onDestroyEntity(IEntity entity)
		{
			DestroyEntity((Entity)entity);
		}


		/// Returns entity matching the specified creationIndex
		public Entity GetEntity(int creationIndex)
		{
			if (_entitiesLookup == null)
				return null;

			Entity entity = null;
			_entitiesLookup.TryGetValue(creationIndex, out entity);

			return entity;
		}

		/// Returns unique entity with specified component
		public Entity GetSingleEntity<T>() where T : IComponent, IUnique {
			return GetSingleEntity(ComponentIndex<T>.Get());
		}

		public Entity GetSingleEntity(int componentIndex) {
			IGroup group = _groupForSingle[componentIndex];

			if (group == null) {
				group = GetGroup(Matcher.AllOf(componentIndex));
				_groupForSingle[componentIndex] = group;
			}

			return group.GetSingleEntity();
		}

		public T GetUnique<T>() where T : IComponent, IUnique {
			int componentIndex = ComponentIndex<T>.Get();

			IComponent component = GetUniqueComponent(componentIndex);
			if (component == null)
				return default(T);

			return (T)component;
		}

		private IComponent GetUniqueComponent(int componentIndex) {
			Entity entity = GetSingleEntity(componentIndex);
			if (entity == null)
				return null;
			
			return entity.GetComponent(componentIndex);
		}

		public T AddUnique<T>(bool useExisted = true) where T : IComponent, IUnique, new() {
			int componentIndex = ComponentIndex<T>.Get();

			Entity entity = GetSingleEntity(componentIndex);
			if (entity != null) {
				if (!useExisted)
					throw new EntityAlreadyHasComponentException(
					   componentIndex, "Cannot add component '" +
					   _contextInfo.componentNames[componentIndex] + "' to " + entity + "!",
					   "You should check if an entity already has the component."
					);
				return (T)ModifyUniqueComponent(componentIndex);
			}

			entity = CreateEntity(typeof(T).Name);
			var component = (T)entity.CreateComponent(componentIndex, typeof(T));
			entity.AddComponent(componentIndex, component);

			return component;
		}

		public T ModifyUnique<T>() where T : IComponent, IUnique {
			int componentIndex = ComponentIndex<T>.Get();

			IComponent component = ModifyUniqueComponent(componentIndex);
			if (component == null)
				return default(T);

			return (T)component;
		}

		private IComponent ModifyUniqueComponent(int componentIndex) {
			Entity entity = GetSingleEntity(componentIndex);
			if (entity == null)
				return null;

			return entity.ModifyComponent(componentIndex);
		}
	}
}
