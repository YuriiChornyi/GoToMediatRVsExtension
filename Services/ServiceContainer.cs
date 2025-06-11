using System;
using System.Collections.Concurrent;
using System.Reflection;
using VSIXExtention.Interfaces;

namespace VSIXExtention.Services
{
    /// <summary>
    /// Simple service container for dependency injection
    /// </summary>
    public class ServiceContainer : IDisposable
    {
        private readonly ConcurrentDictionary<Type, object> _services = new ConcurrentDictionary<Type, object>();
        private readonly ConcurrentDictionary<Type, Func<ServiceContainer, object>> _factories = new ConcurrentDictionary<Type, Func<ServiceContainer, object>>();
        private bool _disposed = false;

        /// <summary>
        /// Register a service factory
        /// </summary>
        public void Register<TInterface, TImplementation>(Func<ServiceContainer, TImplementation> factory = null)
            where TImplementation : class, TInterface
            where TInterface : class
        {
            if (factory == null)
            {
                // Default factory that tries to create instance using constructor
                factory = container =>
                {
                    var constructors = typeof(TImplementation).GetConstructors();
                    if (constructors.Length == 0)
                    {
                        throw new InvalidOperationException($"No public constructors found for {typeof(TImplementation).Name}");
                    }

                    var constructor = constructors[0]; // Use first constructor
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
                                $"Failed to resolve dependency {parameters[i].ParameterType.Name} for {typeof(TImplementation).Name}: {ex.Message}", 
                                ex);
                        }
                    }
                    
                    return (TImplementation)Activator.CreateInstance(typeof(TImplementation), args);
                };
            }

            _factories.TryAdd(typeof(TInterface), container => factory(container));
        }

        /// <summary>
        /// Register a singleton service factory
        /// </summary>
        public void RegisterSingleton<TInterface, TImplementation>(Func<ServiceContainer, TImplementation> factory = null)
            where TImplementation : class, TInterface
            where TInterface : class
        {
            Register<TInterface, TImplementation>(factory);
        }

        /// <summary>
        /// Register an instance as a singleton
        /// </summary>
        public void RegisterInstance<TInterface>(TInterface instance) where TInterface : class
        {
            _services.TryAdd(typeof(TInterface), instance);
        }

        /// <summary>
        /// Get a service instance
        /// </summary>
        public TInterface GetService<TInterface>() where TInterface : class
        {
            return (TInterface)GetService(typeof(TInterface));
        }

        /// <summary>
        /// Get a service instance by type
        /// </summary>
        public object GetService(Type serviceType)
        {
            // Try to get existing instance first
            if (_services.TryGetValue(serviceType, out var existingInstance))
                return existingInstance;

            // Try to create using factory
            if (_factories.TryGetValue(serviceType, out var factory))
            {
                try
                {
                    var instance = factory(this);
                    _services.TryAdd(serviceType, instance);
                    return instance;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to create service of type {serviceType.Name}: {ex.Message}", ex);
                }
            }

            throw new InvalidOperationException($"Service of type {serviceType.Name} is not registered. Available services: {string.Join(", ", _factories.Keys)}");
        }

        /// <summary>
        /// Check if a service is registered
        /// </summary>
        public bool IsRegistered<TInterface>()
        {
            return _services.ContainsKey(typeof(TInterface)) || _factories.ContainsKey(typeof(TInterface));
        }

        /// <summary>
        /// Helper method to register MediatR services with their dependencies
        /// </summary>
        public void RegisterMediatRServices(IServiceProvider vsServiceProvider)
        {
            // Register VS service provider first
            RegisterInstance<IServiceProvider>(vsServiceProvider);

            // Core services
            RegisterSingleton<IWorkspaceService, Workspace>();
            RegisterSingleton<IMediatRContextService, MediatRContext>();
            RegisterSingleton<IMediatRCacheService, MediatRCacheService>();
            RegisterSingleton<IMediatRHandlerFinder, MediatRHandlerFinder>();
            RegisterSingleton<IMediatRNavigationService, MediatRNavigationService>();
            RegisterSingleton<INavigationUIService, NavigationUI>();
            RegisterSingleton<IDocumentEventService, DocumentEventsService>();
            
            // Main orchestrator
            RegisterSingleton<IMediatRCommandHandler, MediatRCommandHandler>();

            System.Diagnostics.Debug.WriteLine("Service Container: MediatR services registered successfully");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
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
                            System.Diagnostics.Debug.WriteLine($"Error disposing service {service.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                _services.Clear();
                _factories.Clear();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Static service locator for global access (used by VS extension)
    /// </summary>
    public static class ServiceLocator
    {
        private static ServiceContainer _container;
        private static readonly object _lock = new object();

        public static void Initialize(ServiceContainer container)
        {
            lock (_lock)
            {
                _container?.Dispose();
                _container = container;
            }
        }

        public static TInterface GetService<TInterface>() where TInterface : class
        {
            lock (_lock)
            {
                if (_container == null)
                    throw new InvalidOperationException("ServiceLocator is not initialized. Call Initialize() first.");
                
                return _container.GetService<TInterface>();
            }
        }

        public static void Dispose()
        {
            lock (_lock)
            {
                _container?.Dispose();
                _container = null;
            }
        }
    }
} 