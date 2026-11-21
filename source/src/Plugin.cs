using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace ScopeHandjob
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        // Public release "Scope Handjob": left hand only, no pose editor / tuning tools, the poses built in.
        public const string Guid = "scopehandjob.spt416";
        public const string Name = "Scope Handjob";
        public const string Version = "1.0.1";
        internal const string LogTag = "[ScopeHandjob]";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            try
            {
                Patches.Install(new Harmony(Guid));
                Log.LogInfo(LogTag + " " + Version + " ready, " + PoseStore.Count + " scope poses");
            }
            catch (Exception error)
            {
                Log.LogError(LogTag + " not active, a game method could not be hooked: " + error.Message);
            }
        }

        private void Start() => Settings.FindDevBuild(); // every plugin has loaded by now

        private void Update() => StanceSupport.Tick();
    }
}
