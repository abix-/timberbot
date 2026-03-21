using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;

namespace GameStateBridge
{
    [BepInPlugin("com.claude.gamestatebridge", "GameStateBridge", "0.1.0")]
    [BepInProcess("Timberborn.exe")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; }
        private GameStateHttpServer _server;

        private void Awake()
        {
            Log = base.Logger;
            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
            _server = new GameStateHttpServer(8085);
            Logger.LogInfo("GameStateBridge loaded -- HTTP server on port 8085");
        }

        private void Update()
        {
            _server?.DrainRequests();
        }

        private void OnDestroy()
        {
            _server?.Stop();
        }
    }
}
