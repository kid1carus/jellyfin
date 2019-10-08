using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;
using static MediaBrowser.Common.HexHelper;

namespace Emby.Server.Implementations.Updates
{
    /// <summary>
    /// Manages all install, uninstall and update operations (both plugins and system)
    /// </summary>
    public class InstallationManager : IInstallationManager
    {
        public event EventHandler<InstallationEventArgs> PackageInstalling;
        public event EventHandler<InstallationEventArgs> PackageInstallationCompleted;
        public event EventHandler<InstallationFailedEventArgs> PackageInstallationFailed;
        public event EventHandler<InstallationEventArgs> PackageInstallationCancelled;

        /// <summary>
        /// The current installations
        /// </summary>
        private List<(InstallationInfo info, CancellationTokenSource token)> _currentInstallations { get; set; }

        /// <summary>
        /// The completed installations
        /// </summary>
        private ConcurrentBag<InstallationInfo> _completedInstallationsInternal;

        public IEnumerable<InstallationInfo> CompletedInstallations => _completedInstallationsInternal;

        /// <summary>
        /// Occurs when [plugin uninstalled].
        /// </summary>
        public event EventHandler<GenericEventArgs<IPlugin>> PluginUninstalled;

        /// <summary>
        /// Occurs when [plugin updated].
        /// </summary>
        public event EventHandler<GenericEventArgs<(IPlugin, PackageVersionInfo)>> PluginUpdated;

        /// <summary>
        /// Occurs when [plugin updated].
        /// </summary>
        public event EventHandler<GenericEventArgs<PackageVersionInfo>> PluginInstalled;

        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;

        private readonly IApplicationPaths _appPaths;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IServerConfigurationManager _config;
        private readonly IFileSystem _fileSystem;

        /// <summary>
        /// Gets the application host.
        /// </summary>
        /// <value>The application host.</value>
        private readonly IApplicationHost _applicationHost;

        private readonly IZipClient _zipClient;

        public InstallationManager(
            ILogger<InstallationManager> logger,
            IApplicationHost appHost,
            IApplicationPaths appPaths,
            IHttpClient httpClient,
            IJsonSerializer jsonSerializer,
            IServerConfigurationManager config,
            IFileSystem fileSystem,
            IZipClient zipClient)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _currentInstallations = new List<(InstallationInfo, CancellationTokenSource)>();
            _completedInstallationsInternal = new ConcurrentBag<InstallationInfo>();

            _logger = logger;
            _applicationHost = appHost;
            _appPaths = appPaths;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _config = config;
            _fileSystem = fileSystem;
            _zipClient = zipClient;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PackageInfo>> GetAvailablePackages(CancellationToken cancellationToken = default)
        {
            using (var response = await _httpClient.SendAsync(
                new HttpRequestOptions
                {
                    Url = "https://repo.jellyfin.org/releases/plugin/manifest.json",
                    CancellationToken = cancellationToken,
                    CacheMode = CacheMode.Unconditional,
                    CacheLength = TimeSpan.FromMinutes(3)
                },
                HttpMethod.Get).ConfigureAwait(false))
            using (Stream stream = response.Content)
            {
                return await _jsonSerializer.DeserializeFromStreamAsync<IReadOnlyList<PackageInfo>>(
                    stream).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public IEnumerable<PackageInfo> FilterPackages(
            IEnumerable<PackageInfo> availablePackages,
            string name = null,
            Guid guid = default)
        {
            if (name != null)
            {
                availablePackages = availablePackages.Where(x => x.name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            if (guid != Guid.Empty)
            {
                var strGuid = guid.ToString("N", CultureInfo.InvariantCulture);
                availablePackages = availablePackages.Where(x => x.guid.Equals(strGuid, StringComparison.OrdinalIgnoreCase));
            }

            return availablePackages;
        }

        /// <inheridoc />
        public IEnumerable<PackageVersionInfo> GetCompatibleVersions(
            IEnumerable<PackageVersionInfo> availableVersions,
            Version minVersion = null,
            PackageVersionClass classification = PackageVersionClass.Release)
        {
            var appVer = _applicationHost.ApplicationVersion;
            availableVersions = availableVersions.Where(x => Version.Parse(x.requiredVersionStr) <= appVer);

            if (minVersion != null)
            {
                availableVersions = availableVersions.Where(x => x.Version >= minVersion);
            }

            return availableVersions;
        }

        /// <inheritdoc />
        public IEnumerable<PackageVersionInfo> GetCompatibleVersions(
            IEnumerable<PackageInfo> availablePackages,
            string name = null,
            Guid guid = default,
            Version minVersion = null,
            PackageVersionClass classification = PackageVersionClass.Release)
        {
            var package = FilterPackages(availablePackages, name, guid).FirstOrDefault();

            // Package not found.
            if (package == null)
            {
                return null;
            }

            return GetCompatibleVersions(
                package.versions,
                minVersion,
                classification);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<PackageVersionInfo>> GetAvailablePluginUpdates(CancellationToken cancellationToken = default)
        {
            var catalog = await GetAvailablePackages(cancellationToken).ConfigureAwait(false);

            var systemUpdateLevel = _applicationHost.SystemUpdateLevel;

            // Figure out what needs to be installed
            return _applicationHost.Plugins.Select(x =>
            {
                var compatibleversions = GetCompatibleVersions(catalog, x.Name, x.Id, x.Version, systemUpdateLevel);
                return compatibleversions.FirstOrDefault(y => y.Version > x.Version);
            }).Where(x => x != null)
            .Where(x => !CompletedInstallations.Any(y => string.Equals(y.AssemblyGuid, x.guid, StringComparison.OrdinalIgnoreCase)));
        }

        /// <inheritdoc />
        public async Task InstallPackage(PackageVersionInfo package, CancellationToken cancellationToken)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            var installationInfo = new InstallationInfo
            {
                Id = Guid.NewGuid(),
                Name = package.name,
                AssemblyGuid = package.guid,
                UpdateClass = package.classification,
                Version = package.versionStr
            };

            var innerCancellationTokenSource = new CancellationTokenSource();

            var tuple = (installationInfo, innerCancellationTokenSource);

            // Add it to the in-progress list
            lock (_currentInstallations)
            {
                _currentInstallations.Add(tuple);
            }

            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, innerCancellationTokenSource.Token).Token;

            var installationEventArgs = new InstallationEventArgs
            {
                InstallationInfo = installationInfo,
                PackageVersionInfo = package
            };

            PackageInstalling?.Invoke(this, installationEventArgs);

            try
            {
                await InstallPackageInternal(package, linkedToken).ConfigureAwait(false);

                lock (_currentInstallations)
                {
                    _currentInstallations.Remove(tuple);
                }

                _completedInstallationsInternal.Add(installationInfo);

                PackageInstallationCompleted?.Invoke(this, installationEventArgs);
            }
            catch (OperationCanceledException)
            {
                lock (_currentInstallations)
                {
                    _currentInstallations.Remove(tuple);
                }

                _logger.LogInformation("Package installation cancelled: {0} {1}", package.name, package.versionStr);

                PackageInstallationCancelled?.Invoke(this, installationEventArgs);

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Package installation failed");

                lock (_currentInstallations)
                {
                    _currentInstallations.Remove(tuple);
                }

                PackageInstallationFailed?.Invoke(this, new InstallationFailedEventArgs
                {
                    InstallationInfo = installationInfo,
                    Exception = ex
                });

                throw;
            }
            finally
            {
                // Dispose the progress object and remove the installation from the in-progress list
                tuple.innerCancellationTokenSource.Dispose();
            }
        }

        /// <summary>
        /// Installs the package internal.
        /// </summary>
        /// <param name="package">The package.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see cref="Task" />.</returns>
        private async Task InstallPackageInternal(PackageVersionInfo package, CancellationToken cancellationToken)
        {
            // Set last update time if we were installed before
            IPlugin plugin = _applicationHost.Plugins.FirstOrDefault(p => string.Equals(p.Id.ToString(), package.guid, StringComparison.OrdinalIgnoreCase))
                           ?? _applicationHost.Plugins.FirstOrDefault(p => p.Name.Equals(package.name, StringComparison.OrdinalIgnoreCase));

            // Do the install
            await PerformPackageInstallation(package, cancellationToken).ConfigureAwait(false);

            // Do plugin-specific processing
            if (plugin == null)
            {
                _logger.LogInformation("New plugin installed: {0} {1} {2}", package.name, package.versionStr ?? string.Empty, package.classification);

                PluginInstalled?.Invoke(this, new GenericEventArgs<PackageVersionInfo>(package));
            }
            else
            {
                _logger.LogInformation("Plugin updated: {0} {1} {2}", package.name, package.versionStr ?? string.Empty, package.classification);

                PluginUpdated?.Invoke(this, new GenericEventArgs<(IPlugin, PackageVersionInfo)>((plugin, package)));
            }

            _applicationHost.NotifyPendingRestart();
        }

        private async Task PerformPackageInstallation(PackageVersionInfo package, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(package.targetFilename);
            if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Only zip packages are supported. {Filename} is not a zip archive.", package.targetFilename);
                return;
            }

            // Always override the passed-in target (which is a file) and figure it out again
            string targetDir = Path.Combine(_appPaths.PluginsPath, package.name);

// CA5351: Do Not Use Broken Cryptographic Algorithms
#pragma warning disable CA5351
            using (var res = await _httpClient.SendAsync(
                new HttpRequestOptions
                {
                    Url = package.sourceUrl,
                    CancellationToken = cancellationToken,
                    // We need it to be buffered for setting the position
                    BufferContent = true
                },
                HttpMethod.Get).ConfigureAwait(false))
            using (var stream = res.Content)
            using (var md5 = MD5.Create())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hash = ToHexString(md5.ComputeHash(stream));
                if (!string.Equals(package.checksum, hash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "The checksums didn't match while installing {Package}, expected: {Expected}, got: {Received}",
                        package.name,
                        package.checksum,
                        hash);
                    throw new InvalidDataException("The checksum of the received data doesn't match.");
                }

                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                }

                stream.Position = 0;
                _zipClient.ExtractAllFromZip(stream, targetDir, true);
            }

#pragma warning restore CA5351
        }

        /// <summary>
        /// Uninstalls a plugin
        /// </summary>
        /// <param name="plugin">The plugin.</param>
        public void UninstallPlugin(IPlugin plugin)
        {
            plugin.OnUninstalling();

            // Remove it the quick way for now
            _applicationHost.RemovePlugin(plugin);

            var path = plugin.AssemblyFilePath;
            bool isDirectory = false;
            // Check if we have a plugin directory we should remove too
            if (Path.GetDirectoryName(plugin.AssemblyFilePath) != _appPaths.PluginsPath)
            {
                path = Path.GetDirectoryName(plugin.AssemblyFilePath);
                isDirectory = true;
            }

            // Make this case-insensitive to account for possible incorrect assembly naming
            var file = _fileSystem.GetFilePaths(Path.GetDirectoryName(path))
                .FirstOrDefault(i => string.Equals(i, path, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(file))
            {
                path = file;
            }

            if (isDirectory)
            {
                _logger.LogInformation("Deleting plugin directory {0}", path);
                Directory.Delete(path, true);
            }
            else
            {
                _logger.LogInformation("Deleting plugin file {0}", path);
                _fileSystem.DeleteFile(path);
            }

            var list = _config.Configuration.UninstalledPlugins.ToList();
            var filename = Path.GetFileName(path);
            if (!list.Contains(filename, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(filename);
                _config.Configuration.UninstalledPlugins = list.ToArray();
                _config.SaveConfiguration();
            }

            PluginUninstalled?.Invoke(this, new GenericEventArgs<IPlugin> { Argument = plugin });

            _applicationHost.NotifyPendingRestart();
        }

        /// <inheritdoc/>
        public bool CancelInstallation(Guid id)
        {
            lock (_currentInstallations)
            {
                var install = _currentInstallations.Find(x => x.info.Id == id);
                if (install == default((InstallationInfo, CancellationTokenSource)))
                {
                    return false;
                }

                install.token.Cancel();
                _currentInstallations.Remove(install);
                return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                lock (_currentInstallations)
                {
                    foreach (var tuple in _currentInstallations)
                    {
                        tuple.token.Dispose();
                    }

                    _currentInstallations.Clear();
                }
            }
        }
    }
}
