using Android.App;
using Android.OS;
using Android.Util;

namespace RecursiveAI.OneLite.AndroidSmoke;

/// <summary>Runs the shared smoke checks once on a worker thread and logs one line, "mobile smoke: ok" or "mobile smoke: FAILED", under the tag
/// OneLiteSmoke, then closes. There is no UI.</summary>
[Activity(Label = "OneLite smoke", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    private const string Tag = "OneLiteSmoke";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var thread = new Thread(() =>
        {
            int exit;
            try
            {
                exit = RecursiveAI.OneLite.AotSmoke.Program.Main();
            }
            catch (Exception e)
            {
                Log.Error(Tag, "unhandled: " + e);
                exit = 1;
            }

            Log.Info(Tag, exit == 0 ? "mobile smoke: ok" : "mobile smoke: FAILED");
            RunOnUiThread(Finish);
        }, 16 * 1024 * 1024);
        thread.Start();
    }
}
