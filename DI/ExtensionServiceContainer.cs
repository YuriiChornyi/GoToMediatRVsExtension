using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VSIXExtention.Interfaces;
using VSIXExtention.Services;

namespace VSIXExtention.DI
{
    /// <summary>
    /// Enhanced service container with solution scoping support
    /// </summary>
    public class ExtensionServiceContainer : IDisposable
    {
        private readonly ConcurrentDictionary<Type, object> _services = new ConcurrentDictionary<Type, object>();
        private readonly ConcurrentDictionary<Type, Func<ExtensionServiceContainer, object>> _factories = new ConcurrentDictionary<Type, Func<ExtensionServiceContainer, object>>();
        private readonly ConcurrentDictionary<Type, ServiceLifetime> _lifetimes = new ConcurrentDictionary<Type, ServiceLifetime>();
        private readonly string _containerName;
        private bool _disposed = false;

        public ExtensionServiceContainer(string containerName = "Default")
        {
            _containerName = containerName;
        }

        /// <summary>
        /// Register a service factory with specified lifetime
        /// </summary>
        public void Register<TInterface, TImplementation>(
            Func<ExtensionServiceContainer, TImplementation> factory = null,
            ServiceLifetime lifetime = ServiceLifetime.Singleton)
            where TImplementation : class, TInterface
            where TInterface : class
        {
            if (factory == null)
            {
                // Default factory that tries to create instance using constructor
                factory = container => CreateInstance<TImplementation>(container);
            }

            _factories.TryAdd(typeof(TInterface), container => factory(container));
            _lifetimes.TryAdd(typeof(TInterface), lifetime);

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Registered {typeof(TInterface).Name} -> {typeof(TImplementation).Name} ({lifetime})");
        }

        /// <summary>
        /// Register a singleton service factory
        /// </summary>
        public void RegisterSingleton<TInterface, TImplementation>(Func<ExtensionServiceContainer, TImplementation> factory = null)
            where TImplementation : class, TInterface
            where TInterface : class
        {
            Register<TInterface, TImplementation>(factory, ServiceLifetime.Singleton);
        }

        /// <summary>
        /// Register a transient service factory
        /// </summary>
        public void RegisterTransient<TInterface, TImplementation>(Func<ExtensionServiceContainer, TImplementation> factory = null)
            where TImplementation : class, TInterface
            where TInterface : class
        {
            Register<TInterface, TImplementation>(factory, ServiceLifetime.Transient);
        }

        /// <summary>
        /// Register a solution-scoped service factory
        /// </summary>
        public void RegisterSolutionScoped<TInterface, TImplementation>(Func<ExtensionServiceContainer, TImplementation> factory = null)
            where TImplementation : class, TInterface
            where TInterface : class
        {
            Register<TInterface, TImplementation>(factory, ServiceLifetime.SolutionScoped);
        }

        /// <summary>
        /// Register an instance as a singleton
        /// </summary>
        public void RegisterInstance<TInterface>(TInterface instance) where TInterface : class
        {
            _services.TryAdd(typeof(TInterface), instance);
            _lifetimes.TryAdd(typeof(TInterface), ServiceLifetime.Singleton);

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Registered instance {typeof(TInterface).Name}");
        }

        /// <summary>
        /// Get a service instance
        /// </summary>
        public TInterface GetService<TInterface>() where TInterface : class
        {
            return (TInterface)GetService(typeof(TInterface));
        }

        /// <summary>
        /// Try to get a service instance, returns null if not found
        /// </summary>
        public TInterface TryGetService<TInterface>() where TInterface : class
        {
            try
            {
                return GetService<TInterface>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get a service instance by type
        /// </summary>
        public object GetService(Type serviceType)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ExtensionServiceContainer));

            if (!_lifetimes.TryGetValue(serviceType, out var lifetime))
            {
                lifetime = ServiceLifetime.Singleton;
            }

            switch (lifetime)
            {
                case ServiceLifetime.Transient:
                    // Always create new instance
                    if (_factories.TryGetValue(serviceType, out var transientFactory))
                    {
                        try
                        {
                            var transientInstance = transientFactory(this);
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Created transient {serviceType.Name}");
                            return transientInstance;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"MediatRNavigationExtension: Failed to create transient service {serviceType.Name}: {ex.Message}", ex);
                        }
                    }
                    break;

                case ServiceLifetime.Singleton:
                case ServiceLifetime.SolutionScoped:
                    // Check for existing instance first
                    if (_services.TryGetValue(serviceType, out var existingInstance))
                    {
                        return existingInstance;
                    }

                    // Create and cache
                    if (_factories.TryGetValue(serviceType, out var factory))
                    {
                        try
                        {
                            var instance = factory(this);
                            _services.TryAdd(serviceType, instance);
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Created and cached {serviceType.Name}");
                            return instance;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"MediatRNavigationExtension: Failed to create service {serviceType.Name}: {ex.Message}", ex);
                        }
                    }
                    break;
            }

            throw new InvalidOperationException($"Service of type {serviceType.Name} is not registered in container '{_containerName}'. Available services: {string.Join(", ", _factories.Keys.Select(k => k.Name))}");
        }

        /// <summary>
        /// Check if a service is registered
        /// </summary>
        public bool IsRegistered<TInterface>()
        {
            return IsRegistered(typeof(TInterface));
        }

        /// <summary>
        /// Check if a service type is registered
        /// </summary>
        public bool IsRegistered(Type serviceType)
        {
            return _services.ContainsKey(serviceType) || _factories.ContainsKey(serviceType);
        }

        /// <summary>
        /// Clear only solution-scoped services
        /// </summary>
        public void ClearSolutionScopedServices()
        {
            var solutionScopedServices = _services
                .Where(kvp => _lifetimes.TryGetValue(kvp.Key, out var lifetime) ? lifetime == ServiceLifetime.SolutionScoped : false)
                .ToList();

            foreach (var kvp in solutionScopedServices)
            {
                if (kvp.Value is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Disposed solution-scoped service {kvp.Key.Name}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Error disposing solution-scoped service {kvp.Key.Name}: {ex.Message}");
                    }
                }
                _services.TryRemove(kvp.Key, out _);
            }

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Cleared {solutionScopedServices.Count} solution-scoped services");
        }

        /// <summary>
        /// Get all registered service types
        /// </summary>
        public IEnumerable<Type> GetRegisteredServiceTypes()
        {
            return _factories.Keys.Union(_services.Keys);
        }

        /// <summary>
        /// Helper method to register MediatR services with their dependencies
        /// </summary>
        public void RegisterMediatRServices(IServiceProvider vsServiceProvider)
        {
            // Register VS service provider first
            RegisterInstance<IServiceProvider>(vsServiceProvider);

            // Core services - these are global/singleton and don't depend on solution-scoped services
            RegisterSingleton<IMediatRContextService, MediatRContext>(container => new MediatRContext());
            RegisterSingleton<IMediatRHandlerFinder, MediatRHandlerFinder>(container => new MediatRHandlerFinder());
            RegisterSingleton<IMediatRNavigationService, MediatRNavigationService>(container =>
            {
                var serviceProvider = container.GetService<IServiceProvider>();
                var uiService = container.GetService<INavigationUIService>();
                return new MediatRNavigationService(serviceProvider, uiService);
            });
            RegisterSingleton<INavigationUIService, NavigationUI>(container => new NavigationUI());

            // Main orchestrator - remove IWorkspaceService dependency (it will get it from ServiceLocator when needed)
            RegisterSingleton<IMediatRCommandHandler, MediatRCommandHandler>(container =>
            {
                var contextService = container.GetService<IMediatRContextService>();
                var handlerFinder = container.GetService<IMediatRHandlerFinder>();
                var navigationService = container.GetService<IMediatRNavigationService>();
                var uiService = container.GetService<INavigationUIService>();

                // Don't inject IWorkspaceService directly - let the command handler get it from ServiceLocator
                return new MediatRCommandHandler(contextService, handlerFinder, navigationService, uiService);
            });

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: MediatR services registered successfully");
        }

        /// <summary>
        /// Register solution-scoped services
        /// </summary>
        public void RegisterSolutionServices()
        {
            // Example solution-scoped services
            RegisterSolutionScoped<IWorkspaceService, WorkspaceService>();
            RegisterSolutionScoped<IMediatRCacheService, MediatRCacheService>();
            RegisterSolutionScoped<IDocumentEventService, DocumentEventsService>();

            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainerNew[{_containerName}]: Solution services registered successfully");
        }

        /// <summary>
        /// Create instance using reflection and dependency injection
        /// </summary>
        private T CreateInstance<T>(ExtensionServiceContainer container)
        {
            var type = typeof(T);
            var constructors = type.GetConstructors();

            if (constructors.Length == 0)
            {
                throw new InvalidOperationException($"No public constructors found for {type.Name}");
            }

            // Try to find constructor with most parameters that we can satisfy
            var constructor = constructors
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault(c => CanSatisfyConstructor(c, container));

            if (constructor == null)
            {
                throw new InvalidOperationException($"No suitable constructor found for {type.Name}. Available constructors require dependencies that are not registered.");
            }

            var parameters = constructor.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                try
                {
                    args[i] = container.GetService(parameters[i].ParameterType);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to resolve dependency {parameters[i].ParameterType.Name} for {type.Name}: {ex.Message}",
                        ex);
                }
            }

            return (T)Activator.CreateInstance(type, args);
        }

        /// <summary>
        /// Check if we can satisfy all constructor parameters
        /// </summary>
        private bool CanSatisfyConstructor(ConstructorInfo constructor, ExtensionServiceContainer container)
        {
            var parameters = constructor.GetParameters();
            return parameters.All(p => container.IsRegistered(p.ParameterType));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Disposing container with {_services.Count} services");

                foreach (var service in _services.Values)
                {
                    if (service is IDisposable disposable)
                    {
                        try
                        {
                            disposable.Dispose();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Error disposing service {service.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                _services.Clear();
                _factories.Clear();
                _lifetimes.Clear();
                _disposed = true;

                System.Diagnostics.Debug.WriteLine($"MediatRNavigationExtension: ServiceContainer[{_containerName}]: Container disposed");
            }
        }
    }

    /// <summary>
    /// Enhanced service locator with hierarchical container support
    /// </summary>
    public static class ServiceLocator
    {
        private static ExtensionServiceContainer _globalContainer;
        private static ExtensionServiceContainer _solutionContainer;
        private static readonly object _lock = new object();

        /// <summary>
        /// Initialize the global service container
        /// </summary>
        public static void Initialize(ExtensionServiceContainer globalContainer)
        {
            lock (_lock)
            {
                _globalContainer?.Dispose();
                _globalContainer = globalContainer;
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: ServiceLocator: Global container initialized");
            }
        }

        /// <summary>
        /// Initialize the solution-scoped service container
        /// </summary>
        public static void InitializeSolutionContainer(ExtensionServiceContainer solutionContainer)
        {
            lock (_lock)
            {
                _solutionContainer?.Dispose();
                _solutionContainer = solutionContainer;
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: ServiceLocator: Solution container initialized");
            }
        }

        /// <summary>
        /// Get service with hierarchical lookup (solution first, then global)
        /// </summary>
        public static TInterface GetService<TInterface>() where TInterface : class
        {
            lock (_lock)
            {
                // Try solution container first
                if (_solutionContainer?.IsRegistered<TInterface>() == true)
                {
                    return _solutionContainer.GetService<TInterface>();
                }

                // Fall back to global container
                if (_globalContainer?.IsRegistered<TInterface>() == true)
                {
                    return _globalContainer.GetService<TInterface>();
                }

                throw new InvalidOperationException($"Service {typeof(TInterface).Name} not found in any container. Available global services: {GetAvailableServiceNames(_globalContainer)}, Available solution services: {GetAvailableServiceNames(_solutionContainer)}");
            }
        }

        /// <summary>
        /// Try to get service, returns null if not found
        /// </summary>
        public static TInterface TryGetService<TInterface>() where TInterface : class
        {
            try
            {
                return GetService<TInterface>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get service from global container only
        /// </summary>
        public static TInterface GetGlobalService<TInterface>() where TInterface : class
        {
            lock (_lock)
            {
                if (_globalContainer == null)
                    throw new InvalidOperationException("Global ServiceLocator is not initialized. Call Initialize() first.");

                return _globalContainer.GetService<TInterface>();
            }
        }

        /// <summary>
        /// Get service from solution container only
        /// </summary>
        public static TInterface GetSolutionService<TInterface>() where TInterface : class
        {
            lock (_lock)
            {
                if (_solutionContainer == null)
                    throw new InvalidOperationException("Solution container is not initialized or no solution is open.");

                return _solutionContainer.GetService<TInterface>();
            }
        }

        /// <summary>
        /// Check if service is available in any container
        /// </summary>
        public static bool IsServiceAvailable<TInterface>()
        {
            lock (_lock)
            {
                return (_solutionContainer?.IsRegistered<TInterface>() == true) ||
                       (_globalContainer?.IsRegistered<TInterface>() == true);
            }
        }

        /// <summary>
        /// Clear solution-scoped services
        /// </summary>
        public static void ClearSolutionServices()
        {
            lock (_lock)
            {
                if (_solutionContainer != null)
                {
                    _solutionContainer.Dispose();
                    _solutionContainer = null;
                    System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: ServiceLocator: Solution services cleared");
                }
            }
        }

        /// <summary>
        /// Dispose all containers
        /// </summary>
        public static void Dispose()
        {
            lock (_lock)
            {
                _solutionContainer?.Dispose();
                _globalContainer?.Dispose();
                _solutionContainer = null;
                _globalContainer = null;
                System.Diagnostics.Debug.WriteLine("MediatRNavigationExtension: ServiceLocator: All containers disposed");
            }
        }

        private static string GetAvailableServiceNames(ExtensionServiceContainer container)
        {
            if (container == null) return "none";

            try
            {
                return string.Join(", ", container.GetRegisteredServiceTypes().Select(t => t.Name));
            }
            catch
            {
                return "error retrieving services";
            }
        }
    }

    /// <summary>
    /// Service lifetime enumeration
    /// </summary>
    public enum ServiceLifetime
    {
        /// <summary>
        /// Single instance for the entire application lifetime
        /// </summary>
        Singleton,

        /// <summary>
        /// New instance created each time the service is requested
        /// </summary>
        Transient,

        /// <summary>
        /// Single instance per solution scope
        /// </summary>
        SolutionScoped
    }
}