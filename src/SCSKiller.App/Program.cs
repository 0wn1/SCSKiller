using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace SCSKiller.App;

/// <summary>Our own entry point (DISABLE_XAML_GENERATED_MAIN): one SCSKiller per user session. A second launch (the
/// shortcut clicked while the app waits in the notification area, the sign-in entry, the driver-update task's
/// --driver-updated) hands its activation to the running instance (App.OnActivated) and exits. Design-data runs
/// (--fake, --screenshots) never register nor redirect: they must not reach a real running instance.</summary>
public static class Program
{
    public const string InstanceKey = "SCSKiller";

    [STAThread]
    static int Main(string[] args)
    {
        Updater.RunHooks();   // first: Update.exe runs install/update/uninstall hooks through here, and they exit
        // an offline session's cleanup helper: no window, no instance of its own
        if (args is [Core.App.ScsKiller.CleanupArg, var game]) return Core.App.ScsKiller.RunOfflineCleanup(new Core.App.AppStore(Core.App.AppStore.DefaultDir), game, exe: Environment.ProcessPath);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (!IsDesignRun(args) && RedirectedToRunning()) return 0;
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }

    public static bool IsDesignRun(string[] args) => args.Any(a => a is "--fake" or "--screenshots");

    static bool RedirectedToRunning()
    {
        var key = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (key.IsCurrent) return false;
        AllowSetForegroundWindow(key.ProcessId);   // its window may come to the front: this launch has the user's click
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEvent(false);
        // On another thread: awaiting the redirect on this STA thread hangs. WaitOne pumps COM meanwhile.
        Task.Run(async () =>
        {
            try { await key.RedirectActivationToAsync(activation); }
            finally { done.Set(); }
        });
        done.WaitOne(TimeSpan.FromSeconds(30));
        return true;
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
}
