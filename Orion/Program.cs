#if DISABLE_XAML_GENERATED_MAIN
#if !WINAPPSDKSELFCONTAINED && !PACKAGED
using Microsoft.Windows.ApplicationModel.DynamicDependency;
#endif

namespace Orion
{
    public static class Program
    {
        internal static string LauncherPath => System.IO.Path.Combine(System.AppContext.BaseDirectory, "Orion.exe");

        private static void StartBroker(string path, string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false };
            foreach (string argument in args)
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start);
        }

        public static void Main(string[] args)
        {
            // Chromium's sandbox requires its broker and children to use the
            // same CEF bootstrap image. Blink-only and private launches can
            // delegate immediately, before touching profile storage.
            string broker = System.IO.Path.Combine(System.AppContext.BaseDirectory,
                "engines", "blink", "OrionEngineB.exe");
            bool isBroker = string.Equals(System.Environment.ProcessPath, broker,
                System.StringComparison.OrdinalIgnoreCase);
            if (!isBroker && (!App.SupportsWebKit || System.Environment.GetEnvironmentVariable("ORION_PRIVATE_SESSION") == "1"))
            {
                StartBroker(broker, args);
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();

            OrionBackend.Initialize();

            PrivateBrowsing.Initialize();

            // One profile per process: resolve which profile this process
            // hosts (running the first-launch registry migration if needed),
            // then either claim it or forward to the instance that owns it.
            long requestedProfile = 0;
            foreach (string a in args)
            {
                if (a.StartsWith("--profile=", System.StringComparison.Ordinal)
                    && long.TryParse(a["--profile=".Length..], out long parsed))
                {
                    requestedProfile = parsed;
                }
            }
            // A cold start (no --profile) reopens the profiles that were open
            // when the user last quit Orion (one process per profile): the
            // profile focused at quit hosts this process, the rest are spawned
            // by App.OnLaunched. The list is written by the quit path and
            // consumed here exactly once, so profiles closed individually
            // later don't come back.
            bool consumeReopenList = false;
            if (requestedProfile == 0)
            {
                // Opens/migrates the registry DB so GetAppSetting works.
                _ = OrionBackend.ResolveStartupProfile(0);
                string reopenCsv = OrionBackend.GetAppSetting(
                    "settings::general::reopen_profiles", "");
                if (reopenCsv.Length > 0)
                {
                    consumeReopenList = true;
                    foreach (string part in reopenCsv.Split(','))
                    {
                        // Skip ids whose profile no longer exists.
                        if (!long.TryParse(part, out long id)
                            || OrionBackend.ResolveStartupProfile(id) != id)
                        {
                            continue;
                        }
                        if (requestedProfile == 0)
                            requestedProfile = id;
                        else if (id != requestedProfile && !App.ReopenProfileIds.Contains(id))
                            App.ReopenProfileIds.Add(id);
                    }
                }
            }

            long profileId = OrionBackend.ResolveStartupProfile(requestedProfile);
            if (profileId == 0)
                profileId = requestedProfile;  // InitOrionCore will surface the error
            if (!isBroker && App.RequestedEngine(
                OrionBackend.GetProfiles().Find(profile => profile.Id == profileId), args) == "blink")
            {
                StartBroker(broker, args);
                return;
            }
            if (consumeReopenList)
                OrionBackend.SetAppSetting("settings::general::reopen_profiles", "");
            App.StartupProfileId = profileId;
            if (!SingleInstance.TryAcquire(profileId))
                return;

            System.Threading.Thread.CurrentThread.SetApartmentState(System.Threading.ApartmentState.Unknown);
            System.Threading.Thread.CurrentThread.SetApartmentState(System.Threading.ApartmentState.STA);

#if !WINAPPSDKSELFCONTAINED && !PACKAGED
            bool bootstrapInitialized = Bootstrap.TryInitialize(0x00010008, out int hresult);

            if (!bootstrapInitialized)
            {
                throw new System.Exception(
                    $"Failed to initialize Windows App SDK. HRESULT: 0x{hresult:X8}. " +
                    "Install the Windows App SDK 1.8 runtime from https://aka.ms/windowsappsdk");
            }
#endif

#if PACKAGED
            try
            {
#endif
            Microsoft.UI.Xaml.Application.Start(p =>
                {
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });
#if PACKAGED
            }
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.HResult == unchecked((int)0x80040154)) // REGDB_E_CLASSNOTREG
            {
                throw new System.InvalidOperationException(
                    "WinRT classes not registered — the MSIX package hasn't been deployed. " +
                    "In Visual Studio, set 'OrionPackage' (not 'Orion') as the startup project.", ex);
            }
#endif

#if !WINAPPSDKSELFCONTAINED && !PACKAGED
            Bootstrap.Shutdown();
#endif
        }
    }
}
#endif
