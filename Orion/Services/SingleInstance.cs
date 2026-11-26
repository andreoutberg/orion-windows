using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orion
{
    /// <summary>
    /// One Orion process per profile: a named mutex declares ownership of a
    /// profile, and a named pipe lets a would-be duplicate process forward an
    /// "open" request to the owner instead of starting a second instance.
    /// </summary>
    internal static class SingleInstance
    {
        // Rooted for process lifetime: the kernel mutex IS the single-instance
        // claim. If this object were collected, its finalizer would close the
        // handle, the OS would destroy the named mutex, and a second process
        // could acquire the profile while this one still runs.
        private static Mutex? _ownerMutex;
        private static volatile bool _stopping;

        private static readonly string ProfileScope = GetProfileScope();

        private static string GetProfileScope()
        {
            const string profileScope = "Orion.com.kagi.orion.Profile";
            if (!PrivateBrowsing.IsActive && Environment.GetEnvironmentVariable("ORION_DESIGN_PREVIEW") != "1")
                return profileScope;

            string path = Path.GetFullPath(Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            string fingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(path).ToUpperInvariant())));
            return $"{profileScope}.Preview.{fingerprint[..16]}";
        }

        private static string MutexName(long profileId)
            => $@"Local\{ProfileScope}.{profileId}";

        private static string PipeName(long profileId)
            => $"{ProfileScope}.{profileId}";

        /// <summary>
        /// Returns true when this process now owns the profile. Returns false
        /// when another process already owns it — in that case the running
        /// instance has been asked to open a window and the caller should
        /// exit immediately.
        /// </summary>
        public static bool TryAcquire(long profileId)
        {
            _ownerMutex = new Mutex(true, MutexName(profileId), out bool createdNew);
            if (createdNew)
                return true;

            try
            {
                // We are (about to have been) the foreground process; without
                // this grant Windows would deny the owner's SetForegroundWindow
                // and its window would only flash in the taskbar.
                _ = AllowSetForegroundWindow(ASFW_ANY);
                using var client = new NamedPipeClientStream(
                    ".", PipeName(profileId), PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.WriteLine("open");
                writer.Flush();
            }
            catch (Exception ex)
            {
                OrionBackend.LogWarning(
                    $"SingleInstance: could not signal the running instance: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Waits until no process owns the profile; the OS releases the mutex
        /// when the owner terminates, even on a crash. True when the profile
        /// is gone within the timeout. Note the owner releases its claim
        /// early in shutdown (Stop()), before its file handles — callers that
        /// touch the profile's files must tolerate a short residual lock.
        /// </summary>
        public static bool WaitForExit(long profileId, TimeSpan timeout)
        {
            Mutex mutex;
            try
            {
                mutex = Mutex.OpenExisting(MutexName(profileId));
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return true;
            }
            using (mutex)
            {
                try
                {
                    if (!mutex.WaitOne(timeout))
                        return false;
                }
                catch (AbandonedMutexException)
                {
                    // Owner terminated without releasing — it is gone, and
                    // the wait granted us ownership all the same.
                }
                mutex.ReleaseMutex();
                return true;
            }
        }

        /// <summary>True when a process currently owns the profile.</summary>
        public static bool IsRunning(long profileId)
        {
            try
            {
                using var owner = Mutex.OpenExisting(MutexName(profileId));
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Tells the running instance of <paramref name="profileId"/> (if any)
        /// that its profile was deleted, so it closes its windows and exits.
        /// Returns false when no process owns the profile.
        /// </summary>
        public static bool NotifyProfileDeleted(long profileId)
        {
            if (!IsRunning(profileId))
                return false;
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", PipeName(profileId), PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.WriteLine("profile-deleted");
                writer.Flush();
            }
            catch (Exception ex)
            {
                OrionBackend.LogWarning(
                    $"SingleInstance: could not notify profile {profileId} of deletion: {ex.Message}");
            }
            return true;
        }

        /// <summary>
        /// Notifies every other profile's running process that the profile
        /// registry changed, so their profile UIs refresh (the macOS analog
        /// is the OrionProfilesDidUpdate distributed notification).
        /// </summary>
        public static void BroadcastProfilesChanged()
        {
            long self = OrionBackend.GetCurrentProfileId();
            foreach (Profile p in OrionBackend.GetProfiles())
            {
                if (p.Id == self)
                    continue;
                try
                {
                    using var owner = Mutex.OpenExisting(MutexName(p.Id));
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    continue;  // profile not running
                }
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".", PipeName(p.Id), PipeDirection.Out);
                    client.Connect(500);
                    using var writer = new StreamWriter(client);
                    writer.WriteLine("profiles-changed");
                    writer.Flush();
                }
                catch (Exception ex)
                {
                    OrionBackend.LogWarning(
                        $"SingleInstance: profiles-changed to {p.Id} failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Starts the request listener for the owning process; the callbacks
        /// run on the UI thread once per forwarded request. "quit" arrives
        /// from another profile's process whose user confirmed Quit Orion —
        /// quit without asking again. <paramref name="onNotify"/> receives the
        /// payload of each forwarded notification activation ("notify <payload>",
        /// see <see cref="ForwardNotificationActivation"/>).
        /// </summary>
        public static void StartServer(long profileId, DispatcherQueue dispatcher, Action onOpen,
            Action onProfileDeleted, Action onProfilesChanged, Action onQuit,
            Action<string>? onNotify = null)
        {
            var thread = new Thread(() =>
            {
                while (!_stopping)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName(profileId), PipeDirection.In, maxNumberOfServerInstances: 1);
                        server.WaitForConnection();
                        using var reader = new StreamReader(server);
                        string? request = reader.ReadLine();
                        Action? handler = request switch
                        {
                            "open" => onOpen,
                            "profile-deleted" => onProfileDeleted,
                            "profiles-changed" => onProfilesChanged,
                            "quit" => onQuit,
                            _ => null,
                        };
                        if (handler != null)
                        {
                            _ = dispatcher.TryEnqueue(() => handler());
                        }
                        else if (request != null && onNotify != null
                                 && request.StartsWith(NotifyPrefix, StringComparison.Ordinal))
                        {
                            string payload = request[NotifyPrefix.Length..];
                            _ = dispatcher.TryEnqueue(() => onNotify(payload));
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_stopping)
                            break;
                        OrionBackend.LogWarning($"SingleInstance: pipe server error: {ex.Message}");
                        Thread.Sleep(500);  // avoid a tight loop on persistent failure
                    }
                }
            })
            { IsBackground = true, Name = "OrionSingleInstance" };
            thread.Start();
        }

        private const string NotifyPrefix = "notify ";

        /// <summary>
        /// Hands a notification activation that arrived in this process to the
        /// process owning <paramref name="profileId"/> (see NotificationService).
        /// Any thread. Dropped with a warning when that profile is not running.
        /// </summary>
        public static void ForwardNotificationActivation(long profileId, string payload)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", PipeName(profileId), PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.WriteLine(NotifyPrefix + payload);
                writer.Flush();
            }
            catch (Exception ex)
            {
                OrionBackend.LogWarning(
                    $"SingleInstance: could not forward a notification to profile {profileId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Asks the process owning <paramref name="profileId"/> (if any) to
        /// quit without further confirmation. Returns false when no process
        /// owns that profile.
        /// </summary>
        public static bool SendQuit(long profileId)
        {
            // The mutex is the ownership claim — checking it first avoids a
            // pipe-connect timeout for every profile that isn't running.
            if (!IsRunning(profileId))
                return false;

            try
            {
                using var client = new NamedPipeClientStream(
                    ".", PipeName(profileId), PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.WriteLine("quit");
                writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                OrionBackend.LogWarning(
                    $"SingleInstance: could not send quit to profile {profileId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the listener and releases the profile claim so a new process
        /// can take ownership immediately (rather than after process exit).
        /// </summary>
        public static void Stop()
        {
            _stopping = true;
            _ownerMutex?.Dispose();
            _ownerMutex = null;
        }

        private const uint ASFW_ANY = unchecked((uint)-1);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);
    }
}
