// Copyright (c) Xenko contributors (https://xenko.com) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xenko.Core.Annotations;
using Xenko.Core.Diagnostics;
using Xenko.Core.IO;

namespace Xenko.Core.Reflection
{
    public class LoadedAssembly
    {
        public AssemblyContainer Container { get; }
        public string Path { get; }
        public Assembly Assembly { get; }

        public LoadedAssembly([NotNull] AssemblyContainer container, [NotNull] string path, [NotNull] Assembly assembly)
        {
            Container = container;
            Path = path;
            Assembly = assembly;
        }
    }
    public class AssemblyContainer
    {
        [ItemNotNull, NotNull]
        private readonly List<LoadedAssembly> loadedAssemblies = new List<LoadedAssembly>();
        private readonly Dictionary<string, LoadedAssembly> loadedAssembliesByName = new Dictionary<string, LoadedAssembly>(StringComparer.InvariantCultureIgnoreCase);
        private static readonly string[] KnownAssemblyExtensions = { ".dll", ".exe" };
        [ThreadStatic]
        private static AssemblyContainer currentContainer;

        [ThreadStatic]
        private static LoggerResult log;

        [ThreadStatic]
        private static string currentSearchDirectory;

        private static readonly ConditionalWeakTable<Assembly, LoadedAssembly> assemblyToContainers = new ConditionalWeakTable<Assembly, LoadedAssembly>();

        /// <summary>
        /// The default assembly container loader.
        /// </summary>
        public static readonly AssemblyContainer Default = new AssemblyContainer();

        static AssemblyContainer()
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }

        /// <summary>
        /// Gets a copy of the list of loaded assemblies.
        /// </summary>
        /// <value>
        /// The loaded assemblies.
        /// </value>
        [ItemNotNull, NotNull]
        public IList<LoadedAssembly> LoadedAssemblies
        {
            get
            {
                lock (loadedAssemblies)
                {
                    return loadedAssemblies.ToList();
                }
            }
        }

        [CanBeNull]
        public Assembly LoadAssemblyFromPath([NotNull] string assemblyFullPath, ILogger outputLog = null)
        {
            if (assemblyFullPath == null) throw new ArgumentNullException(nameof(assemblyFullPath));

            log = new LoggerResult();

            assemblyFullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, assemblyFullPath));
            var assemblyDirectory = Path.GetDirectoryName(assemblyFullPath);

            if (assemblyDirectory == null || !Directory.Exists(assemblyDirectory))
            {
                throw new ArgumentException("Invalid assembly path. Doesn't contain directory information");
            }

            try
            {
                return LoadAssemblyFromPathInternal(assemblyFullPath);
            }
            finally
            {
                if (outputLog != null)
                {
                    log.CopyTo(outputLog);
                }
            }
        }

        public bool UnloadAssembly([NotNull] Assembly assembly)
        {
            lock (loadedAssemblies)
            {
                var loadedAssembly = loadedAssemblies.FirstOrDefault(x => x.Assembly == assembly);
                if (loadedAssembly == null)
                    return false;

                loadedAssemblies.Remove(loadedAssembly);
                loadedAssembliesByName.Remove(loadedAssembly.Path);
                assemblyToContainers.Remove(assembly);
                return true;
            }
        }

        [CanBeNull]
        private Assembly LoadAssemblyByName([NotNull] string assemblyName, [NotNull] string searchDirectory)
        {
            if (assemblyName == null) throw new ArgumentNullException(nameof(assemblyName));

            var assemblyPartialPathList = new List<string>();
            assemblyPartialPathList.AddRange(KnownAssemblyExtensions.Select(knownExtension => assemblyName + knownExtension));

            foreach (var assemblyPartialPath in assemblyPartialPathList)
            {
                var assemblyFullPath = Path.Combine(searchDirectory, assemblyPartialPath);
                if (File.Exists(assemblyFullPath))
                {
                    return LoadAssemblyFromPathInternal(assemblyFullPath);
                }
            }
            return null;
        }

        [CanBeNull]
        private Assembly LoadAssemblyFromPathInternal([NotNull] string assemblyFullPath)
        {
            if (assemblyFullPath == null) throw new ArgumentNullException(nameof(assemblyFullPath));

            assemblyFullPath = Path.GetFullPath(assemblyFullPath);

            try
            {
                lock (loadedAssemblies)
                {
                    LoadedAssembly loadedAssembly;
                    if (loadedAssembliesByName.TryGetValue(assemblyFullPath, out loadedAssembly))
                    {
                        return loadedAssembly.Assembly;
                    }

                    if (!File.Exists(assemblyFullPath))
                        return null;

                    // Find pdb (if it exists)
                    var pdbFullPath = Path.ChangeExtension(assemblyFullPath, ".pdb");
                    if (!File.Exists(pdbFullPath))
                        pdbFullPath = null;

                    // PreLoad the assembly into memory without locking it
                    var assemblyBytes = File.ReadAllBytes(assemblyFullPath);
                    var pdbBytes = pdbFullPath != null ? File.ReadAllBytes(pdbFullPath) : null;

                    // Load the assembly into the current AppDomain
                    Assembly assembly;
                    if (new UDirectory(AppDomain.CurrentDomain.BaseDirectory) == new UFile(assemblyFullPath).GetFullDirectory())
                    {
                        // If loading from base directory, don't even try to load through byte array, as Assembly.Load will notice there is a "safer" version to load
                        // This happens usually when opening Xenko assemblies themselves
                        assembly = Assembly.LoadFrom(assemblyFullPath);
                    }
                    else
                    {
                        // TODO: Is using AppDomain would provide more opportunities for unloading?
                        assembly = pdbBytes != null ? Assembly.Load(assemblyBytes, pdbBytes) : Assembly.Load(assemblyBytes);
                        loadedAssembly = new LoadedAssembly(this, assemblyFullPath, assembly);
                        loadedAssemblies.Add(loadedAssembly);
                        loadedAssembliesByName.Add(assemblyFullPath, loadedAssembly);

                        // Force assembly resolve with proper name (with proper context)
                        var previousSearchDirectory = currentSearchDirectory;
                        var previousContainer = currentContainer;
                        try
                        {
                            currentContainer = this;
                            currentSearchDirectory = Path.GetDirectoryName(assemblyFullPath);

                            Assembly.Load(assembly.FullName);
                        }
                        finally
                        {
                            currentContainer = previousContainer;
                            currentSearchDirectory = previousSearchDirectory;
                        }
                    }

                    // Make sure there is no duplicate
                    Debug.Assert(!assemblyToContainers.TryGetValue(assembly, out var _));
                    // Add to mapping
                    assemblyToContainers.GetValue(assembly, _ => loadedAssembly);

                    // Make sure that Module initializer are called
                    foreach (var module in assembly.Modules)
                    {
                        RuntimeHelpers.RunModuleConstructor(module.ModuleHandle);
                    }
                    return assembly;
                }
            }
            catch (Exception exception)
            {
                log.Error($"Error while loading assembly reference [{assemblyFullPath}]", exception);
                var loaderException = exception as ReflectionTypeLoadException;
                if (loaderException != null)
                {
                    foreach (var exceptionForType in loaderException.LoaderExceptions)
                    {
                        log.Error("Unable to load type. See exception.", exceptionForType);
                    }
                }
            }
            return null;
        }

        [CanBeNull]
        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            // If it is handled by current thread, then we can handle it here.
            var container = currentContainer;
            string searchDirectory = currentSearchDirectory;

            // If it's a dependent assembly loaded later, find container and path
            if (container == null && args.RequestingAssembly != null && assemblyToContainers.TryGetValue(args.RequestingAssembly, out var loadedAssembly))
            {
                // Assembly reference requested after initial resolve, we need to setup context temporarily
                container = loadedAssembly.Container;
                searchDirectory = Path.GetDirectoryName(loadedAssembly.Path);
            }

            // Load assembly
            if (container != null)
            {
                var assemblyName = new AssemblyName(args.Name);
                return container.LoadAssemblyByName(assemblyName.Name, searchDirectory);
            }

            return null;
        }
    }
}
