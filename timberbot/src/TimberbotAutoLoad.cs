// TimberbotAutoLoad.cs. Auto-load a save at the main menu.
//
// Reads autoload.json from the mod folder (Documents/Timberborn/Mods/Timberbot/):
//   { "settlement": "Potato Tomato", "save": "Potato Tomato (15)" }
//
// If the file exists, loads the save and deletes the file (one-shot).
// If "save" is omitted, picks the most recent save in the settlement.
// Falls back to CLI args --tb-settlement / --tb-save for backwards compat.
//
// Uses ValidatingGameLoader.LoadGame(). same path as clicking Continue/Load in the UI.
// Runs as ILoadableSingleton in [Context("MainMenu")] so it fires once when the menu loads.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSaveRepositorySystemUI;
using Timberborn.PlatformUtilities;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace Timberbot
{
    public class TimberbotAutoLoad : ILoadableSingleton
    {
        private readonly GameSaveRepository _gameSaveRepository;
        private readonly ValidatingGameLoader _validatingGameLoader;

        private static readonly string ModDir = TimberbotPaths.ModDir;

        public TimberbotAutoLoad(
            GameSaveRepository gameSaveRepository,
            ValidatingGameLoader validatingGameLoader)
        {
            _gameSaveRepository = gameSaveRepository;
            _validatingGameLoader = validatingGameLoader;
        }

        public void Load()
        {
            try
            {
                string settlement = null;
                string saveName = null;

                // try autoload.json first (written by timberbot.py launch)
                var autoloadPath = Path.Combine(ModDir, "autoload.json");
                if (File.Exists(autoloadPath))
                {
                    var json = JObject.Parse(File.ReadAllText(autoloadPath));
                    settlement = json.Value<string>("settlement");
                    saveName = json.Value<string>("save");
                    File.Delete(autoloadPath);
                    Debug.Log($"[Timberbot] autoload.json: settlement={settlement} save={saveName}");
                }

                // fallback to CLI args
                if (settlement == null)
                {
                    var args = Environment.GetCommandLineArgs();
                    settlement = GetArg(args, "--tb-settlement");
                }
                if (settlement == null)
                    return;

                if (saveName == null)
                {
                    var args = Environment.GetCommandLineArgs();
                    saveName = GetArg(args, "--tb-save");
                }

                string saveDir = Path.Combine(UserDataFolder.Folder, "Saves");
                var settlementRef = new SettlementReference(settlement, saveDir);

                SaveReference saveRef;
                if (saveName != null)
                {
                    saveRef = new SaveReference(saveName, settlementRef);
                }
                else
                {
                    saveRef = _gameSaveRepository.GetSaves(settlementRef).FirstOrDefault();
                    if (saveRef == null)
                    {
                        Debug.LogError($"[Timberbot] no saves found for settlement '{settlement}'");
                        return;
                    }
                }

                if (!_gameSaveRepository.SaveExists(saveRef))
                {
                    Debug.LogError($"[Timberbot] save not found: {saveRef}");
                    return;
                }

                Debug.Log($"[Timberbot] auto-loading: {saveRef}");
                _validatingGameLoader.LoadGame(saveRef);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Timberbot] auto-load failed: {ex}");
            }
        }

        private static string GetArg(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
